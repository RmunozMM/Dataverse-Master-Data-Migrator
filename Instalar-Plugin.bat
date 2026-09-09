@echo off
REM Thin wrapper so double-clicking installs without a manual PowerShell ExecutionPolicy change.
REM Same pattern as Metadata Dataverse Document's own installer.
REM
REM -SourceFolder is passed explicitly (using this batch file's own %~dp0, which cmd.exe resolves
REM reliably) instead of relying on the .ps1 inferring it from $PSScriptRoot - that came back
REM empty in at least one real launch context, and Join-Path against an empty string fails hard.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-DataverseMasterDataMigrator.ps1" -SourceFolder "%~dp0bin" %*
pause
