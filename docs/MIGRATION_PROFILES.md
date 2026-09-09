# Formato de perfiles de migración

Versión de esquema actual: **1**

## Ubicación

Los perfiles se guardan como archivos `.json` individuales, uno por perfil, en la carpeta que
la capa XrmToolBox resuelva (normalmente algo como
`%APPDATA%\MscrmTools\XrmToolBox\DataverseMasterDataMigrator\Profiles\`). El nombre de archivo
es `<id>.json`, donde `<id>` es el GUID del perfil — así renombrar el perfil (cambiar `name`)
nunca requiere renombrar el archivo.

Los backups automáticos (antes de cada sobrescritura) van a una subcarpeta `Backups\` dentro de
la misma carpeta de perfiles, con nombre `<id>.<yyyyMMddHHmmss>.json`. Se conservan como máximo
5 backups por perfil.

## Codificación

UTF-8 sin BOM, JSON indentado (legible), `\n` como salto de línea.

## Esquema

```json
{
  "schemaVersion": 1,
  "id": "3f2a9e10-1b7a-4e2a-9a4b-000000000001",
  "name": "Maestros Customer Service",
  "description": "Tablas maestras utilizadas por Customer Service Workspace",
  "createdUtc": "2026-09-07T14:00:00Z",
  "updatedUtc": "2026-09-07T14:00:00Z",
  "options": {
    "preserveSourceGuid": true,
    "mode": "Upsert",
    "stopOnError": false,
    "restoreStateStatus": true,
    "createManyToMany": true,
    "requiredLookupPolicy": "FailPreflight",
    "optionalLookupPolicy": "WarnAndContinue"
  },
  "entities": [
    {
      "logicalName": "wit_tema",
      "displayName": "Tema",
      "enabled": true,
      "preferredOrder": 10,
      "filter": null,
      "attributeMode": "AllWritable",
      "excludedAttributes": []
    }
  ]
}
```

## Campos

### Raíz

| Campo | Tipo | Obligatorio | Notas |
|---|---|---|---|
| `schemaVersion` | int | sí | Hoy siempre `1`. Un valor mayor al soportado por la versión instalada del plugin produce un error de validación explícito, nunca una lectura parcial silenciosa. |
| `id` | GUID string | sí | Inmutable una vez creado. Es también el nombre de archivo. |
| `name` | string | sí | Nombre visible, editable (Rename no cambia `id`). |
| `description` | string o `null` | no | Libre. |
| `createdUtc` / `updatedUtc` | ISO 8601 UTC | sí | `updatedUtc` se actualiza en cada `Save`. |
| `options` | objeto | sí | Ver abajo. |
| `entities` | array | sí | Al menos 1 elemento para que el perfil sea válido en Preflight (puede guardarse vacío mientras se está armando). |

### `options`

| Campo | Tipo | Default | Notas |
|---|---|---|---|
| `preserveSourceGuid` | bool | `true` | Comportamiento esencial descrito en la sección 11 del requerimiento. Poner en `false` queda soportado por el esquema para el futuro, pero la UI de esta versión no expone esa opción (siempre `true`). |
| `mode` | enum string: `Upsert`, `CreateOnly`, `UpdateOnly` | `Upsert` | `Upsert` es el único usado en V1; los otros dos valores existen para no romper el esquema cuando se agregue esa opción a la UI. |
| `stopOnError` | bool | `false` | Si `true`, un error no transitorio en un registro detiene la tabla completa en vez de continuar con el resto y reportarlo al final. |
| `restoreStateStatus` | bool | `true` | Aplica solo a tablas con `statecode`/`statuscode`; ver `ARCHITECTURE.md` sección 6, Pass 3. |
| `createManyToMany` | bool | `true` | Si `false`, el perfil migra las tablas pero omite el Pass 3 de asociaciones N:N. |
| `requiredLookupPolicy` | enum string: `FailPreflight`, `WarnOnly` | `FailPreflight` | Coherente con la sección 12: un lookup obligatorio sin resolver siempre debería bloquear, pero se deja como opción explícita y no como comportamiento hardcodeado, documentando la excepción. |
| `optionalLookupPolicy` | enum string: `WarnAndContinue`, `SkipSilently` | `WarnAndContinue` | Por defecto nunca se oculta un problema de integridad referencial sin avisar (sección 12, "no ocultar errores"). |

### Cada elemento de `entities`

| Campo | Tipo | Obligatorio | Notas |
|---|---|---|---|
| `logicalName` | string | sí | Nombre lógico real de la tabla en Source. Es la clave de identidad dentro del perfil — no puede haber dos entradas con el mismo `logicalName`. |
| `displayName` | string | sí | Copia de la metadata al momento de crear/editar el perfil, solo para mostrarla en la UI sin re-consultar metadata. No es la fuente de verdad — el Preflight siempre revalida contra la metadata real de Source/Target. |
| `enabled` | bool | sí | Permite dejar una tabla en el perfil pero excluirla temporalmente de la ejecución sin borrarla de la definición. |
| `preferredOrder` | int | sí | Desempate cuando el orden real de dependencias no obliga una secuencia (ver `ARCHITECTURE.md` sección 4). No es el orden de ejecución definitivo. |
| `filter` | string o `null` | no | Reservado para FetchXML/filtro por tabla (sección 10 del requerimiento: "dejar preparado el modelo"). `null` en V1 siempre significa "todos los registros". |
| `attributeMode` | enum string: `AllWritable` | sí | Único valor soportado en V1. El esquema admite el string para no romper si se agrega `Custom` (lista explícita) más adelante. |
| `excludedAttributes` | array de string | no (default `[]`) | Nombres lógicos de atributos a excluir explícitamente aunque sean escribibles. |

## Validación de archivos corruptos

`MigrationProfileSerializer.TryDeserialize` nunca lanza una excepción hacia el llamador. Ante
JSON inválido, `schemaVersion` no reconocido, o campos obligatorios ausentes, devuelve
`success = false` con un mensaje de error legible. La UI debe mostrar ese perfil como
"⚠ no se pudo cargar" en el listado, sin impedir que el resto de los perfiles se lean
normalmente ni que el plugin arranque.

## Evolución futura del esquema

Cuando se necesite un `schemaVersion: 2`, la regla es: el deserializador de la versión N+1 debe
poder seguir leyendo archivos `schemaVersion: N` rellenando los campos nuevos con sus defaults
— nunca al revés. Un plugin viejo abriendo un perfil de esquema más nuevo debe rechazarlo con un
mensaje explícito ("este perfil requiere una versión más reciente del plugin"), no intentar
adivinar.
