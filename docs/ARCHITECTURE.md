# Dataverse Master Data Migrator — Arquitectura

**Estado:** Fase 1 (Core + planificación). No incluye todavía la capa XrmToolBox ni el instalador.

Este documento fija las decisiones tomadas para que la construcción no dependa de preguntar
cada detalle menor. Donde el requerimiento original dejaba una alternativa abierta, se eligió
la opción más seguray mantenible, y queda registrada aquí con su razón.

---

## 1. Principio rector: Core sin SDK, sin WinForms, sin XrmToolBox

`DataverseMasterDataMigrator.Core` no referencia `Microsoft.Xrm.Sdk`, `System.Windows.Forms` ni
`XrmToolBox.Extensibility`. Toda esa dependencia vive exclusivamente en
`DataverseMasterDataMigrator.XrmToolBox`, que implementa las interfaces que el Core declara.

Esto no es una preferencia estética: es lo que permite que el mismo Core sirva después al
plugin de Universidad Mayor, y eventualmente a una CLI, sin tocar una línea de lógica de
negocio (sección 23 del requerimiento).

Consecuencia directa: el Core no conoce `Entity` ni `EntityReference` del SDK. Define sus
propios tipos (`DataRecord`, `DataReference`) y dos interfaces de adaptación
(`IDataverseMetadataProvider`, `IDataverseRecordService`) que la capa XrmToolBox implementa
usando `IOrganizationService`. El Core solo ve datos, nunca la forma de obtenerlos.

## 2. Formato de perfiles: JSON versionado, `schemaVersion` explícito

Se define el contrato completo en `MIGRATION_PROFILES.md`. Decisiones clave:

- `schemaVersion` es un entero, no un string — comparaciones numéricas simples para "es
  anterior a".
- La deserialización nunca lanza directamente: `MigrationProfileSerializer.TryDeserialize`
  devuelve `(bool success, MigrationProfile profile, string error)`. Un perfil corrupto no
  puede tumbar el plugin (requisito explícito de la sección 6).
- Antes de sobrescribir un perfil existente, `MigrationProfileRepository` copia el archivo
  actual a `Backups/<id>.<timestampUtc>.json`. Se conservan como máximo 5 backups por perfil
  (rotación simple) para no acumular basura indefinidamente.
- Serialización con **Newtonsoft.Json**, no `System.Text.Json`. Razón: XrmToolBox ya carga
  Newtonsoft.Json como dependencia compartida en la raíz de `Plugins` (todo el host y la
  mayoría de sus plugins la usan). Referenciarla como no-copy-local evita duplicar el
  ensamblado y el riesgo de versión distinta que ya documentaste en el proyecto de referencia
  para `EPPlus`/`System.Resources.Extensions`. `System.Text.Json` en .NET Framework 4.8
  requeriría un paquete adicional sin ese beneficio.

## 3. Persistencia de perfiles: ubicación decidida por la capa XrmToolBox, no por el Core

El Core recibe una carpeta (`string profilesFolder`) en el constructor de
`MigrationProfileRepository`. No sabe ni le importa si esa carpeta está bajo
`%APPDATA%\MscrmTools\XrmToolBox\...`. Quien resuelve esa ruta es
`DataverseMasterDataMigrator.XrmToolBox`, exactamente como pide la sección 7: los perfiles
sobreviven a la actualización de la DLL porque viven fuera de la carpeta de instalación del
plugin.

## 4. Planner: grafo de dependencias + orden topológico + tolerancia a ciclos

- `DependencyGraphBuilder` construye aristas únicamente entre tablas **incluidas en el
  perfil**. Un lookup hacia una tabla fuera del perfil no genera arista — se resuelve en
  runtime contra Target (sección 12), no en el grafo de planificación.
