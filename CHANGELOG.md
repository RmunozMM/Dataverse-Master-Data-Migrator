# Changelog

## [0.6.6] — Agregado: `OwnerIdOverride` (mecanismo del Core, sin uso en el migrador genérico)

### Agregado
- `ProfileOptions.OwnerIdOverride` (`Guid?`, default `null`): cuando viene seteado, Pass 1 escribe
  explícitamente el atributo owner-lookup real de cada tabla (`AttributeSummary.IsOwnerLookup`,
  normalmente `ownerid`) apuntando a ese SystemUser, en vez de dejarlo fuera del payload como es
  el comportamiento por defecto. Pensado como mitigación de último recurso para el caso real
  (reportado desde **Umayor Test Data Seeder**, herramienta hermana que consume este mismo Core)
  donde Target tiene automatización server-side que intenta auto-asignar un owner de Origen
  inexistente cuando `ownerid` llega vacío en el Create, y el error resultante
  (`Entity 'SystemUser' With Id = ... Does Not Exist`) no señala ningún campo que el migrador
  controle directamente.
- **El migrador genérico (este plugin) nunca setea este valor** — el default `null` preserva el
  comportamiento actual (owner nunca escrito) sin cambios. Solo lo activa el consumidor Umayor
  Test Data Seeder, seteándolo al SystemUser del usuario conectado a Target.

### Interno
- `AssemblyVersion`/`AssemblyFileVersion`: Core `0.2.2.0` → `0.2.3.0`; plugin XrmToolBox
  `0.6.5.0` → `0.6.6.0` (bump de convención — el plugin en sí no cambió de comportamiento, pero
  Core sí, y ambos plugins que lo consumen deben quedar en la misma versión de Core tras
  reinstalar; ver lección de `Core.dll` sin versionar en `[0.6.1]`).

## [0.6.5] — Corregido: `ExistsAsync` abortaba toda la migración ante un tipo de entidad no consultable

### Corregido (crash real reportado por el usuario, usando SkipSilently contra su tenant real)
- **`The 'Retrieve' method does not support entities of type 'attachment'`** abortaba la
  migración COMPLETA (no solo un registro) apenas arrancaba Pass 1. Causa: `ExistsAsync` (usado
  por `SkipSilently` para decidir si un lookup externo al perfil existe en Target) solo toleraba
  el fault específico de Dataverse "el registro no existe" — cualquier OTRO fault, incluido "este
  tipo de entidad no admite `Retrieve` en absoluto" (un tipo interno/restringido como
  `attachment`, el almacenamiento binario real detrás de `activitymimeattachment`), se propagaba
  sin capturar y tumbaba todo el proceso antes de escribir un solo registro.
- **Fix**: `ExistsAsync` ahora trata CUALQUIER `FaultException<OrganizationServiceFault>` como
  "no se puede confirmar/resolver este valor externo" (`false`) — exactamente lo que sus dos
  únicos llamadores (`MigrationExecutor.RemoveSkipSilentlyLookupsAsync`,
  `ExternalLookupSampler.SampleAsync`) ya hacían con un "no existe" genuino: omitir ese valor
  puntual en vez de fallar. Ninguno de los dos necesita distinguir "genuinamente no existe" de
  "no se pudo consultar" — fallar seguro (omitir el campo) es correcto en ambos casos. El chequeo
  de existencia de `WriteBulkCreateThenUpdate` (decide create-vs-update, un contexto distinto)
  NO se tocó — ahí sí importa distinguir "no existe todavía" de un error real.

### Interno
- `AssemblyVersion`/`AssemblyFileVersion` (plugin XrmToolBox): `0.6.4.0` → `0.6.5.0`. `Core.dll`
  no cambió (el fix vive en `DataverseRecordServiceAdapter.cs`, proyecto `XrmToolBox`).

## [0.6.4] — Corregido: atributos válidos para escribir pero no para leer (`subscriptionid`)

