#Requires -Version 7.0
<#
.SYNOPSIS
    One-command release builder for WinBitTorrent: portable folder + Setup.exe installer.

.DESCRIPTION
    Publishes a self-contained x64 or ARM64 build and stages it as:
      dist/WinBitTorrent-<version>-<architecture>-portable/
      dist/WinBitTorrent-<version>-<architecture>-installer/*.exe
    Defaults come from build/release.settings.json; every setting can be overridden
    with a matching parameter for a one-off build.

.PARAMETER Version
    Version number (e.g. 1.0.0). Defaults to the value in release.settings.json.

.PARAMETER Portable
    Build only the portable folder (skips the installer).

.PARAMETER Platform
    Target architecture: x64 (default) or arm64.

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
    [ValidateSet('x64','arm64')]
    [string]$Platform,
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
. (Join-Path $PSScriptRoot 'architecture.ps1')

function Assert-ReleaseChildPath([string] $Path, [string] $Root) {
    $full = [IO.Path]::GetFullPath($Path)
    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    if (-not $full.StartsWith($rootFull, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to mutate path outside '$Root': $full"
    }
}

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
$platform = if ($Platform) { $Platform.ToLowerInvariant() } else { ([string]$settings.platform).ToLowerInvariant() }
if ($platform -notin 'x64','arm64') { throw "Unsupported target platform '$platform'. Use x64 or arm64." }
$runtimeIdentifier = "win-$platform"
$msbuildPlatform = if ($platform -eq 'arm64') { 'ARM64' } else { 'x64' }
$assetSuffix = if ($platform -eq 'arm64') { '\arm64' } else { '' }

Write-Host "WinBitTorrent release builder" -ForegroundColor Yellow
Write-Info "Version:        $effectiveVersion"
Write-Info "Configuration:  $configuration ($platform)"
Write-Info "Portable:       $buildPortable"
Write-Info "Installer:      $buildInstaller"
Write-Info "Zip portable:   $makeZip"

# ---------------------------------------------------------------------------
# 2. Sanity checks.
# ---------------------------------------------------------------------------
$engineRoot = Join-Path $repoRoot "Engine$assetSuffix"
$pythonRoot = Join-Path $repoRoot "Backend$assetSuffix\Python"
$nativeEngine = Join-Path $engineRoot 'WinBitTorrent.Native.dll'
$pythonExe = Join-Path $pythonRoot 'python.exe'
if (-not (Test-Path $nativeEngine)) { throw "$nativeEngine is missing. Run build\build-engine.ps1 -Platform $platform first." }
if (-not (Test-Path $pythonExe)) { throw "$pythonExe is missing. Run build\build-runtime.ps1 -Platform $platform first." }
Assert-RuntimeArchitecture $engineRoot $pythonRoot $platform

$publishDir = Join-Path $repoRoot "bin\$configuration\net8.0-windows10.0.19041.0\$runtimeIdentifier\publish"
$enginePublishDir = Join-Path $repoRoot "WinBitTorrent.EngineHost\bin\$configuration\net8.0-windows10.0.19041.0\$runtimeIdentifier\publish"
$distRoot = Join-Path $repoRoot 'dist'
$portableName = "WinBitTorrent-$effectiveVersion-$platform-portable"
$portableDir = Join-Path $distRoot $portableName
$installerDir = Join-Path $distRoot "WinBitTorrent-$effectiveVersion-$platform-installer"
$zipPath = Join-Path $distRoot "$portableName.zip"

New-Item -ItemType Directory -Force -Path $distRoot | Out-Null

# A release rebuilt without -Zip must not leave an older archive with the same version next to
# the fresh folder. Otherwise the summary makes that stale archive look like a current artifact.
if (-not $NoClean -and -not $makeZip -and (Test-Path $zipPath)) {
    Remove-Item $zipPath -Force
}

# ---------------------------------------------------------------------------
# 3. Publish the selected architecture.
# ---------------------------------------------------------------------------
if ($SkipPublish) {
    if (-not (Test-Path (Join-Path $publishDir 'WinBitTorrent.exe'))) {
        throw "SkipPublish was requested but no existing publish output was found at $publishDir."
    }
    if (-not (Test-Path (Join-Path $publishDir 'EngineHost\WinBitTorrent.EngineHost.exe'))) {
        throw "SkipPublish was requested but the staged EngineHost is missing from $publishDir."
    }
    Write-Step "Skipping publish, reusing existing output"
}
else {
    Write-Step "Publishing self-contained $platform build"
    foreach ($output in $publishDir,$enginePublishDir) {
        if (Test-Path -LiteralPath $output) {
            Assert-ReleaseChildPath $output $repoRoot
            Remove-Item -LiteralPath $output -Recurse -Force
        }
    }
    $versionParts = "$effectiveVersion.0"
    & dotnet publish (Join-Path $repoRoot 'WinBitTorrent.csproj') `
        -c $configuration -r $runtimeIdentifier --self-contained true -o $publishDir `
        -p:Platform=$msbuildPlatform -p:PublishProfile=win-$platform `
        -p:Version=$effectiveVersion -p:AssemblyVersion=$versionParts -p:FileVersion=$versionParts
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }
    & dotnet publish (Join-Path $repoRoot 'WinBitTorrent.EngineHost\WinBitTorrent.EngineHost.csproj') `
        -c $configuration -r $runtimeIdentifier --self-contained true -o $enginePublishDir -p:Platform=$msbuildPlatform `
        -p:Version=$effectiveVersion -p:AssemblyVersion=$versionParts -p:FileVersion=$versionParts
    if ($LASTEXITCODE -ne 0) { throw "EngineHost publish failed with exit code $LASTEXITCODE." }
    $engineTarget = Join-Path $publishDir 'EngineHost'
    if (Test-Path -LiteralPath $engineTarget) { Assert-ReleaseChildPath $engineTarget $publishDir; Remove-Item -LiteralPath $engineTarget -Recurse -Force }
    Copy-Item $enginePublishDir $engineTarget -Recurse
    Write-Ok "Publish complete"
}
Assert-NativeArchitecture (Join-Path $publishDir 'WinBitTorrent.exe') $platform
Assert-NativeArchitecture (Join-Path $publishDir 'EngineHost\WinBitTorrent.EngineHost.exe') $platform
Assert-RuntimeArchitecture (Join-Path $publishDir 'EngineHost') (Join-Path $publishDir 'Backend\Python') $platform

$forbiddenPayload = Get-ChildItem $publishDir -Recurse -File | Where-Object {
    $_.Name -ieq 'qbittorrent-nox.exe' -or $_.Name -like 'Qt6*.dll' -or $_.Name -ieq 'qsqlite.dll'
}
if ($forbiddenPayload) {
    throw "Publish payload contains removed qBittorrent/Qt files: $($forbiddenPayload.FullName -join ', ')"
}

# ---------------------------------------------------------------------------
# 4. Stage the portable payload (always needed as the installer's source too).
# ---------------------------------------------------------------------------
Write-Step "Staging portable payload"
if (-not $NoClean -and (Test-Path $portableDir)) {
    Assert-ReleaseChildPath $portableDir $distRoot
    Remove-Item -LiteralPath $portableDir -Recurse -Force
}
if (-not (Test-Path $portableDir)) {
    Copy-Item $publishDir $portableDir -Recurse
    $readme = @"
WinBitTorrent $effectiveVersion ($runtimeIdentifier, portable)
$('=' * ("WinBitTorrent $effectiveVersion ($runtimeIdentifier, portable)".Length))

A native WinUI 3 desktop BitTorrent client powered by libtorrent.
https://github.com/Gorbachevvv/winBitTorrent

HOW TO RUN
----------
1. Unzip this folder anywhere (e.g. a USB drive or C:\Tools\WinBitTorrent).
2. Double-click WinBitTorrent.exe.

That's it - WinBitTorrent.EngineHost and libtorrent are bundled and managed
automatically. No separate torrent process, HTTP service or server setup is required.

REQUIREMENTS
------------
- Windows 10 version 2004 (build 19041) or newer, 64-bit.

WHERE YOUR DATA IS STORED
-------------------------
Settings, torrents and the engine state live in:
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
Assert-NativeArchitecture (Join-Path $portableDir 'WinBitTorrent.exe') $platform
Assert-NativeArchitecture (Join-Path $portableDir 'EngineHost\WinBitTorrent.EngineHost.exe') $platform

# A stale packaged-app resource index makes the unpackaged WinUI executable fail before its
# first window is shown. The installer removes this legacy 1.1.x artifact during upgrades; keep
# this assertion next to staging so a future installer refactor cannot silently regress it.
$installerScriptPath = Join-Path $repoRoot 'build\installer\WinBitTorrent.iss'
$installerScript = Get-Content -Path $installerScriptPath -Raw
if ($installerScript -notmatch '(?im)^Type:\s*files;\s*Name:\s*"\{app\}\\resources\.pri"') {
    throw 'Installer upgrade migration for the legacy resources.pri file is missing.'
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
    & $iscc $installerScriptPath "/DAppVersion=$effectiveVersion" "/DAppArchitecture=$platform" "/DPayloadDir=$portableDir" "/DOutputDir=$installerDir"
    if ($LASTEXITCODE -ne 0) { throw "ISCC.exe failed with exit code $LASTEXITCODE." }
    Write-Ok "Installer built at $installerDir"
}

if (-not $buildPortable -and -not $Zip) {
    # Installer-only run: the staged portable folder was only a build prerequisite.
    Assert-ReleaseChildPath $portableDir $distRoot
    Remove-Item -LiteralPath $portableDir -Recurse -Force
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