- `CycleDetector` usa Tarjan (componentes fuertemente conexas) para detectar ciclos con costo
  O(V+E), y para poder informar cada ciclo completo en el Preflight (sección 16: "1 - multipass
  resolution"), no solo "hay un ciclo".
- `TopologicalSorter` implementa Kahn. Cuando hay un empate entre nodos sin dependencias
  pendientes, desempata por `preferredOrder` del perfil (menor primero) y, en último caso, por
  nombre lógico — así el orden es determinista entre ejecuciones, lo cual importa para
  reproducibilidad y para que los logs de dos corridas sean comparables.
- Un ciclo real (A→B→C→A) no bloquea el plan: los nodos del ciclo se emiten en el orden de
  `preferredOrder`/nombre, y quedan marcados `IsPartOfCycle = true` en el `MigrationPlan`. La
  resolución real ocurre en el motor de escritura multipass (ver sección 6), no en el planner.

## 5. Preflight: separado del planner, pero lo consume

`PreflightValidator` no repite el trabajo del planner — lo llama y interpreta su resultado.
Categoriza cada hallazgo en `Error` (bloquea Execute) o `Warning` (requiere confirmación,
sección 22). Fuente de verdad para "qué es error vs warning":

| Situación | Severidad |
|---|---|
| Source y Target son el mismo `OrganizationId` | Error |
| Tabla del perfil no existe en Target | Error |
| Lookup requerido (`RequiredLevel != None`) sin resolver | Error |
| Lookup opcional sin resolver | Warning |
| Ciclo de dependencias detectado | Warning (multipass lo resuelve) |
| Atributo no migrable filtrado silenciosamente (calculado/system) | Warning informativo |

## 6. Motor de escritura: estrategia por tabla, no global

`IDataverseRecordService` expone capacidades (`SupportsCreateMultiple`, etc.) por tabla,
obtenidas de metadata real (`EntityMetadata.IsCreateMultipleSupported` /
`IsUpdateMultipleSupported` en la capa XrmToolBox). El Core decide la estrategia
(`WriteStrategy` enum: `BulkUpsert`, `BulkCreateThenUpdate`, `ExecuteMultipleFallback`,
`IndividualRequests`) pero delega la ejecución concreta al adaptador. Así el Core contiene la
*decisión* sin conocer el mensaje SOAP/Web API real.

Multipass (sección 9), concreto:

- **Pass 1**: crear/actualizar cada registro con sus atributos no-lookup, más los lookups que
  ya son resolubles (apuntan a un registro que sabemos que ya existe en Target, sea porque no
  está en el perfil y se verificó, sea porque su tabla ya se procesó en un pass anterior sin
  formar parte de un ciclo).
- **Pass 2**: para los lookups diferidos en Pass 1 (típicamente los que cierran un ciclo),
  update dirigido solo a esos atributos.
- **Pass 3**: relaciones N:N configuradas, y restauración de `statecode`/`statuscode` cuando
  la combinación es válida.

## 7. Checkpoints: por tabla y por chunk, sin guardar payloads completos

`ExecutionManifest` persiste tras cada tabla (no solo al final) y, dentro de una tabla, tras
cada chunk. Se guarda: GUID del registro, operación, resultado, mensaje de error si aplica,
pass, intento de retry — nunca los atributos del registro en sí (sección 20: "no guardar
payloads completos salvo justificación técnica real"; aquí no la hay).

`Retry Failed` relee el manifest de la última ejecución `Failed`/`CompletedWithWarnings` y
reconstruye la lista de pendientes sin re-leer ni re-planificar las tablas ya completadas.

## 8. Retry / throttling: el Core decide, el adaptador clasifica

`RetryPolicy` en el Core no conoce excepciones de Dataverse. Recibe un `RetryContext` con
`IsTransient` (bool) y `RetryAfter` (TimeSpan?) ya resueltos por el adaptador XrmToolBox, que sí
sabe interpretar un `FaultException<OrganizationServiceFault>` con código 429 o un error
transitorio de red. Esto mantiene la clasificación específica de Dataverse fuera del Core sin
sacrificar que la política de backoff (exponencial con jitter, tope de intentos) sea la misma
lógica reutilizable para cualquier otro caller futuro (CLI, plugin UMayor).

## 9. Extensibilidad para el segundo plugin (sección 23)

Interfaces ya definidas en el Core para que el futuro `UMayor Contact Data Clone` las
implemente sin tocar el motor genérico:

- `IRecordTransformer` — transformación de un `DataRecord` antes de escribir (anonimización
  futura).
- `IReferenceResolver` — resolución de lookups externos al perfil; la implementación default
  busca por GUID igual en Target, una futura implementación UMayor podría resolver por mapping
  alternativo.
- `IMigrationProfileProvider` — abstrae de dónde viene un perfil (hoy: archivo JSON local;
  mañana: podría venir embebido en código para el plugin UMayor).
- `IRecordSelector` — qué registros de una tabla se incluyen (hoy: todos; UMayor: grafo desde
  un Contact raíz).

Ninguna lógica de UMayor vive en este Core. Estas son extensiones (interfaces), no
ramificaciones condicionales dentro del motor genérico.

## 10. Lo que NO se resuelve en esta fase

Coherente con el alcance del requerimiento (sección 2): sin delete, sin mirror destructivo, sin
CLI headless, sin anonimización. El `IRecordTransformer` existe como *gancho* vacío
(implementación default = identidad), no como funcionalidad activa.

## 11. Estado de esta entrega (fase 2)

**Fase 1 (Core):** completa, con tests unitarios, compilada y verificada con un runtime C# real
(mono) dentro del entorno de generación — 26/26 verificaciones de comportamiento pasaron.

**Fase 2 (XrmToolBox):** `DataverseMasterDataMigrator.XrmToolBox` compila limpio contra los
DLL reales de tu instalación (`Microsoft.Xrm.Sdk.dll`, `XrmToolBox.Extensibility.dll`, etc.,
verificado con `mcs` + desensamblado real de esos ensamblados, no solo revisión visual).
Incluye:

- `Plugin.cs`: exportación MEF de dos conexiones, `AssemblyResolveEventHandler` portado
  directamente del ya validado en Metadata Dataverse Document.
- `Services/DataverseMetadataProviderAdapter.cs` y `Services/DataverseRecordServiceAdapter.cs`:
  implementación real de los puertos del Core contra `IOrganizationService`.
- `UI/PluginControl.cs` + `UI/PluginControl.Layout.cs`: control de XrmToolBox con las cuatro
  secciones (Connections, Profiles, Tables, Migration) — UI construida en código, no vía
  archivo `.designer.cs` (ver nota en `PluginControl.cs`), funcionalmente completa para
  conexiones, gestión de perfiles, carga/filtro de tablas y Preflight.

**Limitación real conocida, encontrada durante la verificación:** la versión de
`Microsoft.Xrm.Sdk.dll` referenciada en `lib/` (la misma que usa tu Metadata Dataverse
Document) no expone `EntityMetadata.IsCreateMultipleSupported` / `IsUpdateMultipleSupported`.
El código detecta esto de forma segura (ambos quedan en `false`, cayendo siempre a
`WriteStrategy.ExecuteMultipleUpsert`), pero no vas a obtener la ventaja de rendimiento de
`CreateMultiple`/`UpdateMultiple` hasta actualizar esa referencia a una versión más nueva del
SDK.

**Pendiente aún dentro de la fase 2:**
- Motor de ejecución multipass real conectado al botón Execute (hoy solo construye y muestra el
  plan; no escribe todavía en Target — ver comentario en `OnExecute`).
- Muestreo de lookups externos no resueltos para completar el Preflight (`UnresolvedExternalLookups`
  se pasa vacío por ahora).
- `Retry Failed` leyendo el `ExecutionManifest` real.
- Instalador y empaquetado de distribución.

Todo lo anterior fue compilado con `mcs` (Mono) contra los DLL reales de tu proyecto de
referencia — no es una revisión visual del código, es una compilación real que atrapó y corrigió
varios errores genuinos (ver `CHANGELOG.md`). Aun así, la verificación definitiva sigue siendo
compilar en Visual Studio con .NET Framework 4.8 real y probar contra XrmToolBox de verdad.

## 12. Estado de esta entrega (fase 3)

Actualización sobre la sección 11: la verificación definitiva mencionada arriba ya se hizo —
`dotnet build` real contra `net48` (con Roslyn moderno, no Mono ni el `csc.exe` viejo del
framework — ver `Microsoft.NETFramework.ReferenceAssemblies` en `CHANGELOG.md` [0.3.0]),
56/56 tests xUnit sobre el CLR de .NET Framework real, y una instalación real en
`C:\CT\XrmToolbox` (que reveló que los DLL de `lib/` estaban desactualizados respecto al
XrmToolBox real — corregido, ver changelog).

Motor de ejecución (`Core.Migration.MigrationExecutor`), muestreo de lookups externos
(`Core.Validation.ExternalLookupSampler`), Retry Failed
(`MigrationExecutor.RetryFailedAsync` + `IDataverseRecordService.RetrieveByIdsAsync`), e
instalador (`Instalar-Plugin.bat` + `.ps1` + `dist/*-Installation.zip`) — todo implementado y
conectado a la UI. Detalle completo en `CHANGELOG.md` [0.3.0].

Lo que sigue pendiente, por decisión explícita o por no poder verificarse sin datos reales:
- Íconos reales de `Plugin.cs` (hoy placeholder).
- Pass 3b (N:N) no se ha probado contra una relación real todavía.
