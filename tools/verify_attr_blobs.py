#!/usr/bin/env python3
"""
verify_attr_blobs.py - Valida los blobs de atributos personalizados de un assembly .NET.

POR QUE EXISTE ESTE CHEQUEO
---------------------------
El compilador de Mono (mcs) codifica MAL la longitud de un string dentro del blob de un
atributo personalizado cuando ese string mide mas de 16383 bytes. El formato de longitud
comprimida de ECMA-335 usa:

    1 byte   para 0      .. 127        (0xxxxxxx)
    2 bytes  para 128    .. 16383      (10xxxxxx xxxxxxxx)   <- maximo 0x3FFF
    4 bytes  para 16384  .. 536870911  (110xxxxx ...)

Pasado 16383, mcs sigue escribiendo el formato de 2 bytes con un valor truncado. El blob
queda inconsistente y .NET Framework lanza System.Reflection.CustomAttributeFormatException
al leerlo. En un plugin de XrmToolBox eso ocurre durante la composicion MEF al arrancar
(CompositionServices.TryExportMetadataForMember) y tumba la carga de TODOS los plugins.

Caso real: ExportMetadata("BigImageBase64", "<16774 caracteres>") en Metadata Dataverse
Document. Sintoma: "Formato binario del atributo personal especificado no valido" al abrir
XrmToolBox, y ningun plugin cargaba.

USO
    python3 verify_attr_blobs.py <ruta.dll> [mas.dll ...]

Codigo de salida 0 si todo esta bien, 1 si hay algun blob invalido.
"""
import sys

try:
    import dnfile
except ImportError:
    print("Falta dnfile:  pip install dnfile --break-system-packages")
    sys.exit(2)

LIMITE_2_BYTES = 0x3FFF  # 16383

# Tamanos fijos de los tipos primitivos dentro de un blob de atributo (ECMA-335 II.23.3)
TAM = {0x02: 1, 0x03: 2, 0x04: 1, 0x05: 1, 0x06: 2, 0x07: 2,
       0x08: 4, 0x09: 4, 0x0A: 8, 0x0B: 8, 0x0C: 4, 0x0D: 8}


def leer_longitud(b, p):
    """Devuelve (nueva_pos, longitud, bytes_usados) de un entero comprimido."""
    if p >= len(b):
        raise ValueError("fin de blob al leer una longitud")
    x = b[p]
    if x & 0x80 == 0:
        return p + 1, x, 1
    if x & 0xC0 == 0x80:
        if p + 1 >= len(b):
            raise ValueError("longitud de 2 bytes truncada")
        return p + 2, ((x & 0x3F) << 8) | b[p + 1], 2
    if x & 0xE0 == 0xC0:
        if p + 3 >= len(b):
            raise ValueError("longitud de 4 bytes truncada")
        return p + 4, ((x & 0x1F) << 24) | (b[p + 1] << 16) | (b[p + 2] << 8) | b[p + 3], 4
    raise ValueError("byte de longitud invalido 0x%02x" % x)


def leer_serstring(b, p):
    if p < len(b) and b[p] == 0xFF:
        return p + 1, None
    p, ln, nbytes = leer_longitud(b, p)
    if p + ln > len(b):
        # El sintoma exacto del bug de Mono.
        disponible = len(b) - p
        extra = ""
        if nbytes == 2 and disponible > LIMITE_2_BYTES:
            extra = ("  <-- el string real mide %d B, excede el maximo de 2 bytes (%d) "
                     "y fue codificado en 2 bytes: BUG DE CODIFICACION"
                     % (disponible, LIMITE_2_BYTES))
        raise ValueError("string declarado de %d B pero solo hay %d B en el blob%s"
                         % (ln, disponible, extra))
    return p + ln, b[p:p + ln]


