#Requires -Version 5.1
<#
.SYNOPSIS
Build the reduced Microsoft Store edition as an unsigned MSIX upload package.
.DESCRIPTION
Uses Visual Studio MSBuild's WinUI packaging targets, not a renamed portable ZIP.
Partner Center signs the submission. Local installation requires a separately
signed test package or developer registration; this script changes no certificates.
#>
[CmdletBinding()]
param(
    [string]$Version,
    [switch]$OpenOutput
)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$identity = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'store.settings.json') -Raw | ConvertFrom-Json
if (-not $Version) {
    [xml]$props = Get-Content -LiteralPath (Join-Path $repoRoot 'Directory.Build.props')
    $Version = @($props.Project.PropertyGroup.Version | Where-Object { $_ })[0]
}
if ($Version -notmatch '^([0-9]+)\.([0-9]+)\.([0-9]+)(?:\.0)?$') {
    throw 'Store version must be major.minor.patch or major.minor.patch.0 (fourth part must be zero).'
}
$parts = @([int]$Matches[1], [int]$Matches[2], [int]$Matches[3], 0)
if ($parts[0] -lt 1 -or @($parts | Where-Object { $_ -gt 65535 }).Count -gt 0) { throw 'Invalid MSIX version range.' }
$packageVersion = $parts -join '.'
if ($identity.identityName -notmatch '^[A-Za-z0-9.-]{3,50}$' -or $identity.publisher -notmatch '^CN=.+') {
    throw 'Set the exact Partner Center identity in build/store.settings.json.'
}
if ([string]::IsNullOrWhiteSpace($identity.publisherDisplayName)) { throw 'Publisher display name is required.' }

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio with MSIX packaging tools is required.' }
$msbuild = & $vswhere -latest -products '*' -find 'MSBuild/**/Bin/MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw 'Visual Studio MSBuild was not found.' }
if (-not (Test-Path -LiteralPath (Join-Path $repoRoot 'artifacts\runtime\Engine\WinBitTorrent.Native.dll'))) {
    throw 'Run build/build-engine.ps1 first. The native torrent engine is required.'
}

# All generated/deleted paths are fixed descendants of this checkout, never based
# on an unchecked version string or an external environment variable.
$work = Join-Path $PSScriptRoot '.store-work'
$output = Join-Path $repoRoot "dist\WinBitTorrent-$packageVersion-store"
function Remove-GeneratedDirectory([string]$Path) {
    $absolute = [IO.Path]::GetFullPath($Path)
    $allowed = @((Join-Path $PSScriptRoot '.store-work'), (Join-Path $repoRoot 'dist'))
    if (-not @($allowed | Where-Object {
        $absolute.Equals($_, [StringComparison]::OrdinalIgnoreCase) -or
        $absolute.StartsWith($_ + '\', [StringComparison]::OrdinalIgnoreCase)
    }).Count) { throw "Refusing to remove path outside generated build folders: $absolute" }
    if (Test-Path -LiteralPath $absolute) { Remove-Item -LiteralPath $absolute -Recurse -Force }
}
Remove-GeneratedDirectory $work
Remove-GeneratedDirectory $output
New-Item -ItemType Directory -Path $work, $output -Force | Out-Null
$engine = Join-Path $work 'EngineHost'
$manifestPath = Join-Path $work 'Package.appxmanifest'
[xml]$manifest = Get-Content -LiteralPath (Join-Path $repoRoot 'src\WinBitTorrent\Package.appxmanifest')
$manifest.Package.Identity.Name = [string]$identity.identityName
$manifest.Package.Identity.Publisher = [string]$identity.publisher
$manifest.Package.Identity.Version = $packageVersion
$manifest.Package.Properties.PublisherDisplayName = [string]$identity.publisherDisplayName
# SDK token substitution does not cover startup-task extension Executable attributes.
foreach ($attribute in $manifest.SelectNodes('//@Executable')) {
    $attribute.Value = $attribute.Value.Replace('$targetnametoken$', 'WinBitTorrent')
}
$manifest.Save($manifestPath)

Write-Host "Building Microsoft Store edition $packageVersion ($($identity.identityName))" -ForegroundColor Cyan
& dotnet publish (Join-Path $repoRoot 'src\WinBitTorrent.EngineHost\WinBitTorrent.EngineHost.csproj') `
    -c Release -r win-x64 --self-contained true -p:StoreBuild=true `
    "-p:Version=$packageVersion" "-p:AssemblyVersion=$packageVersion" "-p:FileVersion=$packageVersion" -o $engine -v:minimal
if ($LASTEXITCODE -ne 0) { throw "Store engine build failed ($LASTEXITCODE)." }

& $msbuild (Join-Path $repoRoot 'src\WinBitTorrent\WinBitTorrent.csproj') /restore /t:Rebuild /nologo /v:minimal `
    /p:Configuration=Release /p:Platform=x64 /p:StoreBuild=true /p:WindowsPackageType=MSIX `
    /p:SelfContained=true /p:GenerateAppxPackageOnBuild=true /p:UapAppxPackageBuildMode=StoreUpload `
    /p:AppxPackageSigningEnabled=false /p:AppxBundle=Never `
    "/p:Version=$packageVersion" "/p:AssemblyVersion=$packageVersion" "/p:FileVersion=$packageVersion" `
    "/p:StoreManifestPath=$manifestPath" "/p:StoreEnginePayload=$engine" "/p:AppxPackageDir=$output\"
if ($LASTEXITCODE -ne 0) { throw "Store app packaging failed ($LASTEXITCODE)." }

$uploads = @(Get-ChildItem -LiteralPath $output -Recurse -File | Where-Object { $_.Extension -in '.msixupload', '.appxupload' })
if ($uploads.Count -ne 1) { throw "Expected one Store upload artifact, found $($uploads.Count)." }
& (Join-Path $PSScriptRoot 'verify-store.ps1') -PackagePath $uploads[0].FullName -ReportPath (Join-Path $output 'store-validation.json')
Write-Host "`nStore upload ready: $($uploads[0].FullName)" -ForegroundColor Green
Write-Host 'Unsigned by design: upload this file to Partner Center. Microsoft signs Store deliveries.'
Write-Host 'Review docs/microsoft-store.md for privacy policy, certification notes and package testing.'
if ($OpenOutput) { Start-Process explorer.exe -ArgumentList ('"' + $output + '"') -WindowStyle Hidden }
