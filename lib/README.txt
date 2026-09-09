Esta carpeta sigue el mismo patrón que "lib/" en Metadata Dataverse Document: DLLs de
referencia que el proyecto necesita para compilar, versionadas explícitamente en vez de
resueltas por NuGet en cada máquina.

Para compilar DataverseMasterDataMigrator.Core (Fase 1) necesitas colocar aquí:

  Newtonsoft.Json.dll   (versión 13.0.x recomendada)

Puedes copiarla desde tu instalación de XrmToolBox (ya la tiene en la raíz de Plugins, es una
dependencia compartida del host) o descargarla del paquete NuGet Newtonsoft.Json y extraer
lib/net45/Newtonsoft.Json.dll del paquete.

Cuando se agregue el proyecto DataverseMasterDataMigrator.XrmToolBox (fase 2), esta misma
carpeta también alojará:

  Microsoft.Xrm.Sdk.dll
  Microsoft.Crm.Sdk.Proxy.dll
  Microsoft.Xrm.Sdk.Workflow.dll
  Microsoft.Xrm.Tooling.Connector.dll
  McTools.Xrm.Connection.dll
  McTools.Xrm.Connection.WinForms.dll
  XrmToolBox.Extensibility.dll
  XrmToolBox.ToolLibrary.dll

IMPORTANTE (encontrado durante la verificación de fase 3, con compilación real): las copias que
había aquí originalmente (tomadas de MetadataDataverseDocument-Source/lib/) estaban desactualizadas
respecto al XrmToolBox real instalado en esta máquina — 3 de los 9 DLL (McTools.Xrm.Connection.dll,
McTools.Xrm.Connection.WinForms.dll, XrmToolBox.Extensibility.dll, XrmToolBox.ToolLibrary.dll)
tenían una versión distinta (comparado por AssemblyName.Version, no solo tamaño de archivo), lo
que arriesgaba un MissingMethodException/FileLoadException en runtime pese a compilar sin error.

Las 9 DLL de esta carpeta fueron re-copiadas directamente desde la instalación LIVE de
XrmToolBox de esta máquina (no desde MetadataDataverseDocument-Source/lib/, que puede estar
igual de desactualizada):

  C:\ProgramData\XrmToolBox\Update\

(esa es la carpeta que XrmToolBox.AutoUpdater.exe mantiene actualizada; C:\ProgramData\XrmToolBox\
también tiene subcarpetas Backup_* con versiones viejas — no copiar desde ahí). Si vuelves a
compilar en otra máquina o después de que XrmToolBox se autoactualice, verifica versión con:

  [System.Reflection.AssemblyName]::GetAssemblyName("ruta\al.dll").Version

contra el DLL real instalado, y vuelve a copiar si difiere.