### Corregido (crash real reportado por el usuario, primera vez que Migrar corrió contra el tenant real)
- **`Retrieve can only return columns that are valid for read. Column: subscriptionid. Entity: contact`**
  al correr Migrar por primera vez de verdad. Causa: `subscriptionid` (usado por la
  sincronización offline/Outlook) reporta `IsValidForCreate`/`IsValidForUpdate = true` en la
  metadata real, pero `IsValidForRead = false` — una combinación real que
  `AttributeWritabilityRules.GetWritableAttributes` (la única fuente de verdad de "qué atributo
  se escribe", usada tanto por Pass 1 de `MigrationExecutor` como por Preflight) nunca chequeaba:
  solo filtraba por Create/Update, nunca por Read. El atributo terminaba en la lista de columnas
  a pedirle a Source, y Dataverse rechaza ese `Retrieve` de plano — tumbando la tabla completa
  antes de escribir un solo registro.
- **Fix**: nuevo campo `AttributeSummary.IsValidForRead` (default `true`, no rompe nada
  existente), poblado desde la metadata real (`AttributeMetadata.IsValidForRead`) en
  `DataverseMetadataProviderAdapter`, y agregado como filtro explícito en
  `AttributeWritabilityRules.GetWritableAttributes` y en `ExternalLookupSampler` (mismo
  razonamiento: no tiene sentido "samplear" para Preflight un valor que tampoco se puede leer).
  2 tests nuevos (`AttributeWritabilityRulesTests.cs`) prueban el caso real y confirman que el
  default no cambia el comportamiento de ningún atributo/test existente.
- Umayor Test Data Seeder tiene su propia copia vendored de `DataverseMetadataProviderAdapter.cs`
  (mismo patrón ya visto en 0.6.3 con `DataverseRecordServiceAdapter.cs`) — corregida también,
  por separado, no asumida cubierta por este fix.

### Interno
- `AssemblyVersion`/`AssemblyFileVersion` (plugin XrmToolBox): `0.6.3.0` → `0.6.4.0`. Core sube a
  `0.2.2.0` (esta vez sí cambió contenido real de Core: `MetadataSummaries.cs` y
  `AttributeWritabilityRules.cs`).

## [0.6.3] — Corregido: `RetrieveByIdsAsync` asumía mal la primary key de tablas tipo Activity

### Corregido (crash real reportado por el usuario contra su tenant real)
- **`'ActivityPointer' entity doesn't contain attribute with Name = 'activitypointerid'`** al
  usar la tool hermana Umayor Test Data Seeder. Causa: `DataverseRecordServiceAdapter.RetrieveByIdsAsync`
  asumía que la primary key de CUALQUIER tabla es siempre `<logicalname>id` — cierto para la
  mayoría, pero FALSO para toda entidad de tipo Activity en Dataverse (`email`, `phonecall`,
  `activitypointer`, y cualquier tabla custom de tipo Activity como `wit_evento`/`wit_actividadchat`),
  cuya primary key real es siempre `activityid`. El comentario que documentaba esa suposición
  ("por convención de plataforma, siempre...") era incorrecto — nunca se había verificado contra
  una tabla Activity real hasta ahora.
- **Fix**: `RetrieveByIdsAsync` ahora resuelve la primary key real vía una consulta de metadata
  liviana (`RetrieveEntityRequest` con solo `EntityFilters.Entity`, sin atributos ni relaciones),
  cacheada por tabla para no repetir la consulta. Afecta tanto a este plugin (Retry Failed y el
  chequeo de existencia de Preview Data) como a cualquier consumidor futuro del mismo adaptador.
  Vive en `DataverseRecordServiceAdapter.cs` (proyecto `XrmToolBox`, no `Core`) — Umayor Test
  Data Seeder tiene su propia copia vendored de este mismo archivo (mismo patrón que las DLL de
  `lib/`, no se comparte vía `ProjectReference`) y se corrigió ahí también, por separado.
- Este plugin en sí no expone tablas Activity-type en ningún perfil de ejemplo probado hasta
  ahora, así que el bug estaba latente sin manifestarse — recién se disparó al usarlo desde
  Umayor Test Data Seeder, cuyo mapa de relaciones sí incluye varias.

### Interno
- `AssemblyVersion`/`AssemblyFileVersion` (plugin XrmToolBox): `0.6.2.0` → `0.6.3.0`. `Core.dll`
  no cambió en esta versión (el fix vive en el proyecto `XrmToolBox`), así que no hace falta
  reinstalarlo — solo se sube la versión del plugin, por convención (su propio DLL sí cambió).

## [0.6.2] — Contador de paso en el progreso de Preview Data

### Agregado (encontrado usando la tool hermana Umayor Test Data Seeder)
- `MigrationPreviewBuilder`'s `onProgress` ahora reporta "(i/N)" además del nombre de la tabla
  ("Reading wit_colegio from Source and Target... (12/28)") — una corrida de Preview Data larga
  contra un perfil con varias tablas grandes ya no se ve "pegada" sin ningún indicio de en qué
  paso va. Mismo principio que `LoadEnabledTableMetadata` ya aplicaba a su propio progreso.
- `AssemblyVersion` de `DataverseMasterDataMigrator.Core`: `0.2.0.0` → `0.2.1.0` (mismo motivo que
  la entrada anterior — mantener sincronizadas las copias instaladas de Core entre los dos
  plugins que lo comparten).

### Interno
- `AssemblyVersion`/`AssemblyFileVersion` (plugin XrmToolBox): `0.6.1.0` → `0.6.2.0`.

## [0.6.1] — Corregido: `DataverseMasterDataMigrator.Core.dll` nunca había subido de versión

### Corregido (bug real encontrado probando la tool hermana Umayor Test Data Seeder)
- **`MissingMethodException` real al abrir un segundo plugin (Umayor Test Data Seeder, que
  también referencia `DataverseMasterDataMigrator.Core.dll`) en el mismo XrmToolBox.** Causa
  raíz: `DataverseMasterDataMigrator.Core`'s `AssemblyVersion` quedó fija en `0.1.0.0` desde
  Fase 1 y NUNCA se subió, pese a cientos de cambios reales de código en 20+ versiones. Al haber
  dos plugins distintos en el mismo proceso de XrmToolBox, cada uno con su propia copia de
  `DataverseMasterDataMigrator.Core.dll` en su propia subcarpeta (el patrón de aislamiento ya
  documentado), pero AMBAS copias declarando la MISMA identidad (`Version=0.1.0.0`) pese a tener
  contenido distinto (una más nueva que la otra): el CLR trata ambas copias como intercambiables
  y reutiliza la que se cargó primero para CUALQUIER solicitud posterior de esa misma identidad
  — sin importar desde qué subcarpeta se pidió. El `AssemblyResolveEventHandler` de cada plugin
  intenta filtrar por `RequestingAssembly`, pero ese valor puede llegar `null` en una resolución
  disparada durante JIT profundo en la pila de llamadas, y en ese caso el handler del OTRO
  plugin puede terminar resolviendo (incorrectamente) la petición, entregando una copia vieja.
  El síntoma exacto: `Umayor.TestDataSeeder.dll` (compilado contra el `RetrieveFilteredPageAsync`/
  `entityFilters` de la 0.6.0) recibía en tiempo real la copia de Core instalada por
  `DataverseMasterDataMigrator` (más vieja, sin ese método) — `MissingMethodException` en
  `MigrationPreviewBuilder.BuildAsync`.
- **Fix**: `AssemblyVersion`/`AssemblyFileVersion` de `DataverseMasterDataMigrator.Core` sube por
  primera vez, de `0.1.0.0` a `0.2.0.0`, y de ahora en más debe subir en cualquier cambio real de
  Core — mismo principio ya aplicado al ensamblado del plugin (sección "caché de XrmToolBox por
  `AssemblyQualifiedName`"), extendido a esta dependencia compartida. Verificado reinstalando
  AMBOS plugins con el Core recompilado y comparando el hash MD5 de las dos copias instaladas
  (idénticas tras el fix, antes NO lo eran — la del plugin original quedó desactualizada desde
  antes del cambio de la 0.6.0 sin que nada lo detectara).
- Lección para cualquier futura tool que reutilice este Core como librería compartida: un
  ensamblado privado sin nombre fuerte (`AssemblyResolveEventHandler` manual, sin binding
  redirects reales) que se distribuye con MÁS DE UN plugin en el mismo proceso necesita que su
  propio `AssemblyVersion` refleje cambios reales — de lo contrario, dos copias con contenido
  distinto pero la misma versión declarada son indistinguibles para el CLR, y "cuál gana" queda
  librado al orden de carga, no al contenido real de cada archivo.

### Interno
- `AssemblyVersion`/`AssemblyFileVersion` (plugin XrmToolBox): `0.6.0.0` → `0.6.1.0` (para que la
  caché de metadata de XrmToolBox note el cambio, aunque el único cambio real sea la versión de
  Core que trae empaquetada).

## [0.6.0] — Lectura filtrada por registro (`RecordFilter`) en Core

### Agregado (base para una tool separada de extracción/anonimización por sujeto, a pedido explícito)
- **Nuevo `IDataverseRecordService.RetrieveFilteredPageAsync`**: igual que `RetrievePageAsync`
  pero acotado por un `RecordFilter` opcional (`Core/Models/RecordFilter.cs` — condiciones
  `Equal`/`NotEqual`/`In` sobre atributos, combinables con `And`/`Or`, con anidamiento
  arbitrario vía `SubFilters`). `RetrievePageAsync` ahora delega a este método con `filter: null`,
  así que su comportamiento no cambió en absoluto.
- **`ProfileEntity.Filter` (existía desde Fase 1 como `string` sin ningún uso real — un
  placeholder muerto) ahora es un `RecordFilter` real, y de verdad se respeta** en los tres
  lugares que leen registros de una tabla del perfil: `MigrationExecutor` (Pass 1, Execute y
  Retry Failed), `ExternalLookupSampler` (muestreo de Preflight) y `MigrationPreviewBuilder`
  (Preview Data — nuevo parámetro opcional `entityFilters`, wireado desde `PluginControl.OnPreviewData`).
  Regla de oro verificada explícitamente: `Filter == null` (el único caso que existe hoy, ningún
  perfil real usa el campo todavía) se comporta exactamente igual que antes de este cambio en
  los tres call sites — ningún perfil existente cambia de conducta.
- Motivación real: una tool separada para Umayor (extraer el grafo de registros de un RUT
  puntual desde Producción, anonimizarlo y migrarlo a un entorno bajo para tener datos de prueba
  reales) necesita esta misma capacidad de lectura acotada. En vez de duplicarla en un fork, se
  agregó al Core compartido — la tool nueva reutilizará `RetrieveFilteredPageAsync` (a través de
  una futura implementación real de `IRecordSelector`, ver `Abstractions/ExtensibilityPorts.cs`)
  en vez de reinventar su propio mecanismo de consulta. Esta versión NO incluye esa tool ni
  ningún control de UI nuevo para editar `filter` desde el plugin — es solo la capacidad de Core.
- Documentado en `docs/MIGRATION_PROFILES.md` (forma real del JSON de `filter`) y
  `Abstractions/ExtensibilityPorts.cs` (`IRecordSelector`, referencia al nuevo método).
- 3 tests nuevos (uno por call site) confirman que un `Filter` seteado realmente acota qué
  registros se migran/muestrean/previsualizan — sin tocar ninguno de los ~105 tests existentes.

### Corregido (encontrado en revisión, antes de llegar a producción)
- El primer borrador de la traducción de `FilterOperator.In` (tanto en el adaptador real como en
  los fakes de test) trataba cualquier `IEnumerable` como una lista de valores candidatos — pero
  un `string` también implementa `IEnumerable<char>`, así que un valor `In` con un único string
  se habría troceado en caracteres individuales en vez de tratarse como un solo candidato.
  Corregido excluyendo `string` explícitamente antes de intentar enumerar.
- `PluginControl.OnPreviewData`: el primer wireo de `entityFilters` reusaba `e` como nombre de
  variable de lambda dentro de un event handler `(object sender, EventArgs e)` — no compilaba
  (`CS0136`, nombre ya usado en el ámbito envolvente). Corregido renombrando la variable.

### Interno
- `AssemblyVersion`/`AssemblyFileVersion` (plugin XrmToolBox): `0.5.4.0` → `0.6.0.0`.
- `DataverseMasterDataMigrator.Core.csproj` ganó `<Compile Include="Models\RecordFilter.cs" />`
  (proyecto de formato clásico, sin glob automático de archivos nuevos).

## [0.5.4] — Descripción del About más concreta sobre qué hace la app

### Cambiado (feedback de uso real — la descripción anterior no explicaba qué hace la app)
- El párrafo de descripción del diálogo About pasó de una frase genérica ("perfiles reutilizables
  y persistentes, en vez de migraciones manuales tabla por tabla") a nombrar las capacidades
  concretas: validación de dependencias y lookups (Preflight), comparación de estructura
  Source/Target, previsualización de cambios y ejecución multipass con reintento de fallos.
  `ClientSize` del diálogo subido de 380 a 400px de alto para darle espacio al texto más largo
  (verificado sin abrir ninguna ventana visible: instanciando el `Form` real y forzando `Handle`
  sin `Show()`/`ShowDialog()` — ningún control se solapa con el botón "Cerrar").

### Interno
- `AssemblyVersion`/`AssemblyFileVersion`: `0.5.3.0` → `0.5.4.0`.

## [0.5.3] — Diálogo About al estilo de Metadata Dataverse Document

### Cambiado (a pedido explícito, con captura de referencia del plugin hermano)
- El diálogo "About" ahora sigue el mismo estilo que el de Metadata Dataverse Document: link
  real "Enlace al repositorio" en el propio encabezado (apuntando a
  https://github.com/RmunozMM/Dataverse-Master-Data-Migrator, ya público), descripción y
  etiquetas de contacto en español ("Desarrollador:", "Sitio Web:", "Contacto:"), copyright en
  negrita con el año antes del nombre ("Copyright © {año} Rogelio Muñoz. Todos los derechos
  reservados."), y botón "Cerrar". Reemplaza el placeholder anterior "Repository: coming soon"
  — el repositorio ya está publicado, ya no aplicaba. El título de la ventana se dejó igual en
  inglés, tal como en la referencia.
- Ajuste menor de robustez encontrado en revisión (no afecta nada visible hoy): el
  `FlowLayoutPanel` del encabezado que agrupa versión + link ahora usa
  `AutoSizeMode.GrowAndShrink` en vez del `GrowOnly` por default, para que su `Bounds` real
  coincida con el contenido — verificado sin abrir ninguna ventana visible, instanciando el
  `Form` real y forzando `Handle` sin `Show()`/`ShowDialog()`.

### Interno
- `AssemblyVersion`/`AssemblyFileVersion`: `0.5.2.0` → `0.5.3.0`.

## [0.5.2] — Botón "Clear Log"

### Agregado (feedback de uso real)
- Nuevo botón "Clear Log" en la pestaña Migration, junto a "View Log": vacía el `TextBox` del
  log de la sesión actual. No afecta ninguna operación en curso ni el `ExecutionManifest`
  persistido — solo limpia lo mostrado en pantalla.

### Interno
- `AssemblyVersion`/`AssemblyFileVersion`: `0.5.1.0` → `0.5.2.0`.

## [0.5.1] — SkipSilently real para lookups opcionales no resueltos

### Agregado (funcionalidad pendiente desde hacía dos versiones, implementada a pedido explícito)
- **El motor de ejecución ahora respeta de verdad `OptionalLookupPolicy = SkipSilently`**: antes,
  Preflight solo cambiaba la severidad del aviso, pero el valor se seguía escribiendo igual y
  fallaba contra Dataverse ("Entity 'X' Does Not Exist"). Causa real documentada de 66 de 96
  fallos en una corrida real, en cascada a través de 3 tablas dependientes. Ahora, cuando el
  lookup opcional (o el obligatorio, vía `RequiredLookupPolicy`) apunta fuera del perfil y el GUID
  no existe en Target, ese atributo puntual se omite del payload de escritura — el registro se
  sigue creando/actualizando, solo sin ese campo. Aplica tanto a Execute como a Retry Failed.
- **Nuevo checkbox en la pestaña Profiles**: "Skip unresolved optional lookups silently" — sin
  esto la funcionalidad quedaba inalcanzable desde la UI (el campo existía en el modelo pero
  nada lo exponía). Solo cubre `OptionalLookupPolicy`; `RequiredLookupPolicy` queda fuera de
  alcance (un skip de campo obligatorio es una decisión más delicada, no pedida).
- 9 tests nuevos en el motor de ejecución (omisión real del atributo, comportamiento por defecto
  sin cambios, caché de `ExistsAsync`, camino de Retry Failed, y el mismo guard de referencia
  polimórfica "entity" ya usado en Preflight — encontrado en revisión antes de llegar a
  producción, no reportado por el usuario).

### Interno
- `AssemblyVersion`/`AssemblyFileVersion`: `0.5.0.0` → `0.5.1.0`.

## [0.5.0] — Comparador de estructura Source/Target (HTML + Excel)

### Agregado (funcionalidad nueva, a pedido explícito del usuario)
- **Nuevo botón "Compare Structure"** en la pestaña Migration: compara la estructura (no los
  datos) de todas las tablas habilitadas del perfil entre Source y Target — por tabla, existencia
  en cada lado; por atributo, existencia, tipo y obligatoriedad. Reutiliza la misma metadata que
  ya carga Preflight (`LoadEnabledTableMetadata`), sin llamadas nuevas a Dataverse.
- **Exportación a HTML**: un único archivo autocontenido (sin recursos externos) con un índice
  navegable arriba y una sección por tabla más abajo, cada una con link "Volver al índice". Se
  abre automático en el navegador al exportar.
- **Exportación a Excel**: un libro con hoja "Índice" (hipervínculos a cada hoja de tabla) y una
  hoja por tabla (con link de vuelta al índice), vía la librería **ClosedXML** — nueva dependencia
  del proyecto, autorizada explícitamente por el usuario. Nombres de hoja saneados y truncados al
  límite real de Excel (31 caracteres, sin `\ / ? * [ ] :`), con sufijo `~2`/`~3` si dos tablas
  colisionan después de truncar.
- Objetivo explícito de esta funcionalidad: homologar Source y Target *antes* de migrar datos,
  detectando de antemano tablas/columnas faltantes (el mismo tipo de problema que ya reportaba
  Preflight como `TABLE_NOT_IN_TARGET`/`ATTRIBUTE_NOT_IN_TARGET`, ahora como reporte completo
  exportable, no solo como advertencia bloqueante puntual).
- 21 tests nuevos (modelos + comparador + exportador HTML + exportador Excel, este último con
  round-trip real leyendo el .xlsx generado, no solo generándolo).

### Corregido (encontrado en revisión antes de llegar al usuario)
- **Crash real de dependencias** en el exportador Excel: ClosedXML/SixLabors.Fonts necesitan
  `System.Buffers`/`System.Memory`/`System.Numerics.Vectors`/`System.Runtime.CompilerServices.Unsafe`,
  ausentes del build original y con un desajuste de versión real entre lo que SixLabors.Fonts
  pide (`System.Numerics.Vectors 4.1.3.0`) y lo que NuGet resuelve (`4.1.4.0`) — sin binding
  redirect disponible (el plugin corre dentro del `app.config` de XrmToolBox.exe, no el propio).
  Detectado cargando el plugin real desde una carpeta con SOLO los DLL que se iban a distribuir
  (no alcanza con que compile o que pasen los tests — el proyecto de tests es SDK-style y
  enmascara este problema). Corregido extendiendo el `AssemblyResolveEventHandler` ya existente
  en `Plugin.cs` con una lista de redirección forzada para esos 4 ensamblados específicos.
  `Install-DataverseMasterDataMigrator.ps1` actualizado para copiar las 11 DLL de la cadena de
  ClosedXML a la subcarpeta propia (nunca a la raíz de Plugins).
- Bug de layout real (mismo patrón de siempre) en el diálogo nuevo de exportación: el panel del
  botón "Close" no tenía `AutoSize`, se superponía 17px con el panel de botones de exportación.
  Verificado (y corregido) sin abrir ninguna ventana visible: forzando el handle de la ventana vía
  reflection y midiendo `Bounds` reales, en vez de renderizarla.

### Interno
- `AssemblyVersion`/`AssemblyFileVersion`: `0.4.9.0` → `0.5.0.0`.

## [0.4.9] — Botón "Skip Unchanged" en Data Preview

### Agregado (feedback de uso real)
- Nuevo botón "Skip Unchanged" junto a Select All/None/New Only/Updates Only: destilda los
  registros "Update" cuyo nombre no cambia entre Source y Target (la misma clasificación que ya
  se pintaba como "⬜ Update, no change" en la leyenda), dejando tildados los New y los Update
  con nombre realmente distinto.
- Deliberadamente NO hace comparación campo por campo de todos los atributos — se descartó esa
  opción en conjunto con el usuario por costo: en una tabla como `contact` (~900 columnas), el
  costo escala con columnas × registros y se dispara rápido en tablas grandes. "Unchanged" acá
  significa "el nombre no cambia", no "el registro es 100% idéntico".

### Interno
- `AssemblyVersion`/`AssemblyFileVersion`: `0.4.8.0` → `0.4.9.0`.

## [0.4.8] — Corregido de raíz: filas de Data Preview tapadas por la barra superior

### Corregido (causa real confirmada por el usuario, arreglo estructural — no otro parche)
- **Los registros se renderizaban bien pero quedaban tapados visualmente por la barra superior**
  (combo de tabla + leyenda + botones) — el usuario confirmó que el alto oculto coincidía
  exactamente con el alto de esa barra. Van tres bugs reales distintos en este mismo diálogo
  originados en la misma causa raíz: apilar controles con `Dock` directo sobre el Form,
  dependiendo de una convención de orden de Z-order ("el último agregado gana el borde") fácil
  de romper sin darse cuenta. En vez de parchear el síntoma otra vez, se reemplazó esa pila por
  un `TableLayoutPanel` con una celda explícita por barra — el mismo patrón que ya funciona sin
  problemas en la pestaña Tables (`BuildTablesTab`). Con celdas explícitas no hay ambigüedad de
  orden posible: la fila `Percent(100)` de las grillas nunca puede superponerse con las filas
  `AutoSize` de arriba.
- El intento anterior de reproducir esto abriendo una ventana de prueba real en la máquina del
  usuario fue un error — esta terminal corre sobre su escritorio real, no un sandbox aislado, y
  terminó capturando pantalla sensible por accidente. Se abortó esa vía; el diagnóstico final se
  hizo por lectura de código y evidencia visual que el propio usuario aportó.

### Interno
- `AssemblyVersion`/`AssemblyFileVersion`: `0.4.7.0` → `0.4.8.0`.

## [0.4.7] — Corregido: primeros registros ocultos al cambiar de tabla en Data Preview

### Corregido (feedback de uso real — una tabla de 8 registros solo mostraba los últimos 4)
- Al cambiar de tabla en el combo de Data Preview, la grilla no reseteaba la posición de scroll
  ni la fila seleccionada de la tabla anterior. Si se venía de una tabla más grande y con scroll,
  esa posición quedaba pegada al pasar a una tabla más chica, ocultando sus primeros registros
  sin ningún indicio visible de que había que scrollear. Se resetea `FirstDisplayedScrollingRowIndex`
  y la selección de ambas grillas cada vez que se elige una tabla nueva.
- Nota abierta (no bloqueante): si además de los registros faltaban los encabezados SOURCE/TARGET
  y las columnas Id/Name/Status, eso no lo explica este fix — pedirle confirmación al usuario de
  si eso pasaba de verdad en la app o era solo un recorte del screenshot.

### Interno
- `AssemblyVersion`/`AssemblyFileVersion`: `0.4.6.0` → `0.4.7.0`.

## [0.4.6] — Corregido: 0.4.5 también bloqueaba tablas propias del usuario

### Corregido (regresión real de 0.4.5, confirmada en vivo por el usuario)
- **0.4.5 excluía de "Find Related Tables" cualquier tabla `IsManaged`, pero `IsManaged` solo
  refleja la capa de solución ACTUAL, no de quién es la tabla** — si el propio tenant despliega
  sus customizaciones (`wit_*`) como solución administrada en Source (patrón normal de ALM:
  desarrollar sin administrar, distribuir administrado), sus propias tablas quedaban excluidas
  también. Confirmado en vivo: `wit_alumnomatriculaweb` (dependencia real de `wit_matriculaweb`)
  dejó de sugerirse tras 0.4.5.
- Se reemplazó el criterio por una lista de prefijos reservados de Microsoft en todo Dataverse
  (`adx_`, `mspp_`, `msdyn_`, `msdynmkt_`, `msdyncrm_`) — ningún tenant puede usar esos prefijos
  para su propia solución, así que es una señal confiable independiente de cómo cada organización
  despliega sus propias customizaciones. Se revirtió `TableSummary.IsManaged` (quedaba sin uso).

### Interno
- `AssemblyVersion`/`AssemblyFileVersion`: `0.4.5.0` → `0.4.6.0`.

## [0.4.5] — Find Related Tables ya no sugiere tablas de soluciones administradas de Microsoft

### Corregido (feedback de uso real — la recursión de 0.4.3 arrastraba tablas no deseadas)
- **"Find Related Tables" empezó a sugerir decenas de tablas `adx_`/`mspp_` (Power Pages),
  `msdyn_` (Copilot/IoT) y `msdynmkt_` (Dynamics Marketing)** en un tenant real, al hacerse
  recursivo en 0.4.3. Causa: `IsCustomEntity` de Dataverse es `true` para cualquier tabla fuera
  de la solución base, incluyendo las instaladas por soluciones **administradas** de Microsoft —
  "custom" en la metadata no significa "creada por este tenant". Se agregó `TableSummary.IsManaged`
  (mapeado de `EntityMetadata.IsManaged` real, verificado contra el SDK) y ahora solo se sugieren
  tablas `IsCustomEntity && !IsManaged` — genuinamente propias y no administradas.
- Cambio acotado a la sugerencia automática únicamente: el filtro manual "Custom" de la pestaña
  Tables sigue mostrando todas las tablas custom (administradas o no), para poder elegirlas a
  mano si hace falta.
- Caveat conocido (no bloqueante): si el propio tenant empaqueta sus customizaciones como
  solución administrada (patrón válido de ALM), esas tablas propias tampoco se sugerirán
  automáticamente — solo quedan disponibles por selección manual en Tables. No hay forma de
  distinguir ese caso del de una solución de Microsoft solo con `IsManaged`.

### Interno
- `AssemblyVersion`/`AssemblyFileVersion`: `0.4.4.0` → `0.4.5.0`.

## [0.4.4] — Corregido: crash real "The 'Retrieve' method does not support entities of type 'entity'"

### Corregido (crash real reportado, causa raíz identificada, no supuesta)
- **Preflight fallaba por completo** con `The 'Retrieve' method does not support entities of type
  'entity'`. Causa: algún registro de una tabla del perfil (probablemente una `msdyn_*` de
  Copilot/AI Builder, ya vistas en el ciclo de dependencias de un log anterior) tiene un lookup
  genuinamente polimórfico/sin tipo declarado, cuyo valor llega con `LogicalName` literal
  `"entity"` — un placeholder, no una tabla real. `ExternalLookupSampler` lo pasaba igual a
  `ExistsAsync`, y Dataverse rechaza ese nombre de plano, tirando abajo TODA la corrida de
  Preflight por un solo valor no evaluable. Ahora se saltea ese valor puntual (no se cuenta como
  faltante, no se consulta a Target) en lugar de explotar.
- **Mismo riesgo encontrado (en revisión, antes de que ocurriera en producción) en el camino de
  escritura real**: `DataverseRecordServiceAdapter.ToSdkEntity` (usado por Create/Update en
  Execute y Retry Failed) armaba un `EntityReference("entity", id)` sin filtrar, lo cual también
  habría sido rechazado por Dataverse si alguna de esas tablas llegara a migrarse de verdad.
  Corregido con el mismo criterio: se omite ese atributo puntual del payload de escritura para
  ese registro, en vez de fallar el registro/lote entero.
- 1 test nuevo (`ReferenceWithGenericEntityLogicalName_IsSkippedNotSampled`) cubre el caso en
  `ExternalLookupSampler`. El fix en `DataverseRecordServiceAdapter` no tiene test dedicado —
  es un método privado que habla con el SDK real, sin arnés de pruebas existente para esa clase.

### Interno
- `AssemblyVersion`/`AssemblyFileVersion`: `0.4.3.0` → `0.4.4.0`.

## [0.4.3] — Find Related Tables recursivo + grilla en blanco en Data Preview

### Agregado (feedback de uso real)
- **"Find Related Tables" ahora es recursivo**: antes solo escaneaba las tablas ya tildadas por
  el usuario, así que una dependencia recién agregada podía a su vez depender de otra tabla
  custom no incluida — el usuario tenía que correr el botón varias veces a mano y Preflight
  seguía encontrando nuevos lookups sin resolver ronda tras ronda. Ahora hace un BFS: cada
  dependencia nueva encontrada también se escanea por sus propias dependencias, hasta que no
  aparece nada nuevo. Con guard contra ciclos (`scanned`) y contra reprocesar la misma tabla dos
  veces.

### Corregido (bug real reportado con screenshot — grilla completamente en blanco)
- **El diálogo Data Preview abría con la franja superior (combo, leyenda, botones) visible pero
  el área de las grillas totalmente en blanco** — sin encabezados SOURCE/TARGET, sin filas —
  pese a que el combo mostraba correctamente "2 total, 2 new, 0 update". Misma causa que el
  crash de `SplitterDistance` de una versión anterior: el constructor seleccionaba la primera
  tabla del combo (lo que dispara el binding de `DataSource` de ambas grillas) ANTES de que el
  formulario hiciera su primer layout real. Se movió esa selección inicial a `Load`, después de
  que `SplitterDistance`/`Panel1MinSize`/`Panel2MinSize` ya se ajustaron. Nota de honestidad: es
  un bug de renderizado WinForms que no se puede verificar 100% sin probarlo en la app real —
  pedirle al usuario que confirme después de instalar esta versión.

### Interno
- `AssemblyVersion`/`AssemblyFileVersion`: `0.4.2.0` → `0.4.3.0`.

## [0.4.2] — Corregido: Preflight fallaba siempre tras 0.4.1 ("Este BackgroundWorker indica que no notifica el progreso")

### Corregido (regresión real de 0.4.1, causa confirmada decompilando el host, no supuesta)
- **0.4.1 rompió Preflight/Preview/Find Related Tables por completo**: el código llamaba
  `worker.ReportProgress(...)` dentro de `WorkAsyncInfo.Work`, pero XrmToolBox arma su
  `BackgroundWorker` interno con `WorkerReportsProgress = (info.ProgressChanged != null)` — y
  nunca seteábamos `ProgressChanged`, así que `ReportProgress` tiraba
  `InvalidOperationException` siempre. Confirmado decompilando `XrmToolBox.Extensibility.dll`
  real (con `ilspycmd`), no asumido — la última vez que se diagnosticó mal un problema similar en
  este proyecto (el crash de `SplitterDistance`) costó una vuelta extra innecesaria, así que esta
  vez se verificó contra el binario del host antes de tocar código.
- Además, esa vía tampoco habría mostrado nada aunque no explotara: el texto visible del modal
  de progreso nunca se actualiza desde `ProgressChanged` en el código del host — el mecanismo
  real para eso es `SetWorkingMessage(texto)`, un método heredado (`PluginControlBase`) que
  internamente hace `host.Invoke(...)` para actualizar el mismo panel de forma seguro desde el
  hilo de background.
- `LoadEnabledTableMetadata` perdió el parámetro `BackgroundWorker worker` (ya no hace falta) y
  ahora llama `SetWorkingMessage(...)` directamente; los 5 lugares que reportaban progreso
  (Preflight, Preview Data, Find Related Tables, y los callbacks `onProgress` hacia
  `ExternalLookupSampler`/`MigrationPreviewBuilder`) se movieron al mismo mecanismo. Verificado:
  cero referencias a `ReportProgress`/`ProgressChanged` en todo el código del plugin.

### Interno
- `AssemblyVersion`/`AssemblyFileVersion`: `0.4.1.0` → `0.4.2.0`.

## [0.4.1] — Progreso real y cancelación en Preflight / Preview / Find Related Tables

### Agregado (feedback de uso real — "lleva 5 minutos y no sé si se quedó pegado")
- **Progreso en vivo dentro del mismo diálogo modal de XrmToolBox**: Preflight, Preview Data y
  Find Related Tables ahora reportan qué tabla están procesando en cada momento (vía
  `BackgroundWorker.ReportProgress`, que XrmToolBox ya muestra dentro de su propio modal "Running
  Preflight..."), en lugar del texto estático de siempre. Causa real del bloqueo aparente:
  `LoadEnabledTableMetadata` hace 2 llamadas de metadata por tabla habilitada (Source + Target) y
  `ExternalLookupSampler` consulta Target una vez por cada GUID externo distinto — con perfiles
  grandes esto son minutos reales de trabajo, no un cuelgue.
- **Botón Cancel real** para estas tres operaciones (antes solo Execute/Retry Failed lo tenían):
  reutiliza el mismo `_currentOperationCts`/`_btnCancel` ya probado en Execute. Cancelar antes de
  o durante el muestreo de lookups externos corta en el próximo checkpoint (como máximo, una
  llamada de red en curso); cancelar durante la carga de metadata corta tabla por tabla — ambos
  casos verificados leyendo el código real, no asumidos.
- `ExternalLookupSampler.SampleAsync` y `MigrationPreviewBuilder.BuildAsync` (Core) ganaron un
  parámetro opcional `Action<string> onProgress = null` — no rompe ningún caller existente. 4
  tests nuevos cubren que se invoca correctamente y que omitirlo sigue siendo seguro.

### Pendiente (próxima fase, cuando aplique)
- Al hacer clic en Preflight/Preview/Find Related Tables mientras otra de estas operaciones ya
  está corriendo, solo se deshabilita el botón de la operación en curso — los otros disparadores
  quedan habilitados. En la práctica el modal de XrmToolBox probablemente bloquea la interacción
  con el resto del control mientras corre, pero no se pudo confirmar 100% sin ejecutar la UI real
  contra un tenant grande. Si se llegara a reproducir un doble-click real, deshabilitar los 5
  botones disparadores (no solo el propio) durante cualquiera de estas operaciones.
- Cancelar Execute/Retry Failed sigue mostrando el MessageBox genérico de error en vez del
  mensaje amigable "cancelado" que ahora tienen Preflight/Preview/Find Related Tables — no se
  tocó su manejo de cancelación existente en esta pasada.

### Interno
- `AssemblyVersion`/`AssemblyFileVersion`: `0.4.0.0` → `0.4.1.0`.

## [0.4.0] — Rediseño del flujo Profiles / Tables

### Cambiado (feedback de uso real — se cometían errores al mezclar "crear perfil" y "seleccionar tablas" en pestañas distintas)
- **La pestaña Tables ahora es el único lugar donde se edita la membresía de tablas de un
  perfil.** Nueva barra fija arriba de la lista, siempre visible, con: combo para elegir el
  perfil activo (`_tablesProfileCombo`), botón "New Profile", botón "Save Changes" (solo
  habilitado si hay cambios sin guardar) y una etiqueta "Editing profile: `<nombre>`" que agrega
  "• unsaved changes" en naranja cuando los checks difieren de lo persistido — nunca más queda
  ambiguo a qué perfil se están aplicando los checks.
- **La pestaña Profiles pasa a ser administración pura**: nombre/descripción, New/Save/Save
  As/Delete/Reload/Open Profiles Folder, y un nuevo botón "Edit Tables →" que lleva a Tables. El
  listado de tablas del perfil ahí sigue siendo de solo lectura (vista previa de lo ya guardado).
  Se eliminó el botón "Create Profile from Selection" — quedaba redundante y ambiguo frente al
  nuevo flujo (era exactamente la causa del problema: crear un perfil "de paso" desde Tables sin
  que quedara claro cuál era el perfil realmente activo).
- **Protección contra pérdida de cambios**: cambiar de perfil activo (desde cualquiera de los
  dos combos) con selección de tablas sin guardar ahora pregunta antes de descartar. Los dos
  combos (Profiles y Tables) siempre muestran el mismo perfil activo, sincronizados con un
  guard de reentrancia (`_syncingProfileCombos`) para no disparar el diálogo de confirmación en
  bucle al revertir la selección tras un "No".
- `SaveCurrentProfile()`: lógica de guardado unificada, usada tanto por "Save" (Profiles) como
  por "Save Changes" (Tables) — evita que las dos superficies terminen divergiendo. Fija
  `_settings.LastProfileId` antes de recargar la lista de perfiles, para que guardar un perfil
  recién creado (nunca cargado antes por combo) no termine mostrando el perfil anterior por
  encima del que se acaba de guardar.

### Corregido (encontrado en revisión, no reportado por el usuario)
- El botón "Save As" podía lanzar `NullReferenceException` si era la primera acción del usuario
  sobre un perfil (sin haber cargado ni creado ninguno todavía) — leía `_currentProfile.Id`
  directo, sin pasar por el mismo resguardo contra perfil nulo que ya tenía "Save". Corregido
  para crear un perfil nuevo primero si hace falta, igual que el resto de las acciones de guardado.

### Interno
- `AssemblyVersion`/`AssemblyFileVersion`: `0.3.3.0` → `0.4.0.0`.

## [0.3.3] — Semáforo de Preflight, versión visible, altura real de Data Preview

### Agregado
- **Semáforo de estado junto a los botones de Migration**: un punto de color + etiqueta,
  actualizado al terminar cada Preflight — 🔴 "Bloqueado" (hay errores que impiden migrar), 🟡
  "Con advertencias" (puede migrar, hay warnings), 🟢 "Listo para migrar" (sin observaciones).
  Gris/"Sin analizar" antes de correr el primer Preflight. Refleja exactamente el mismo criterio
  que ya habilitaba el botón Execute (`ReadyToExecute`/`HasWarnings`), ahora visible de un
  vistazo sin tener que leer el log.
- **Número de versión siempre visible**: la barra superior persistente (la misma que tiene el
  botón "About", visible en todas las pestañas) ahora muestra `vX.Y.Z`, leído en tiempo real del
  ensamblado (`AssemblyVersion`) — nunca queda desactualizado a mano.

### Corregido (Data Preview seguía viéndose con la parte superior muy baja de altura)
- Las franjas superiores del diálogo (`tableBar`, leyenda de colores, botones de selección
  masiva, aviso de truncado) dependían de `AutoSize` para calcular su alto, lo cual — igual que
  en los dos bugs de layout anteriores en este mismo diálogo — resultó poco confiable. Se
  reemplazó por una altura fija explícita en cada una (44 / 34 / 40 / 30 px), siguiendo el mismo
  patrón que ya funcionaba bien en el encabezado de cada panel (`BuildPane`, `Height = 24`). La
  altura de la leyenda se ajustó dos veces (26 → 34) tras detectar en revisión que el texto con
  emojis necesita más alto de línea que texto plano del mismo tamaño de fuente.

### Interno
- `AssemblyVersion`/`AssemblyFileVersion`: `0.3.2.0` → `0.3.3.0` — necesario para que la caché
  de metadata de plugins de XrmToolBox (`Plugins\manifest.json`, indexada por
  `AssemblyQualifiedName`, que incluye la versión) no reutilice una entrada vieja.

## [0.3.0] — Fase 3: motor de ejecución real + instalador

### Agregado
- `Core/Migration/MigrationExecutor.cs`: motor multipass real (Pass 1 create/update, Pass 2
  lookups diferidos, Pass 3 statecode/statuscode + N:N), conectado al botón Execute en
  `PluginControl.OnExecute`. Usa `MigrationPlanner` para el orden real (antes se pasaba metadata
  vacía y el orden colapsaba a `preferredOrder`), pagina con `RetrievePageAsync`, escribe con
  `IDataverseRecordService.WriteBatchAsync`, reintenta con `RetryPolicy`, y persiste checkpoints
  con `ExecutionManifestStore` después de cada chunk.
- `RecordOperationResult.IsTransient` / `RetryAfterHint`: el adaptador XrmToolBox clasifica cada
  fallo (network transitorio, o `OrganizationServiceFault.ErrorDetails["Retry-After"]`) para que
  el Core decida el reintento sin conocer el SDK (ARCHITECTURE.md sección 8).
- 5 tests nuevos en `MigrationExecutorTests.cs` (dependencia lineal, lookup auto-referenciado
  diferido a Pass 2, retry transitorio, `stopOnError`, cancelación) — sin mocks, con un
  `IDataverseRecordService` fake en memoria, mismo estilo que el resto de la suite.

### Corregido (encontrado compilando de verdad, no revisión visual)
- `RetryPolicyTests.cs`: `TimeSpan * double` no existe en .NET Framework (sí en .NET Core/5+);
  las dos aserciones que lo usaban no compilaban contra el `net48` real.
- `OnExecute` construía el plan con `TableSummary` vacíos (sin `Attributes`), así que
  `DependencyGraphBuilder` no veía ningún lookup y el orden de ejecución ignoraba las
  dependencias reales. Ahora reutiliza la misma carga de metadata completa que Preflight
  (`LoadEnabledTableMetadata`).
- Entorno de build sin Visual Studio ni Developer Pack de .NET Framework instalados: se agregó
  `PackageReference Include="Microsoft.NETFramework.ReferenceAssemblies"` a `Core.csproj` y
  `XrmToolBox.csproj` — deja compilar contra `net48` real (con Roslyn moderno, no el `csc.exe`
  viejo de `v4.0.30319`) sin depender de una instalación a nivel de sistema. No afecta una
  máquina que sí tenga el Developer Pack instalado (VS 2022).

### Agregado (continuación)
- `Core/Validation/ExternalLookupSampler.cs`: muestreo real de lookups externos al perfil para
  Preflight — pagina Source y verifica existencia en Target una vez por GUID distinto (no una
  vez por registro). Conectado a `OnPreflight` (antes pasaba una lista vacía siempre).
- `IDataverseRecordService.RetrieveByIdsAsync` + `MigrationExecutor.RetryFailedAsync`: Retry
  Failed real — agrupa las fallas del último `ExecutionManifest` por (tabla, pass) y solo
  vuelve a consultar esos IDs puntuales en Source, sin re-planificar ni re-paginar tablas ya
  completadas. Conectado al botón "Retry Failed".
- 7 tests nuevos (`ExternalLookupSamplerTests.cs`, más 3 en `MigrationExecutorTests.cs` para
  Retry Failed, incluido un caso que retenta solo un lookup diferido de Pass 2 sin re-crear el
  registro).

### Corregido (continuación — encontrado al instalar contra el XrmToolBox real, no revisión visual)
- **Los DLL en `lib/` estaban desactualizados respecto al XrmToolBox realmente instalado en esta
  máquina.** Comparando `AssemblyName.Version` (no solo tamaño de archivo) contra
  `C:\ProgramData\XrmToolBox\Update\`, 3 de 9 DLL diferían: `McTools.Xrm.Connection.dll`
  (`1.2025.7.63` → `1.2025.9.64`), `McTools.Xrm.Connection.WinForms.dll` (iguales en tamaño mas
  no en versión) y `XrmToolBox.Extensibility.dll`/`XrmToolBox.ToolLibrary.dll` (`1.2025.?` →
  `1.2025.10.74`). Compilaba sin error contra las viejas, pero arriesgaba un
  `MissingMethodException`/`FileLoadException` real al cargar en tu XrmToolBox actual. Las 9
  DLL se re-copiaron directo desde la instalación live (ver `lib/README.txt`), y la solución
  compila limpio (0 warnings de conflicto de versión) contra las nuevas.

### Agregado (instalador y empaquetado)
- `Instalar-Plugin.bat` + `Install-DataverseMasterDataMigrator.ps1`: mismo patrón que Metadata
  Dataverse Document — DLL principal a la raíz de `Plugins\`, `DataverseMasterDataMigrator.Core.dll`
  a su propia subcarpeta (nunca a la raíz), y deliberadamente SIN copiar los DLL del host
  (`Microsoft.Xrm.Sdk.dll`, `XrmToolBox.Extensibility.dll`, etc. — ya los carga XrmToolBox.exe
  mismo; duplicarlos sería el riesgo de versión que este mismo patrón evita).
- `tools/verify_attr_blobs.py` corrido contra el DLL de Release real: 0 blobs `ExportMetadata`
  inválidos (con los íconos placeholder actuales; re-correr al reemplazarlos por íconos reales
  más pesados).
- `dist/DataverseMasterDataMigrator-0.3.0-Installation.zip`: solo el instalador + los 2 DLL
  necesarios, sin código fuente.
- **Instalado y verificado en tu XrmToolBox real** (`C:\CT\XrmToolbox`, sincronizado con
  `C:\ProgramData\XrmToolBox\Update`): reinicié el proceso y siguió corriendo sin crash ni nuevo
  crash dump — no pude confirmar visualmente que el tile aparece en la lista (no tengo forma de
  capturar la ventana de una app de escritorio nativa desde este entorno), pídele una mirada
  rápida a la pantalla para confirmar que no salió un diálogo de error de MEF.

### Corregido (encontrado al instalar contra el XrmToolBox real, no revisión visual)
- Ver la entrada de arriba sobre `lib/` desactualizado — mismo hallazgo, aplicado también antes
  de instalar.

### Corregido (encontrado abriendo el XrmToolBox real, no revisión visual)
- `DataverseMasterDataMigrator.XrmToolBox` nunca tuvo `Properties/AssemblyInfo.cs` (a
  diferencia de `Core`, que sí lo tiene) — el ensamblado compilado no tenía NINGÚN atributo de
  nivel de ensamblado (`AssemblyCompany`, etc.). Al abrir XrmToolBox con el plugin instalado,
  `XrmToolBox.Extensibility.Extensions.GetCompany(Type)` explotaba con
  `IndexOutOfRangeException` (indexa `AssemblyCompanyAttribute` sin verificar que exista) para
  TODOS los plugins, tumbando el arranque completo — el mismo síntoma de clase de bug que
  `verify_attr_blobs.py` busca, pero en un atributo distinto que ese script no cubre. Agregado
  `Properties/AssemblyInfo.cs` con los mismos atributos que `Core`; recompilado, reinstalado.

### Corregido (encontrado usando el plugin real contra un ambiente real, no revisión visual)
- **La búsqueda/filtro de tablas nunca filtraba nada.** `ApplyTableFilter` en `PluginControl.cs`
  calculaba `matchesSearch`/`matchesQuickFilter` por cada fila pero terminaba en
  `_ = matchesSearch && matchesQuickFilter;` — un comentario admitía la limitación de
  `CheckedListBox` (sin flag de visibilidad por ítem) y dejaba el filtro sin aplicar. Reescrito
  para reconstruir `_tablesList.Items` en cada cambio de filtro, con un nuevo campo
  `_checkedTableNames` (`HashSet<string>`) como fuente de verdad de la selección — así una tabla
  marcada mientras un filtro distinto estaba activo no se pierde al reconstruir la lista.
  `SyncProfileEntitiesFromTableSelection`, `LoadProfileIntoUi`, `OnNewProfile` y
  `OnClearAll`/`OnSelectAllVisible`/`OnClearVisible` se actualizaron para usar ese mismo campo en
  vez de `_tablesList.CheckedItems` (que solo refleja lo actualmente visible).

### Agregado (feedback de uso real contra un ambiente real)
- Íconos reales de `Plugin.cs` (32x32 / 80x80, mismos tamaños que usa Metadata Dataverse
  Document, verificado decodificando su propio DLL) — reemplazan el placeholder transparente.
  PNGs fuente en `Resources/` para poder regenerarlos.
- La UI ahora navega automáticamente a la pestaña Migration después de crear un perfil desde
  Tables o de guardar un perfil — antes no había ninguna señal de que "Execute" vive en otra
  pestaña, y un usuario nuevo no tenía forma de descubrirlo.

### Corregido (encontrado usando el instalador real, no revisión visual)
- **El ícono no se actualizaba tras reinstalar.** XrmToolBox cachea los metadatos de cada
  plugin (incluido el ícono en Base64) en `Plugins\manifest.json`, indexados por
  `AssemblyQualifiedName` (que incluye la versión). Como `AssemblyVersion` se mantuvo en
  `0.3.0.0` en los dos builds anteriores (el que agregó el ícono real y el de la navegación a
  Migration), XrmToolBox nunca detectó un cambio y siguió sirviendo el ícono placeholder
  cacheado. Con `AssemblyVersion` en `0.3.1.0`, la caché se refrescó (verificado leyendo
  `manifest.json` después de reinstalar: el Base64 del ícono ya coincide con el real). A tener
  en cuenta para cualquier cambio futuro que dependa de que XrmToolBox "vea" un DLL distinto:
  subir la versión del ensamblado, no solo recompilar.
- **El instalador fallaba con `Join-Path : ... cadena vacía` al correr `Instalar-Plugin.bat`.**
  `$PSScriptRoot` llegó vacío en el contexto de lanzamiento real del usuario (no reproducido de
  forma aislada, pero el síntoma es inequívoco), y el valor por defecto del parámetro
  `-SourceFolder` en `Install-DataverseMasterDataMigrator.ps1` dependía de él directamente
  dentro de la expresión de `param()`, sin resguardo. Se movió la resolución al cuerpo del
  script con una cadena de respaldo (`$PSScriptRoot` → `$MyInvocation.MyCommand.Path` →
  directorio actual), y además `Instalar-Plugin.bat` ahora pasa `-SourceFolder "%~dp0bin"`
  explícitamente, sin depender en absoluto de que el `.ps1` lo infiera. Probado end-to-end
  corriendo el `.bat` real vía `cmd.exe` (no solo invocando el `.ps1` directamente).

### Corregido (encontrado con el instalador ya corriendo en tu XrmToolBox real)
- **El ícono grande quedó corrupto en el commit anterior.** Al incrustar el Base64 del ícono de
  80x80 en `Plugin.cs`, el archivo terminó con solo 6650 de los 13116 caracteres esperados — un
  encabezado PNG válido seguido de datos comprimidos truncados, que decodifican como ruido
  visual (exactamente lo que se veía en pantalla). Verificado comparando byte a byte el PNG
  fuente contra lo decodificado del archivo (antes: distintos; ahora: idénticos, MD5 igual).
  Reescrito el archivo con un script que copia el Base64 directo desde el PNG fuente, sin pasar
  por una edición de texto manual de una cadena de 13000+ caracteres. `AssemblyVersion` subida a
  `0.3.2.0` para invalidar el caché de nuevo; confirmado leyendo `manifest.json`: el ícono
  cacheado ahora coincide con el archivo fuente.

### Agregado (feedback de uso real — pestaña Connections poco amigable)
- Rediseñada la pestaña Connections: antes era una fila angosta por conexión con un botón
  "Change Source" que solo mostraba un mensaje pidiendo usar la barra de XrmToolBox. Ahora son
  dos tarjetas lado a lado (Source / Target) con un indicador de estado (punto de color) y los
  datos de la conexión en varias líneas. El botón de Source ahora dispara directamente el
  selector de conexión real de XrmToolBox (`RaiseRequestConnectionEvent`, API pública de
  `PluginControlBase` encontrada por reflexión contra el DLL real) en vez de solo indicar dónde
  buscarlo.

### Corregido (encontrado probando Preflight contra un ambiente real)
- **`ExternalLookupSampler` advertía sobre lookups que `MigrationExecutor` nunca iba a intentar
  escribir.** Campos gestionados por la plataforma (`createdby`, `modifiedby`, `organizationid`,
  ...) apuntan a tablas fuera del perfil (`systemuser`, `organization`), así que el muestreo los
  marcaba como "lookup externo sin resolver" — técnicamente cierto, pero irrelevante, porque
  `IsValidForCreate`/`IsValidForUpdate` son `false` para esos atributos y el motor de ejecución
  ya los excluye por completo (`GetWritableAttributes`). Se agregó el mismo filtro al sampler,
  para que Preflight solo advierta sobre lookups que realmente se van a intentar escribir.

### Agregado (feedback de uso real)
- **Preview Data**: nuevo botón en la pestaña Migration, separado de Preflight. Cuenta, por
  tabla, cuántos registros de Source se crearían vs. actualizarían en Target dado el perfil
  actual (V1 siempre es Upsert con `preserveSourceGuid`: si el GUID ya existe en Target es
  Update, si no, Create). Deliberadamente separado de Preflight porque, a diferencia de este,
  necesita leer todos los IDs de Source y Target — vale la pena que sea un clic explícito, no
  parte del paso rápido de Preflight. Nueva clase `Core/Validation/MigrationPreviewBuilder.cs`
  (3 tests).
- **About** real (antes un `MessageBox` genérico): ahora un diálogo propio
  (`UI/AboutForm.cs`) con el mismo estilo visual que el de Metadata Dataverse Document —
  encabezado oscuro con título/versión, descripción, desarrollador, copyright, y enlaces a
  sitio web/contacto. El repositorio se muestra como "coming soon" (texto, no enlace) hasta que
  el proyecto se suba a GitHub.

### Agregado (feedback de uso real — continuación)
- **Botón "About"** en una barra superior persistente (visible en las 4 pestañas), en vez de
  depender del menú "About Plugin" que expone XrmToolBox por su cuenta (clic derecho sobre la
  pestaña, o la flecha ▼) — verificado que ese menú de XrmToolBox sí existe y funciona (la
  interfaz `IAboutPlugin` coincide exactamente con la real), pero el usuario prefirió un botón
  explícito, igual que en Metadata Dataverse Document.
- **Data Preview rediseñado**: ya no son solo conteos en el log. Ahora "Preview Data" abre un
  diálogo (`UI/PreviewResultsForm.cs`) con las tablas del perfil a la izquierda y, a la derecha,
  cada registro de Source con su nombre (usa el atributo de nombre primario de la tabla,
  `TableSummary.PrimaryNameAttribute`, nuevo) y su estado: 🟩 New (no existe en Target, se va a
  crear) o 🟨 Update (el GUID ya existe en Target). Limitado a 2000 registros de detalle por
  tabla (los conteos agregados siempre son exactos, incluso si el detalle se trunca) para no
  cargar tablas enormes completas en memoria. `Core/Validation/MigrationPreviewBuilder.cs`
  extendido con 6 tests.

### Corregido (encontrado ejecutando una migración real contra un ambiente real)
- **`Created` se contaba dos veces cuando Pass 3 restauraba statecode/statuscode** (y también
  podía pasar con Pass 2, lookups diferidos). `ApplyResults` incrementa `Created` para todo
  éxito cuyo `Operation` no sea explícitamente `Update` — pero el adaptador
  (`WriteExecuteMultipleUpsert`) siempre informa `Operation = Create`, porque un `UpsertRequest`
  no distingue create-vs-update en su respuesta. Reutilizar `ApplyResults` para Pass 2/3 hacía
  que cada registro, ya contado en Pass 1, se sumara otra vez al completarse su pase siguiente
  — un usuario lo detectó con 32 registros de Source reportados como "64 created". Se agregó
  `ApplyFollowUpResults` (Pass 2/3 solo suman a `Failed`, nunca a `Created`/`Updated`, ya que
  esos registros ya fueron contados en Pass 1) y se aplicó en los 6 lugares donde correspondía
  (Pass 2, Pass 3, y los 3 casos análogos dentro de `RetryFailedAsync`). Nuevo test de
  regresión (`StateStatusRestoreInPass3_DoesNotDoubleCountCreated`) que falla con el bug viejo.

### Agregado (feedback de uso real — tenant con ~2500 tablas)
- **"Find Related Tables"** en la pestaña Tables: para cada tabla ya seleccionada, trae su
  metadata completa y busca lookups hacia tablas que todavía no están en la selección. Solo
  sugiere tablas propias (`IsCustomEntity`) — un lookup hacia una tabla estándar/de sistema
  (`systemuser`, `transactioncurrency`, `organization`, ...) se sigue dejando como lookup
  externo resuelto en runtime (sección 12), no como sugerencia para agregar al perfil. Muestra
  un diálogo (`UI/RelatedTablesForm.cs`) con las tablas encontradas, precheckeadas, para
  confirmar cuáles agregar. Al ejecutarlo de nuevo después de agregar las sugeridas, encuentra
  la siguiente capa de dependencias — repetir hasta que reporte "no additional related tables
  found" cubre dependencias transitivas sin necesitar un grafo completo de una sola vez.
- **Contador de selección siempre visible** ("N table(s) selected") en la pestaña Tables, junto
  a los filtros rápidos — antes había que cambiar al filtro "Selected" para verlo, poco
  descubrible con miles de tablas en la lista completa.

### Corregido (encontrado ejecutando Preflight contra tablas reales)
- **`ownerid` bloqueaba Preflight en cualquier tabla, siempre.** A diferencia de
  `createdby`/`modifiedby`/`organizationid` (que no son escribibles y ya se excluían),
  `ownerid` SÍ es válido para create/update — por eso no caía en ese filtro. Como es un lookup
  polimórfico hacia `systemuser`/`team`, y esos GUIDs son específicos de cada ambiente, siempre
  aparecía como `REQUIRED_LOOKUP_UNRESOLVED` (error, bloqueante) — y de haberse ignorado el
  bloqueo, el Create real habría fallado igual por la referencia inválida. Se agregó
  `AttributeSummary.IsOwnerLookup` (true cuando todos los `LookupTargets` son `systemuser`/
  `team`, la convención real de Dataverse para el tipo "Owner") y se excluyó tanto de
  `GetWritableAttributes` (nunca se escribe) como de `ExternalLookupSampler` (nunca se
  muestrea) — mismo tratamiento que los campos de sistema, pero basado en el tipo de dato en
  vez de en `IsValidForCreate`/`IsValidForUpdate`. 2 tests nuevos de regresión (más 3 tests
  existentes que reusaban "ownerid" como nombre genérico de prueba, ahora renombrados para no
  chocar con la exclusión real).

### Agregado (feedback de uso real)
- Pestaña Profiles: al seleccionar un perfil del combo, ahora se lista abajo qué tablas contiene
  (antes solo se veía nombre/descripción, sin forma de saber qué tablas tenía sin ir a Tables y
  comparar los checkboxes a ojo). Se actualiza también al crear un perfil nuevo, al guardar, y
  al crear desde selección (que además ahora deja seleccionado el perfil recién creado en el
  combo, en vez de dejar el que estaba antes).

### Corregido (encontrado en una migración real de 10 tablas / ~2900 registros, 56 fallos)
- **El log de ejecución no mostraba nunca el motivo de un fallo**, solo el conteo (`N failed`).
  Diagnosticar 56 fallos reales requería abrir a mano el JSON del `ExecutionManifest`. Ahora,
  tanto Execute como Retry Failed listan los mensajes de error reales agrupados por causa (ej.
  `27x: 'wit_ofertaacademica' entity doesn't contain attribute with Name = 'wit_habilitadoec'...`)
  en vez de solo el conteo total.
- **Nuevo chequeo de Preflight: `ATTRIBUTE_NOT_IN_TARGET`.** De los 56 fallos reales, 37 fueron
  exactamente "entity doesn't contain attribute ... NameMapping = 'Logical'" — atributos que
  existen en Source pero no en Target (customizaciones de Target desactualizadas respecto a
  Source), detectables de antemano por metadata pura, sin necesitar muestrear datos. Se agregó
  `Core/Migration/AttributeWritabilityRules.cs` como única fuente de verdad de "qué atributo se
  va a escribir realmente" — usado tanto por `MigrationExecutor` como por el nuevo chequeo de
  Preflight, para que nunca queden desincronizados entre sí. Advertencia (Warning), no bloquea
  Execute, ya que solo afecta a los registros que tengan ese campo específico con valor.
- Los otros 19 fallos (`Entity 'transactioncurrency' ... Does Not Exist`) correspondían
  exactamente a la advertencia de lookup opcional ya mostrada en Preflight
  (`transactioncurrencyid`) — comportamiento esperado, no un bug: con
  `optionalLookupPolicy = WarnAndContinue` (el default), el intento de escritura sigue
  incluyendo el valor y falla si el GUID no existe en Target. Pendiente de evaluar en una
  próxima fase: que `SkipSilently` realmente omita el atributo del payload de escritura (hoy
  solo cambia la severidad del aviso en Preflight, no qué se escribe).

### Agregado (feedback de uso real — selección de registros)
- **Data Preview ahora permite curar la selección antes de ejecutar**: cada registro tiene su
  checkbox, con botones de selección masiva ("Select All", "Select None", "New Only", "Updates
  Only"). Al confirmar ("Apply Selection"), lo que quede desmarcado se excluye del próximo
  Execute — nunca se cuenta ni se intenta escribir. La selección se resetea (todo incluido) cada
  vez que se vuelve a correr Preview Data.
  - `MigrationExecutionRequest.ExcludedRecordIds`: nuevo campo (por tabla, set de GUIDs a
    omitir), aplicado en Pass 1 antes de contar/escribir cualquier registro. 1 test nuevo de
    regresión en Core.
  - Limitación conocida: el detalle de Preview está limitado a 2000 registros por tabla (ver
    "Truncated" ya documentado); los registros más allá de ese límite no se pueden excluir
    individualmente desde este diálogo y siempre se incluyen tal cual.
- Pendiente evaluar como próximo paso (pedido explícitamente, no implementado aún): un
  comparador lado a lado estilo cliente FTP mostrando los valores reales de campo de Source vs.
  Target para los registros que ya existen en Target (no solo Nuevo/Actualizar) — requiere traer
  datos completos de ambos lados y una grilla de diff, alcance bastante mayor que la selección
  por registro.

### Agregado (feedback de uso real)
- **Mensaje "Tarea finalizada"**: Execute y Retry Failed ahora muestran un popup al terminar con
  el resumen (estado, total creado/actualizado/fallido) — antes la única señal de que había
  terminado era el texto apareciendo en el log, fácil de perder en una corrida de varios
  minutos.

### Agregado (feedback de uso real — comparador estilo cliente FTP)
- **Data Preview rediseñado como panel dual** (Source | Target), inspirado explícitamente en
  FileZilla: ambas grillas comparten la misma lista de registros subyacente, así que muestran
  siempre las mismas filas en el mismo orden, con scroll y selección sincronizados entre las dos
  — se ven ambos ambientes al mismo tiempo, alineados fila por fila.
  - `MigrationPreviewBuilder` ahora también trae el valor ACTUAL en Target (antes solo
    verificaba existencia) — nuevo campo `RecordPreviewRow.TargetDisplayName`. 2 tests nuevos.
  - Color por fila: 🟩 verde = New (no existe en Target); ⬜ gris claro = Update sin cambio de
    nombre; 🟥 rojizo = Update con el nombre realmente distinto entre Source y Target — la forma
    más simple de "marcar las diferencias" sin necesitar traer y comparar cada atributo de cada
    registro.
  - La selección (checkbox, botones de selección masiva) sigue funcionando igual que antes,
    ahora en el panel izquierdo (Source) únicamente.

### Corregido (encontrado probando el comparador de dos paneles recién agregado)
- **La grilla se veía diminuta, con un área gris enorme debajo.** Causa: el layout externo del
  diálogo usaba un `TableLayoutPanel` con filas `AutoSize` mezcladas con una fila `Percent(100)`
  — combinación que en este caso no se expandía como se esperaba. Reescrito usando `Dock` simple
  (barras superiores `Dock.Top` agregadas en orden, botones `Dock.Bottom`, y el contenido
  principal `Dock.Fill` agregado al final — el orden de agregado es lo que determina qué
  espacio reclama cada uno). Además, los dos paneles ahora son un `SplitContainer` con divisor
  arrastrable, más parecido a un cliente FTP real.
- **Los dos paneles mostraban columnas distintas** (Source no tenía Status, Target no tenía Id).
  Ahora ambos muestran exactamente las mismas: Id, Name, Status (el checkbox de selección sigue
  siendo exclusivo del panel Source, ya que no aplica al panel Target).
- El combo de tablas ahora también muestra el nombre para mostrar de la tabla, no solo el
  nombre lógico (mismo patrón usado en la pestaña Tables) — nuevo campo
  `TableDataPreview.DisplayName`.

### Corregido (crash real al abrir Data Preview — diagnóstico anterior incorrecto)
- **`InvalidOperationException: SplitterDistance debe estar entre Panel1MinSize y Ancho -
  Panel2MinSize`** al abrir el diálogo. El primer intento de arreglo (mover `SplitterDistance`
  a `Form.Load`) no resolvió nada porque diagnostiqué mal la causa — el stack trace real decía
  `set_Panel2MinSize → ApplyPanel2MinSize → set_SplitterDistance`, es decir, la excepción salía
  de fijar `Panel1MinSize`/`Panel2MinSize` en el inicializador del objeto (en el constructor),
  no de la línea de `SplitterDistance` que había movido. Asignar cualquiera de esos dos MinSize
  valida/ajusta `SplitterDistance` de inmediato contra el `Width` ACTUAL del control — y en el
  constructor, antes de que el formulario acople sus hijos, ese ancho es un valor transitorio
  minúsculo, menor que `Panel1MinSize + Panel2MinSize`. Solución real: `Panel1MinSize` /
  `Panel2MinSize` también se movieron a `Form.Load`, junto con `SplitterDistance` y en ese
  orden (distancia primero, límites después).

### Corregido (barras superiores del comparador dual amontonadas y superpuestas con la grilla)
- **La franja superior (combo de tabla, leyenda de colores, botones de selección masiva, aviso de
  truncado) se veía comprimida y superpuesta con el contenido de la grilla.** Causa real: para
  controles hermanos que comparten el mismo `Dock`, el ÚLTIMO agregado a `Controls` es el que
  termina más cerca del borde verdadero del contenedor — al revés de lo que asumía el comentario
  de la clase y el orden de agregado original (que agregaba `tableBar` primero, pensando que así
  quedaría arriba de todo). Se reescribió el constructor para construir todos los controles
  primero, sin `Controls.Add` intercalados, y agregarlos al final en el orden correcto:
  `buttons` (Bottom) → `_truncatedNotice` → `bulkBar` → `legend` → `tableBar` (el último del
  grupo Top, por eso el que queda arriba de todo) → `split` (Fill, siempre al final). También se
  corrigió el comentario de documentación de la clase, que describía la regla al revés.

### Pendiente (próxima fase, cuando aplique)
- Íconos reales (hoy PNG transparente 1x1 — decisión explícita del usuario de seguir así por
  ahora).
- Evaluar actualizar `Microsoft.Xrm.Sdk.dll` a una versión que exponga
  `IsCreateMultipleSupported`/`IsUpdateMultipleSupported` (impacto en otros plugins a evaluar
  antes de tocarla).
- Pass 3b (asociaciones N:N) es best-effort: identifica las 2 columnas FK de la tabla de
  intersección vía metadata: si no las reconoce, lo salta con un warning en vez de fallar la
  ejecución completa — no verificado contra una relación N:N real todavía.

## [0.2.0] — Fase 2: capa XrmToolBox

### Agregado
- `DataverseMasterDataMigrator.XrmToolBox`: `Plugin.cs` (exportación MEF de doble conexión,
  `AssemblyResolveEventHandler` portado de Metadata Dataverse Document), `Settings.cs`,
  adaptadores reales `DataverseMetadataProviderAdapter` / `DataverseRecordServiceAdapter` sobre
  `IOrganizationService`, y `PluginControl` (UI de Connections/Profiles/Tables/Migration,
  construida en código).
- Compilación verificada con `mcs` (Mono) contra los DLL reales de XrmToolBox/Dataverse SDK
  (copiados desde tu propio `MetadataDataverseDocument-Source/lib/`), no solo revisión visual.

### Corregido (encontrado durante la verificación real, no revisión manual)
- `WriteStrategy.BulkUpsert` eliminado: Dataverse no tiene un mensaje bulk de upsert. Reemplazado
  por `BulkCreateThenUpdate` (split + `CreateMultiple`/`UpdateMultiple`) y `ExecuteMultipleUpsert`
  (batch de `UpsertRequest` individuales vía `ExecuteMultipleRequest`).
- `DependencyGraph.DependenciesOf`: error de tipos real en el operador ternario
  (`HashSet<string>` vs `string[]` sin conversión implícita).
- `CycleDetector` reescrito de recursivo a iterativo (Tarjan con pila explícita), evitando riesgo
  de stack overflow en grafos grandes.
- `WhoAmIRequest` / `RetrieveTotalRecordCountRequest` corregidos a su namespace real
  (`Microsoft.Crm.Sdk.Messages`, no `Microsoft.Xrm.Sdk.Messages`).
- `EntityMetadata.IsCreateMultipleSupported` / `IsUpdateMultipleSupported` no existen en la
  versión del SDK referenciada; código ajustado a un default seguro (`false`) con fallback
  automático a `ExecuteMultipleUpsert`.
- Nombre real de la acción de conexión adicional corregido a `"AdditionalOrganization"` (no
  `"Target"`), y `AdditionalConnectionsCount` (que no existe en esta versión de
  `XrmToolBox.Extensibility`) eliminado a favor de `AddAdditionalOrganization()` +
  `ConnectionDetailsUpdated`.

### Pendiente (próxima fase)
- Motor de ejecución multipass conectado a Execute; muestreo de lookups externos para Preflight;
  `Retry Failed` real; instalador y empaquetado.

## [0.1.0] — Fase 1: Core

### Agregado
- `DataverseMasterDataMigrator.Core`: modelos, perfiles (serialización + repositorio con
  backups), grafo de dependencias, detección de ciclos (Tarjan), orden topológico determinista
  (Kahn con desempate por `preferredOrder`/nombre), planner, preflight validator, retry policy
  con backoff exponencial, selector de estrategia de escritura, y manifest de ejecución con
  checkpoints incrementales.
- Interfaces de extensibilidad (`IRecordTransformer`, `IReferenceResolver`,
  `IMigrationProfileProvider`, `IRecordSelector`) preparadas para el futuro plugin UMayor.
- Suite de tests unitarios (xUnit) cubriendo toda la lógica que no requiere una conexión
  Dataverse real.
- `docs/ARCHITECTURE.md` y `docs/MIGRATION_PROFILES.md`.

### Pendiente (próximas fases)
- `DataverseMasterDataMigrator.XrmToolBox`: UI WinForms, `MultipleConnectionsPluginControlBase`,
  adaptadores reales de `IDataverseMetadataProvider`/`IDataverseRecordService` sobre
  `IOrganizationService`.
- Instalador (`Instalar-Plugin.bat` + `Install-DataverseMasterDataMigrator.ps1`) y empaquetado
  de distribución.
- Validación del build Release con `verify_attr_blobs.py` (adaptado del proyecto de
  referencia) antes de generar el instalador.
