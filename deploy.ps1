<#
  Builds both tools (Release) and copies them into an XrmToolBox Plugins folder – for testing before publishing.
  Close XrmToolBox first (otherwise the DLLs are locked).

  Your normal XrmToolBox:
    powershell -ExecutionPolicy Bypass -File deploy.ps1

  Separate sandbox (does not touch your normal XrmToolBox; starts it with /overridepath):
    powershell -ExecutionPolicy Bypass -File deploy.ps1 -Sandbox "C:\XtbSandbox" -XrmToolBoxExe "C:\Tools\XrmToolBox\XrmToolBox.exe"
#>
param(
    [string]$PluginsPath = (Join-Path $env:APPDATA 'MscrmTools\XrmToolBox\Plugins'),
    [string]$Sandbox,
    [string]$XrmToolBoxExe
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

if (Get-Process XrmToolBox -ErrorAction SilentlyContinue) {
    Write-Warning 'XrmToolBox is running – close it first, otherwise the DLLs cannot be replaced.'
    return
}

dotnet build (Join-Path $root 'D365Tools.sln') -c Release -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

if ($Sandbox) { $PluginsPath = Join-Path $Sandbox 'Plugins' }
New-Item -ItemType Directory -Force $PluginsPath | Out-Null

$dlls = @(
    'src\D365AuditManager\bin\Release\net48\D365Tools.AuditManager.dll',
    'src\D365TranslationManager\bin\Release\net48\D365Tools.TranslationManager.dll'
)
foreach ($dll in $dlls) {
    Copy-Item (Join-Path $root $dll) $PluginsPath -Force
    Write-Host "Copied: $(Split-Path $dll -Leaf) -> $PluginsPath"
}

if ($Sandbox -and $XrmToolBoxExe) {
    Write-Host "Starting XrmToolBox with /overridepath:$Sandbox (own plugins, settings and connections)"
    Start-Process $XrmToolBoxExe -ArgumentList "/overridepath:`"$Sandbox`""
}
else {
    Write-Host 'Done. Start XrmToolBox – the tools are called “D365 Audit Manager” and “D365 Translation Manager”.' -ForegroundColor Green
}
