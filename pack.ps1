<#
  Builds the NuGet packages for the XrmToolBox Tool Library into .\artifacts
  and checks them against the "Nuget package checklist" of the Tool Library validation.

    powershell -ExecutionPolicy Bypass -File pack.ps1             # build + check
    powershell -ExecutionPolicy Bypass -File pack.ps1 -CheckUrls  # also checks that icon/project URLs are reachable

  Then upload the .nupkg files to nuget.org and register the package ids on xrmtoolbox.com.
#>
param(
    [string]$Output = (Join-Path $PSScriptRoot 'artifacts'),
    [switch]$CheckUrls
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

Remove-Item $Output -Recurse -Force -ErrorAction SilentlyContinue
dotnet pack (Join-Path $PSScriptRoot 'D365Tools.sln') -c Release -o $Output -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'dotnet pack failed.' }

$failed = 0
function Check([string]$package, [bool]$ok, [string]$text, [switch]$Warn) {
    if ($ok) { Write-Host "  PASS  $text" -ForegroundColor Green }
    elseif ($Warn) { Write-Host "  WARN  $text" -ForegroundColor Yellow }
    else { Write-Host "  FAIL  $text" -ForegroundColor Red; $script:failed++ }
}
function IsPlaceholder([string]$value) { return [string]::IsNullOrWhiteSpace($value) -or $value -match 'your-github-user|Your Name' }

foreach ($pkg in Get-ChildItem $Output -Filter *.nupkg) {
    Write-Host "`n$($pkg.Name)" -ForegroundColor Cyan
    $zip = [IO.Compression.ZipFile]::OpenRead($pkg.FullName)
    try {
        $entry = $zip.Entries | Where-Object { $_.FullName -like '*.nuspec' } | Select-Object -First 1
        $reader = New-Object IO.StreamReader($entry.Open())
        [xml]$nuspec = $reader.ReadToEnd()
        $reader.Dispose()
        $m = $nuspec.package.metadata

        $dlls = @($zip.Entries | Where-Object { $_.FullName -like '*.dll' })
        $pluginDlls = @($dlls | Where-Object { $_.FullName -match '^lib/net48/Plugins/[^/]+\.dll$' })

        $dllVersion = $null
        if ($pluginDlls.Count -gt 0) {
            $tmp = Join-Path ([IO.Path]::GetTempPath()) ('xtbcheck_' + [guid]::NewGuid().ToString('N'))
            New-Item -ItemType Directory $tmp | Out-Null
            $path = Join-Path $tmp $pluginDlls[0].Name
            [IO.Compression.ZipFileExtensions]::ExtractToFile($pluginDlls[0], $path)
            $dllVersion = [Reflection.AssemblyName]::GetAssemblyName($path).Version
            Remove-Item $tmp -Recurse -Force
        }

        Check $pkg.Name (-not (IsPlaceholder $m.iconUrl)) "Icon url: $($m.iconUrl)"
        Check $pkg.Name (-not (IsPlaceholder $m.projectUrl)) "Project url: $($m.projectUrl)"
        Check $pkg.Name ($pluginDlls.Count -eq 1) "Tool library in 'Plugins' folder: $(($pluginDlls | ForEach-Object FullName) -join ', ')"
        Check $pkg.Name ($dllVersion -ne $null -and [version]$m.version -eq $dllVersion) "Package version $($m.version) = DLL version $dllVersion"
        Check $pkg.Name ($dlls.Count -eq $pluginDlls.Count) "Only own assemblies in the package"
        Check $pkg.Name ($m.tags -match '\bXrmToolBox\b') "Tags contain 'XrmToolBox'"
        $deps = @($nuspec.package.metadata.dependencies.dependency)
        Check $pkg.Name (@($deps | Where-Object { $_.id -eq 'XrmToolBox' }).Count -eq 1 -and @($deps | Where-Object { $_.id -eq 'XrmToolBoxPackage' }).Count -eq 0) "Dependency on 'XrmToolBox' (not XrmToolBoxPackage)"
        Check $pkg.Name (-not (IsPlaceholder $m.authors)) "Authors: $($m.authors)"
        Check $pkg.Name (-not [string]::IsNullOrWhiteSpace($m.description) -and -not [string]::IsNullOrWhiteSpace($m.releaseNotes)) "Description + release notes"

        if ($CheckUrls) {
            foreach ($url in @($m.iconUrl, $m.projectUrl)) {
                try {
                    $r = Invoke-WebRequest -Uri $url -Method Head -UseBasicParsing -TimeoutSec 15
                    Check $pkg.Name ($r.StatusCode -eq 200) "Reachable: $url"
                }
                catch {
                    Check $pkg.Name $false "Reachable: $url ($($_.Exception.Message)) – push the repository (incl. docs/icons) first" -Warn
                }
            }
        }
    }
    finally {
        $zip.Dispose()
    }
}

Write-Host ''
if ($failed -gt 0) {
    Write-Host "$failed check(s) failed. Edit Directory.Build.props (RepositoryUrl, Authors, Version) and run again." -ForegroundColor Red
    exit 1
}
Write-Host "All package checks passed. Packages: $Output" -ForegroundColor Green
