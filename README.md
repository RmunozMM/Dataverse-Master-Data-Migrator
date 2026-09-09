# Dataverse Master Data Migrator

Plugin para **XrmToolBox** que migra datos maestros entre ambientes de Microsoft Dataverse /
Dynamics 365 usando perfiles reutilizables y persistentes, en vez de migraciones manuales tabla
por tabla.

- **Autor:** Rogelio Muñoz — [www.rogeliomunoz.cl](http://www.rogeliomunoz.cl)
- **Versión actual:** 0.5.1.0
- **Plataforma:** .NET Framework 4.8, WinForms
- **Todos los derechos reservados.** El código está publicado para consulta y para descargar el
  instalador; no se autoriza su reutilización ni redistribución sin permiso del autor.

## Descargar el instalador

La versión compilada y lista para usar está en la pestaña **[Releases](../../releases)** de
este repositorio. Descargue el `.zip` de la última versión, extráigalo y ejecute
`Instalar-Plugin.bat` **con XrmToolBox cerrado**.

---

## Qué hace

| Botón | Qué genera |
|---|---|
| **Preflight** | Valida un perfil contra la metadata real de Source y Target antes de migrar: tablas/atributos faltantes en Target, lookups obligatorios sin resolver, ciclos de dependencias. No mueve datos. |
| **Preview Data** | Panel dual (Source \| Target), inspirado en un cliente FTP: por tabla, qué registros se crearían (🟩) y cuáles ya existen y se actualizarían (⬜/🟥, según si el nombre cambia). Permite curar la selección registro por registro antes de ejecutar. |
| **Compare Structure** | Compara la **estructura** (no los datos) de las tablas del perfil entre Source y Target — tabla por tabla, atributo por atributo — para homologar entornos antes de migrar. Exportable a `.html` (autocontenido, con índice navegable) o `.xlsx` (una hoja por tabla, hipervínculos al índice). |
| **Execute** | Motor de ejecución multipass real: Pass 1 (create/update), Pass 2 (lookups diferidos, incl. auto-referenciados), Pass 3 (statecode/statuscode + asociaciones N:N). Reintenta transitorios con backoff y deja checkpoints incrementales. |
| **Retry Failed** | Reintenta solo los registros que fallaron en la última ejecución (por tabla y pass), sin re-planificar ni re-paginar lo que ya se completó. |
| **Find Related Tables** | Recorre recursivamente (BFS) las dependencias de las tablas ya seleccionadas de un perfil y sugiere agregar las que falten — evita que Preflight siga encontrando lookups sin resolver ronda tras ronda. |

El perfil de migración (tablas incluidas, política de lookups, modo upsert/create-only/update-only,
etc.) se guarda como JSON reutilizable — ver `docs/MIGRATION_PROFILES.md`.

---

## Cómo compilar

Requiere **Visual Studio 2019 o superior** con soporte de .NET Framework 4.8, **o** solo el
**.NET SDK** (sin Visual Studio):

```
git clone https://github.com/RmunozMM/Dataverse-Master-Data-Migrator.git
cd Dataverse-Master-Data-Migrator
dotnet build DataverseMasterDataMigrator.sln -c Release
dotnet test tests\DataverseMasterDataMigrator.Tests\DataverseMasterDataMigrator.Tests.csproj -c Release
```

Ambos proyectos clásicos (`Core`/`XrmToolBox`, sin formato SDK) referencian
`Microsoft.NETFramework.ReferenceAssemblies` vía NuGet, así que compilan contra `net48` real sin
tener instalado el Developer Pack de .NET Framework ni Visual Studio.

### Sobre las referencias en `lib/`

Las DLL de terceros necesarias para compilar (XrmToolBox, SDK de Dataverse, McTools,
Newtonsoft.Json) están versionadas en **`lib/`**, así que un clon nuevo compila sin pasos
previos. Deben coincidir en versión con el XrmToolBox real donde se va a instalar el plugin — un
tamaño de archivo igual no garantiza la misma `AssemblyName.Version` (ver "Trampas ya pagadas",
punto 6).

### Sobre ClosedXML (exportador a Excel de Compare Structure)

`Core.csproj` no usa formato SDK, así que `dotnet build` **no** convierte automáticamente un
`PackageReference` restaurado en una referencia de compilación/copia local — eso depende de los
targets de `Microsoft.NET.Sdk`, que este proyecto deliberadamente no usa (mismo motivo que
`Newtonsoft.Json` se referencia por `HintPath` en vez de por paquete). Por eso ClosedXML y sus
9 dependencias transitivas se referencian explícitamente por `HintPath` a la caché de NuGet
(`$(NuGetPackageRoot)`) con `Private=True`. Si se sube la versión de ClosedXML, hay que revisar
esas rutas a mano contra `obj\...\project.assets.json` tras un `dotnet restore`.

---

## Cómo empaquetar el instalador

Un instalador es un `.zip` con:

```
DataverseMasterDataMigrator.dll          <- compilado desde bin\Release (XrmToolBox)
Instalar-Plugin.bat                      <- raíz del repo
Install-DataverseMasterDataMigrator.ps1  <- raíz del repo
DataverseMasterDataMigrator/
    DataverseMasterDataMigrator.Core.dll         <- compilado desde bin\Release (Core)
    ClosedXML.dll, DocumentFormat.OpenXml.dll,
    ExcelNumberFormat.dll, Irony.dll, SixLabors.Fonts.dll,
    System.Buffers.dll, System.IO.Packaging.dll, System.Memory.dll,
    System.Numerics.Vectors.dll, System.Runtime.CompilerServices.Unsafe.dll,
    XLParser.dll                                 <- todas desde bin\Release (Core), NUNCA a la raíz de Plugins
```

El script instala en `%APPDATA%\MscrmTools\XrmToolBox\Plugins\` y exige que XrmToolBox esté
cerrado (si está abierto mantiene el DLL bloqueado; el script lo detecta y ofrece cerrarlo). El
`.zip` en sí **no** está versionado — se publica como *GitHub Release*.

---

## Estructura

```
DataverseMasterDataMigrator.sln
/src
    /DataverseMasterDataMigrator.Core          <- motor, sin SDK de Dataverse ni WinForms
    /DataverseMasterDataMigrator.XrmToolBox    <- plugin: Plugin.cs, PluginControl, adaptadores SDK
/tests
    /DataverseMasterDataMigrator.Tests         <- xUnit, sin conexión Dataverse real
/docs
    ARCHITECTURE.md                             <- decisiones de diseño
    MIGRATION_PROFILES.md                       <- contrato del JSON de perfiles
/tools
    verify_attr_blobs.py                        <- reutilizado de Metadata Dataverse Document
/lib
    README.txt, Newtonsoft.Json.dll, Microsoft.Xrm.Sdk.dll, XrmToolBox.Extensibility.dll, ...
```

---

## Trampas ya pagadas (leer antes de tocar el motor o la UI)

Defectos reales que costaron varias versiones de encontrar. Están documentados aquí (y con más
detalle en `CHANGELOG.md`) para no repetirlos.

**1. `BackgroundWorker.ReportProgress` lanza excepción si `WorkAsyncInfo` no declara `ProgressChanged`.**
XrmToolBox arma su `BackgroundWorker` interno con `WorkerReportsProgress = (info.ProgressChanged
!= null)`. Sin ese handler, cada llamada a `ReportProgress` tira `InvalidOperationException` —
tumbó Preflight/Preview/Find Related Tables por completo en la 0.4.1. Confirmado decompilando
`XrmToolBox.Extensibility.dll` real con `ilspycmd`, no asumido. El mecanismo correcto para
actualizar el texto del modal de progreso desde un hilo de background es `SetWorkingMessage(...)`
(heredado de `PluginControlBase`, hace `host.Invoke(...)` internamente) — `ProgressChanged` nunca
actualiza ese texto en el código real del host.

**2. Apilar controles con `Dock` directo sobre un `Form` es ambiguo y frágil.**
Para controles hermanos con el mismo `Dock`, el orden real de anclaje depende del orden de
`Controls.Add` de una forma poco intuitiva. Causó tres bugs reales distintos en el mismo diálogo
(`PreviewResultsForm`): franjas superpuestas, grilla en blanco, filas tapadas por la barra
superior. La solución que elimina la clase de bug entera es un `TableLayoutPanel` con una celda
explícita por franja (una fila `AutoSize` por barra + una `Percent(100)` para el contenido) — no
hay ambigüedad de Z-order posible con celdas explícitas.

**3. Un lookup genuinamente polimórfico puede traer `LogicalName` literal `"entity"`.**
Algunos campos de sistema (vistos en tablas `msdyn_*` de Copilot/AI Builder) no declaran una
tabla real de destino; Dataverse rechaza de plano cualquier `Retrieve`/`Create`/`Update` con
`LogicalName = "entity"` ("The 'Retrieve' method does not support entities of type 'entity'"),
tumbando toda la corrida por un solo valor no evaluable. El guard (`string.IsNullOrEmpty(...) ||
string.Equals(..., "entity", OrdinalIgnoreCase)`) tuvo que aplicarse en **tres** lugares
independientes: el muestreo de Preflight, el camino de escritura real, y el filtro de
`SkipSilently` — cada uno lo reintrodujo por separado.

**4. `EntityMetadata.IsCustomEntity` es `true` también para tablas de soluciones administradas de Microsoft.**
"Custom" en la metadata de Dataverse no significa "creada por este tenant" — incluye tablas
`adx_`/`mspp_` (Power Pages), `msdyn_` (Copilot/IoT), `msdynmkt_` (Marketing) instaladas por
soluciones **managed** de Microsoft. Un primer intento de filtrar por `!IsManaged` fue **peor**:
también excluía las tablas propias del tenant cuando éste las distribuye como solución
administrada (patrón normal de ALM), confirmado en vivo. El criterio correcto es una lista fija
de prefijos globalmente reservados de Microsoft, no un flag de la capa de solución actual.

**5. Un `.csproj` clásico + `dotnet build` no prueba que una dependencia nueva cargue en runtime.**
`PackageReference` sin formato SDK no cablea copia local; hace falta `<Reference HintPath>`
explícito. Pero incluso corrigiendo eso, un test SDK-style puede seguir en verde mientras el
plugin real, cargado desde una carpeta con solo los archivos que se van a distribuir, revienta
con `FileNotFoundException`/`TypeLoadException` por un desajuste de versión de una dependencia
transitiva (`System.Numerics.Vectors` en la cadena de ClosedXML/SixLabors.Fonts) sin binding
redirect disponible (el plugin corre dentro del proceso/`app.config` de `XrmToolBox.exe`, no el
propio). La única prueba válida es cargar desde una carpeta real con exactamente lo que se va a
enviar. La corrección final fue un `AssemblyResolveEventHandler` propio en `Plugin.cs` con una
lista de redirección forzada por nombre simple, ignorando la versión exacta pedida.

**6. XrmToolBox cachea metadata de cada plugin (ícono, versión) por `AssemblyQualifiedName`.**
Recompilar sin subir `AssemblyVersion` deja servida la entrada vieja de `Plugins\manifest.json`
— un ícono nuevo, o un DLL con un fix real, puede parecer que "no se aplicó" cuando en realidad
XrmToolBox nunca detectó que había un ensamblado distinto. Toda versión que se vaya a instalar de
verdad sube `AssemblyVersion`/`AssemblyFileVersion`, nunca solo el DLL.

---

## Diagnóstico

El plugin escribe en el log de XrmToolBox:

```
%APPDATA%\MscrmTools\XrmToolBox\Logs\DataverseMasterDataMigrator.log
```

Cada ejecución además deja un `ExecutionManifest` con checkpoints incrementales en
`%APPDATA%\MscrmTools\XrmToolBox\DataverseMasterDataMigrator\Executions\`, que es lo que usa
"Retry Failed" para saber qué reintentar sin re-planificar toda la migración.

---

## Mejoras pendientes

- Íconos reales de `Plugin.cs` — hoy placeholder, por decisión explícita.
- Verificar Pass 3b (asociaciones N:N) contra una relación real — hoy es best-effort y se salta
  con un warning si no reconoce las columnas FK de la tabla de intersección.
- `RequiredLookupPolicy = SkipSilently` queda fuera de alcance de la implementación actual (solo
  cubre `OptionalLookupPolicy`) — omitir un lookup obligatorio es una decisión más delicada, no
  pedida todavía.
- Evaluar actualizar `Microsoft.Xrm.Sdk.dll` a una versión con `IsCreateMultipleSupported` /
  `IsUpdateMultipleSupported` si se necesita la ventaja de rendimiento de
  `CreateMultiple`/`UpdateMultiple` (impacto en otros plugins a evaluar antes de tocarla).
- Las DLL de `lib/` podrían venir de paquetes NuGet en lugar de estar versionadas.
