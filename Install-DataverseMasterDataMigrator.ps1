<#
.SYNOPSIS
    Installs the Dataverse Master Data Migrator plugin into your local XrmToolBox.

.DESCRIPTION
    Follows exactly the same layout as "Metadata Dataverse Document" (the reference plugin this
    project is patterned after): the main plugin assembly goes to the ROOT of the Plugins
    folder (where XrmToolBox's plugin loader looks for it), and this plugin's OWN extra
    dependencies (DataverseMasterDataMigrator.Core.dll, plus the ClosedXML dependency chain used
    by the Compare Structure -> Export to Excel feature) go into a dedicated subfolder - never
    the Plugins root - so they can never collide with another plugin's same-named dependency.
    Plugin.cs's AssemblyResolveEventHandler is what actually loads them from there at runtime -
    including a forced-redirect path for a few of them (System.Buffers/System.Memory/
    System.Numerics.Vectors/System.Runtime.CompilerServices.Unsafe) whose exact requested
    version doesn't match what NuGet actually restored; see Plugin.cs's ForcedOwnDependencies
    for why that's necessary and CHANGELOG.md for how this was found and verified.

    Deliberately NOT copied: Microsoft.Xrm.Sdk.dll, XrmToolBox.Extensibility.dll,
    McTools.Xrm.Connection*.dll, Newtonsoft.Json.dll, etc. Those are the HOST's own
    dependencies (already loaded by XrmToolBox.exe itself before any plugin assembly loads) -
    shipping a second copy of them would risk a version conflict instead of preventing one.

.PARAMETER SourceFolder
    Folder containing the compiled DLLs (DataverseMasterDataMigrator.dll and
    DataverseMasterDataMigrator.Core.dll). Defaults to ".\bin" next to this script, which is
    where the Installation .zip lays them out; pass -SourceFolder explicitly to install straight
    from a Release build output folder instead.

.PARAMETER Force
    Skip the "XrmToolBox is running" confirmation prompt.
#>
[CmdletBinding()]
param(
    [string]$SourceFolder,
    [switch]$Force
)

$ErrorActionPreference = "Stop"

# $PSScriptRoot can come back empty in some launch contexts (observed when running the .bat
# wrapper from certain shells/paths) - Join-Path against an empty string throws immediately, so
# this resolves a default with a fallback chain instead of relying on $PSScriptRoot alone in a
# param() default expression.
if ([string]::IsNullOrWhiteSpace($SourceFolder)) {
    $scriptDir = $PSScriptRoot
    if ([string]::IsNullOrWhiteSpace($scriptDir) -and $MyInvocation.MyCommand.Path) {
        $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
    }
    if ([string]::IsNullOrWhiteSpace($scriptDir)) {
        $scriptDir = (Get-Location).Path
    }
    $SourceFolder = Join-Path $scriptDir "bin"
}

$MainAssembly = "DataverseMasterDataMigrator.dll"
$CoreAssembly = "DataverseMasterDataMigrator.Core.dll"

# ClosedXML (Compare Structure -> Export to Excel) and its full transitive dependency chain -
# confirmed complete and correct by actually loading the plugin from a folder containing ONLY
# these files (see CHANGELOG.md): a first attempt at this list was missing System.Buffers/
# System.Memory/System.Numerics.Vectors/System.Runtime.CompilerServices.Unsafe and crashed with
# FileNotFoundException at runtime even though the build succeeded - do not trim this list
# without re-running that same empirical check.
$OwnDependencies = @(
    "ClosedXML.dll",
    "DocumentFormat.OpenXml.dll",
    "ExcelNumberFormat.dll",
    "Irony.dll",
    "SixLabors.Fonts.dll",
    "System.Buffers.dll",
    "System.IO.Packaging.dll",
    "System.Memory.dll",
    "System.Numerics.Vectors.dll",
    "System.Runtime.CompilerServices.Unsafe.dll",
    "XLParser.dll"
)

function Write-Step($message) {
    Write-Host "==> $message" -ForegroundColor Cyan
}

$mainAssemblyPath = Join-Path $SourceFolder $MainAssembly
$coreAssemblyPath = Join-Path $SourceFolder $CoreAssembly

if (-not (Test-Path $mainAssemblyPath)) {
    throw "Could not find '$MainAssembly' in '$SourceFolder'. Build in Release first, or pass -SourceFolder pointing at the folder with the compiled DLLs."
}
if (-not (Test-Path $coreAssemblyPath)) {
    throw "Could not find '$CoreAssembly' in '$SourceFolder'."
}
foreach ($dep in $OwnDependencies) {
    if (-not (Test-Path (Join-Path $SourceFolder $dep))) {
        throw "Could not find '$dep' in '$SourceFolder' - this is a required dependency of the Compare Structure -> Export to Excel feature (ClosedXML chain). Build in Release first."
    }
}

$pluginsRoot = Join-Path $env:APPDATA "MscrmTools\XrmToolBox\Plugins"
$ownSubfolder = Join-Path $pluginsRoot "DataverseMasterDataMigrator"

if ((Get-Process -Name "XrmToolBox" -ErrorAction SilentlyContinue) -and -not $Force) {
    Write-Warning "XrmToolBox is running. If the destination file already exists and is in use, the copy below will fail - close XrmToolBox and re-run, or pass -Force to skip this prompt (a first install, with no prior version loaded, normally has no locked file)."
    if ([Environment]::UserInteractive -and -not ([Console]::IsInputRedirected)) {
        $answer = Read-Host "Continue anyway? (y/N)"
        if ($answer -notmatch '^[yY]') {
            Write-Host "Installation cancelled." -ForegroundColor Yellow
            exit 1
        }
    }
    else {
        Write-Host "Non-interactive session: continuing anyway (the copy will fail with a clear error if the file is actually locked)." -ForegroundColor Yellow
    }
}

Write-Step "Plugins folder: $pluginsRoot"
if (-not (Test-Path $pluginsRoot)) {
    New-Item -ItemType Directory -Path $pluginsRoot -Force | Out-Null
}

Write-Step "Copying $MainAssembly to the Plugins root"
Copy-Item $mainAssemblyPath (Join-Path $pluginsRoot $MainAssembly) -Force

Write-Step "Copying $CoreAssembly and its dependencies (ClosedXML chain) to its dedicated subfolder (never the root)"
if (-not (Test-Path $ownSubfolder)) {
    New-Item -ItemType Directory -Path $ownSubfolder -Force | Out-Null
}
Copy-Item $coreAssemblyPath (Join-Path $ownSubfolder $CoreAssembly) -Force
foreach ($dep in $OwnDependencies) {
    Copy-Item (Join-Path $SourceFolder $dep) (Join-Path $ownSubfolder $dep) -Force
}

Write-Host ""
Write-Host "Installation complete." -ForegroundColor Green
Write-Host "  $pluginsRoot\$MainAssembly"
Write-Host "  $ownSubfolder\$CoreAssembly (+ $($OwnDependencies.Count) dependency DLLs)"
Write-Host ""
Write-Host "Open (or reopen) XrmToolBox and look for 'Dataverse Master Data Migrator' in the tool list."
