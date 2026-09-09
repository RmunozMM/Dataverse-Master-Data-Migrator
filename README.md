# Dataverse Master Data Migrator

Motor y (próximamente) plugin de XrmToolBox para migrar datos maestros entre ambientes
Microsoft Dataverse usando perfiles reutilizables en vez de migraciones manuales tabla por
tabla.

**Estado actual: Fase 1 — `DataverseMasterDataMigrator.Core`.** Sin UI todavía. Ver
`CHANGELOG.md` para el detalle de qué está y qué falta.

## Estructura

```
DataverseMasterDataMigrator.sln
/src
    /DataverseMasterDataMigrator.Core          <- motor, sin SDK ni WinForms
    /DataverseMasterDataMigrator.XrmToolBox    <- plugin: Plugin.cs, PluginControl, adaptadores SDK
/tests
    /DataverseMasterDataMigrator.Tests         <- xUnit, sin conexión Dataverse real
/docs
    ARCHITECTURE.md                            <- decisiones de diseño
    MIGRATION_PROFILES.md                      <- contrato del JSON de perfiles
/tools
    verify_attr_blobs.py                       <- reutilizado de Metadata Dataverse Document
/lib
    README.txt, Newtonsoft.Json.dll, Microsoft.Xrm.Sdk.dll, XrmToolBox.Extensibility.dll, ...
```

## Estado

**Fase 1 (Core):** completa y verificada — 26/26 checks de comportamiento pasaron corriendo
sobre Mono real dentro del entorno de generación.

**Fase 2 (XrmToolBox):** compila limpio contra los DLL reales de tu XrmToolBox. UI funcional
para Connections/Profiles/Tables/Preflight.

**Fase 3:** motor de ejecución multipass, Preflight de lookups externos, Retry Failed e
instalador — todo implementado y conectado. Verificado con compilación real (`dotnet build`
sobre `net48`, no revisión visual ni Mono), 56/56 tests xUnit sobre el CLR de .NET Framework
real, y con instalación real en `C:\CT\XrmToolbox` (ver `CHANGELOG.md` [0.3.0] para el detalle,
incluidas dos correcciones reales encontradas en el proceso). Pendiente: íconos reales (hoy
placeholder, por decisión explícita), y verificar Pass 3b (N:N) contra una relación real — ver
`docs/ARCHITECTURE.md` sección 11.

### Instalar

```
dist\DataverseMasterDataMigrator-0.3.0-Installation.zip
```

Descomprime y corre `Instalar-Plugin.bat` (o `Install-DataverseMasterDataMigrator.ps1`
directamente). Copia el DLL principal a la raíz de `Plugins\` de XrmToolBox y su única
dependencia propia (`DataverseMasterDataMigrator.Core.dll`) a una subcarpeta dedicada — nunca a
la raíz — siguiendo el mismo patrón que Metadata Dataverse Document.

## Cómo compilar y probar (en tu máquina)

1. `lib/` ya trae `Newtonsoft.Json.dll` y los DLL de XrmToolBox/SDK necesarios para compilar tal
   como están (copiados desde tu propio `MetadataDataverseDocument-Source/lib/`).
2. Abre `DataverseMasterDataMigrator.sln` en Visual Studio 2022, o compila desde línea de
   comandos con `dotnet build` (funciona para los tres proyectos, incluidos los dos clásicos
   `Core`/`XrmToolBox`, gracias al `PackageReference` a
   `Microsoft.NETFramework.ReferenceAssemblies` agregado en fase 3 — no hace falta tener
   instalado el Developer Pack de .NET Framework ni Visual Studio para compilar):

   ```
   dotnet build DataverseMasterDataMigrator.sln -c Release
   dotnet test tests\DataverseMasterDataMigrator.Tests\DataverseMasterDataMigrator.Tests.csproj -c Release
   ```

3. Si algo no compila o algún test falla, pega el error tal cual — la siguiente fase se ajusta
   sobre eso.

## Próximos pasos

- Reemplazar los íconos placeholder de `Plugin.cs` por íconos reales (y volver a correr
  `tools/verify_attr_blobs.py` después, por su tamaño mayor).
- Verificar Pass 3b (asociaciones N:N) contra una relación real — hoy es best-effort y se salta
  con un warning si no reconoce las columnas FK de la tabla de intersección.
- Evaluar actualizar `Microsoft.Xrm.Sdk.dll` a una versión con `IsCreateMultipleSupported` /
  `IsUpdateMultipleSupported` si se necesita la ventaja de rendimiento de
  `CreateMultiple`/`UpdateMultiple` (impacto en otros plugins a evaluar antes de tocarla).