def leer_valor(b, p):
    """Lee un valor 'boxed' (tag de tipo + payload)."""
    if p >= len(b):
        raise ValueError("falta el tag del valor")
    tag = b[p]
    p += 1
    if tag == 0x0E:
        return leer_serstring(b, p)[0]
    if tag in TAM:
        if p + TAM[tag] > len(b):
            raise ValueError("valor primitivo 0x%02x truncado" % tag)
        return p + TAM[tag]
    if tag == 0x55:                      # enum: nombre del tipo + valor i4
        p = leer_serstring(b, p)[0]
        if p + 4 > len(b):
            raise ValueError("valor de enum truncado")
        return p + 4
    if tag == 0x50:                      # System.Type
        return leer_serstring(b, p)[0]
    if tag == 0x1D:                      # SZARRAY
        if p >= len(b):
            raise ValueError("array sin tag de elemento")
        et = b[p]; p += 1
        if p + 4 > len(b):
            raise ValueError("array sin conteo")
        n = int.from_bytes(b[p:p + 4], 'little'); p += 4
        if n == 0xFFFFFFFF:
            return p
        for _ in range(n):
            if et == 0x0E:
                p = leer_serstring(b, p)[0]
            elif et in TAM:
                p += TAM[et]
            else:
                raise ValueError("tipo de elemento 0x%02x no soportado" % et)
        return p
    raise ValueError("tag de valor desconocido 0x%02x" % tag)


def nombre_del_atributo(row):
    try:
        r = getattr(row.Type, 'row', None)
        if r is None:
            return None
        cls = getattr(r, 'Class', None)
        if cls is None:
            return None
        cr = getattr(cls, 'row', None)
        return str(getattr(cr, 'TypeName', '') or '') if cr else None
    except Exception:
        return None


def valida_dos_args_string_objeto(b):
    """Valida el blob de un atributo con firma (string, object), como ExportMetadataAttribute."""
    if len(b) < 4:
        return "blob demasiado corto (%d B)" % len(b)
    if int.from_bytes(b[:2], 'little') != 1:
        return "prolog invalido 0x%04x" % int.from_bytes(b[:2], 'little')
    p = 2
    try:
        p, _ = leer_serstring(b, p)
        p = leer_valor(b, p)
        if p + 2 > len(b):
            return "falta el conteo de argumentos con nombre"
        p += 2
    except ValueError as e:
        return str(e)
    if p != len(b):
        return "bytes sin consumir: %d de %d" % (p, len(b))
    return None


def revisa(path):
    print("=" * 78)
    print(path)
    try:
        pe = dnfile.dnPE(path)
        ca = pe.net.mdtables.CustomAttribute
    except Exception as e:
        print("  no se pudo leer la metadata: %s" % e)
        return 1
    if ca is None:
        print("  sin tabla CustomAttribute")
        return 0

    revisados = 0
    fallos = 0
    for i, row in enumerate(ca.rows):
        nombre = nombre_del_atributo(row)
        if not nombre or 'ExportMetadata' not in nombre:
            continue
        b = row.Value.value or b''
        revisados += 1
        err = valida_dos_args_string_objeto(b)
        if err:
            fallos += 1
            print("  FILA %-4d %7d B  INVALIDO: %s" % (i, len(b), err))

    # Aviso preventivo: cualquier string largo cerca del limite es una bomba de tiempo.
    for i, row in enumerate(ca.rows):
        b = row.Value.value or b''
        if len(b) > LIMITE_2_BYTES:
            print("  aviso: fila %d tiene un blob de %d B (>%d): revisar su codificacion"
                  % (i, len(b), LIMITE_2_BYTES))

    print("  ExportMetadata revisados: %d | invalidos: %d  %s"
          % (revisados, fallos, "OK" if fallos == 0 else "*** FALLA ***"))
    return fallos


if __name__ == '__main__':
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(2)
    total = sum(revisa(p) for p in sys.argv[1:])
    print("=" * 78)
    print("TOTAL de blobs invalidos: %d" % total)
    sys.exit(0 if total == 0 else 1)
