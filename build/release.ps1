#Requires -Version 5.1
<#
.SYNOPSIS
    One-command release builder for WinBitTorrent: portable folder + Setup.exe installer.

.DESCRIPTION
    Publishes a self-contained win-x64 build and stages it as:
      dist/WinBitTorrent-<version>-portable/            (unzip-and-run folder)
      dist/WinBitTorrent-<version>-installer/*.exe       (Inno Setup installer)
    Defaults come from build/release.settings.json; every setting can be overridden
    with a matching parameter for a one-off build.

.PARAMETER Version
    Version number (e.g. 1.0.0). Defaults to the value in release.settings.json.

.PARAMETER Portable
    Build only the portable folder (skips the installer).

.PARAMETER Installer
    Build only the installer (the portable payload is still staged first, since the
    installer is packed from it, but it is left in dist/ either way).

.PARAMETER Zip
    Also compress the portable folder into a .zip next to it.

.PARAMETER NoClean
    Do not delete a previous output folder for the same version before building.

.PARAMETER SkipPublish
    Reuse the existing bin/Release publish output instead of rebuilding it (faster
    when only re-packaging after a successful build).

.PARAMETER OpenOutput
    Open the dist/ folder in Explorer when done. Overrides the settings file.

.EXAMPLE
    build\release.ps1
    Builds both the portable folder and the installer using the defaults in
    release.settings.json.

.EXAMPLE
    build\release.ps1 -Version 1.1.0 -Zip
    Builds both artifacts tagged 1.1.0 and also produces a portable .zip.

.EXAMPLE
    build\release.ps1 -Installer -SkipPublish
    Re-packages just the installer from the last publish output, without rebuilding.
#>
[CmdletBinding()]
param(
    [string]$Version,
    [switch]$Portable,
    [switch]$Installer,
    [switch]$Zip,
    [switch]$NoClean,
    [switch]$SkipPublish,
    [switch]$OpenOutput
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$settingsPath = Join-Path $PSScriptRoot 'release.settings.json'

function Write-Step($message) { Write-Host "`n==> $message" -ForegroundColor Cyan }
function Write-Info($message) { Write-Host "    $message" -ForegroundColor DarkGray }
function Write-Ok($message) { Write-Host "    $message" -ForegroundColor Green }

# ---------------------------------------------------------------------------
# 1. Load settings (defaults) and apply any parameter overrides.
# ---------------------------------------------------------------------------
$settings = [ordered]@{
    configuration     = 'Release'
    platform          = 'x64'
    createPortableZip = $false
    openOutputFolder  = $true
}
if (Test-Path $settingsPath) {
    $loaded = Get-Content $settingsPath -Raw | ConvertFrom-Json
    foreach ($property in $loaded.PSObject.Properties) { $settings[$property.Name] = $property.Value }
}

# The version is the single source of truth in Directory.Build.props.
function Get-ProjectVersion {
    $propsPath = Join-Path $repoRoot 'Directory.Build.props'
    if (-not (Test-Path $propsPath)) { throw "Directory.Build.props not found; cannot determine version." }
    $match = Select-String -Path $propsPath -Pattern '<Version>\s*([0-9]+(\.[0-9]+){1,3})\s*</Version>' | Select-Object -First 1
    if (-not $match) { throw "No <Version> element found in Directory.Build.props." }
    return $match.Matches[0].Groups[1].Value
}

$effectiveVersion = if ($Version) { $Version } else { Get-ProjectVersion }
$buildPortable = -not $Installer.IsPresent -or $Portable.IsPresent
$buildInstaller = -not $Portable.IsPresent -or $Installer.IsPresent
$makeZip = $Zip.IsPresent -or [bool]$settings.createPortableZip
$openWhenDone = if ($PSBoundParameters.ContainsKey('OpenOutput')) { $OpenOutput.IsPresent } else { [bool]$settings.openOutputFolder }
$configuration = $settings.configuration
$platform = $settings.platform

Write-Host "WinBitTorrent release builder" -ForegroundColor Yellow
Write-Info "Version:        $effectiveVersion"
Write-Info "Configuration:  $configuration ($platform)"
Write-Info "Portable:       $buildPortable"
Write-Info "Installer:      $buildInstaller"
Write-Info "Zip portable:   $makeZip"

# ---------------------------------------------------------------------------
# 2. Sanity checks.
# ---------------------------------------------------------------------------
$backendExe = Join-Path $repoRoot 'Backend\qbittorrent-nox.exe'
if (-not (Test-Path $backendExe)) {
    throw "Backend\qbittorrent-nox.exe is missing. Run build\build-backend.ps1 first, or extract the CI artifact into Backend\."
}

$publishDir = Join-Path $repoRoot 'bin\Release\net8.0-windows10.0.19041.0\win-x64\publish'
$distRoot = Join-Path $repoRoot 'dist'
$portableName = "WinBitTorrent-$effectiveVersion-portable"
$portableDir = Join-Path $distRoot $portableName
$installerDir = Join-Path $distRoot "WinBitTorrent-$effectiveVersion-installer"
$zipPath = Join-Path $distRoot "$portableName.zip"

New-Item -ItemType Directory -Force -Path $distRoot | Out-Null

# ---------------------------------------------------------------------------
# 3. Publish (self-contained win-x64).
# ---------------------------------------------------------------------------
if ($SkipPublish) {
    if (-not (Test-Path (Join-Path $publishDir 'WinBitTorrent.exe'))) {
        throw "SkipPublish was requested but no existing publish output was found at $publishDir."
    }
    Write-Step "Skipping publish, reusing existing output"
}
else {
    Write-Step "Publishing self-contained $platform build"
    $versionParts = "$effectiveVersion.0"
    & dotnet publish (Join-Path $repoRoot 'WinBitTorrent.csproj') `
        -c $configuration -p:Platform=$platform -p:PublishProfile=win-$platform `
        -p:Version=$effectiveVersion -p:AssemblyVersion=$versionParts -p:FileVersion=$versionParts
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }
    Write-Ok "Publish complete"
}

# ---------------------------------------------------------------------------
# 4. Stage the portable payload (always needed as the installer's source too).
# ---------------------------------------------------------------------------
Write-Step "Staging portable payload"
if (-not $NoClean -and (Test-Path $portableDir)) {
    Remove-Item $portableDir -Recurse -Force
}
if (-not (Test-Path $portableDir)) {
    Copy-Item $publishDir $portableDir -Recurse
    $readme = @"
WinBitTorrent $effectiveVersion (win-x64, portable)
$('=' * ("WinBitTorrent $effectiveVersion (win-x64, portable)".Length))

A native WinUI 3 desktop client for qBittorrent.
https://github.com/Gorbachevvv/winBitTorrent

HOW TO RUN
----------
1. Unzip this folder anywhere (e.g. a USB drive or C:\Tools\WinBitTorrent).
2. Double-click WinBitTorrent.exe.

That's it - the qBittorrent engine (qbittorrent-nox) is bundled in the
Backend\ folder and is started and managed automatically. No separate
install or server setup is required.

REQUIREMENTS
------------
- Windows 10 version 2004 (build 19041) or newer, 64-bit.

WHERE YOUR DATA IS STORED
-------------------------
Settings, torrents and the managed engine's profile live in:
    %LOCALAPPDATA%\WinBitTorrent

To keep everything in one place (fully portable), set the environment
variable WINBITTORRENT_DATA_ROOT to a folder of your choice before
launching.

LICENSE
-------
WinBitTorrent is licensed under GPL-3.0-or-later. Third-party notices are
in Licenses\THIRD-PARTY-NOTICES.txt and Backend\Licenses\.
"@
    Set-Content -Path (Join-Path $portableDir 'README.txt') -Value $readme -Encoding UTF8
    Write-Ok "Portable payload staged at dist\$portableName"
}
else {
    Write-Info "Reusing existing dist\$portableName (NoClean)"
}

if ($makeZip) {
    Write-Step "Compressing portable .zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Compress-Archive -Path $portableDir -DestinationPath $zipPath -CompressionLevel Optimal
    Write-Ok "Zip created: dist\$portableName.zip"
}

# ---------------------------------------------------------------------------
# 5. Installer.
# ---------------------------------------------------------------------------
if ($buildInstaller) {
    Write-Step "Building installer (Inno Setup)"
    $iscc = @(
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1

    if (-not $iscc) {
        throw "Inno Setup (ISCC.exe) was not found. Install it with: winget install JRSoftware.InnoSetup"
    }

    New-Item -ItemType Directory -Force -Path $installerDir | Out-Null
    $issPath = Join-Path $repoRoot 'build\installer\WinBitTorrent.iss'
    & $iscc $issPath "/DAppVersion=$effectiveVersion" "/DPayloadDir=$portableDir" "/DOutputDir=$installerDir"
    if ($LASTEXITCODE -ne 0) { throw "ISCC.exe failed with exit code $LASTEXITCODE." }
    Write-Ok "Installer built at dist\WinBitTorrent-$effectiveVersion-installer"
}

if (-not $buildPortable -and -not $Zip) {
    # Installer-only run: the staged portable folder was only a build prerequisite.
    Remove-Item $portableDir -Recurse -Force
    Write-Info "Removed intermediate portable folder (Installer-only run)"
}

# ---------------------------------------------------------------------------
# 6. Summary.
# ---------------------------------------------------------------------------
Write-Step "Done"
$artifacts = @()
if (Test-Path $portableDir) {
    $sizeMb = [math]::Round(((Get-ChildItem $portableDir -Recurse -File | Measure-Object -Property Length -Sum).Sum) / 1MB, 1)
    $artifacts += [pscustomobject]@{ Path = $portableDir; SizeMb = $sizeMb }
}
if (Test-Path $zipPath) {
    $artifacts += [pscustomobject]@{ Path = $zipPath; SizeMb = [math]::Round((Get-Item $zipPath).Length / 1MB, 1) }
}
if ($buildInstaller) {
    Get-ChildItem $installerDir -Filter '*.exe' | ForEach-Object {
        $artifacts += [pscustomobject]@{ Path = $_.FullName; SizeMb = [math]::Round($_.Length / 1MB, 1) }
    }
}
foreach ($artifact in $artifacts) {
    Write-Host ("    {0,-65} {1,8} MB" -f $artifact.Path.Substring($repoRoot.Length + 1), $artifact.SizeMb) -ForegroundColor Green
}

if ($openWhenDone) {
    Start-Process explorer.exe $distRoot
}
