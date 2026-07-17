[CmdletBinding()]
param(
    [string] $VcpkgRoot,
    [string] $WorkRoot,
    [string] $OutputRoot,
    [switch] $Clean
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$scriptRoot = if ([string]::IsNullOrWhiteSpace($PSScriptRoot)) { Split-Path -Parent $MyInvocation.MyCommand.Path } else { $PSScriptRoot }
if ([string]::IsNullOrWhiteSpace($WorkRoot)) { $WorkRoot = Join-Path $scriptRoot '.work' }
if ([string]::IsNullOrWhiteSpace($OutputRoot)) { $OutputRoot = Join-Path (Split-Path $scriptRoot -Parent) 'Backend' }

$versions = [ordered]@{
    QBittorrent = '5.2.3'
    WebApi = '2.15.1'
    Libtorrent = '2.0.13'
    LibtorrentCommit = '7d7fc38fac61177fa5e02148f791b2f65250b09d'
    TrySignalCommit = '105cce59972f925a33aa6b1c3109e4cd3caf583d'
    Qt = '6.11.1'
    Boost = '1.91.0'
    OpenSsl = '3.6.2'
    Python = '3.13.14'
    SearchPluginsCommit = '73613af6545fd2d4d72f59591309a8908b340c62'
    VcpkgBaseline = '4b1c85d04c9ea3730408fefcabc6123312b714d2'
}

$downloads = Join-Path $WorkRoot 'downloads'
$sources = Join-Path $WorkRoot 'sources'
$builds = Join-Path $WorkRoot 'build'
$installed = Join-Path $WorkRoot 'vcpkg_installed'
$stage = Join-Path $WorkRoot 'stage'
$projectRoot = Split-Path $scriptRoot -Parent

function Assert-ChildPath([string] $Path, [string] $Root) {
    $full = [IO.Path]::GetFullPath($Path)
    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $full.StartsWith($rootFull, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to mutate path outside '$Root': $full"
    }
}

function Get-VerifiedFile([string] $Uri, [string] $Path, [string] $Sha256) {
    if (Test-Path -LiteralPath $Path) {
        $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
        if ($actual -eq $Sha256) { return }
        Remove-Item -LiteralPath $Path -Force
    }
    Write-Host "Downloading $Uri"
    Invoke-WebRequest -Uri $Uri -OutFile $Path
    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    if ($actual -ne $Sha256) {
        Remove-Item -LiteralPath $Path -Force
        throw "SHA-256 mismatch for $Uri. Expected $Sha256, got $actual."
    }
}

function Invoke-Native([string] $Executable, [string[]] $Arguments) {
    Write-Host "> $Executable $($Arguments -join ' ')"
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Command failed with exit code ${LASTEXITCODE}: $Executable" }
}

function Initialize-LibtorrentSource([string] $Archive, [string] $Path, [string] $TrySignalArchive) {
    $required = Join-Path $Path 'deps\try_signal\try_signal.cpp'
    if (Test-Path -LiteralPath $required) { return }

    if (Test-Path -LiteralPath $Path) {
        Assert-ChildPath $Path $sources
        Remove-Item -LiteralPath $Path -Recurse -Force
    }

    Expand-Archive -LiteralPath $Archive -DestinationPath $sources

    $trySignalTemp = Join-Path $sources 'try_signal-submodule'
    if (Test-Path -LiteralPath $trySignalTemp) {
        Assert-ChildPath $trySignalTemp $sources
        Remove-Item -LiteralPath $trySignalTemp -Recurse -Force
    }
    Expand-Archive -LiteralPath $TrySignalArchive -DestinationPath $trySignalTemp
    $trySignalRoot = Get-ChildItem -LiteralPath $trySignalTemp -Directory | Select-Object -First 1
    if (-not $trySignalRoot) { throw 'try_signal archive did not contain a source directory.' }

    $trySignalTarget = Join-Path $Path 'deps\try_signal'
    if (Test-Path -LiteralPath $trySignalTarget) {
        Assert-ChildPath $trySignalTarget $Path
        Remove-Item -LiteralPath $trySignalTarget -Recurse -Force
    }
    New-Item -ItemType Directory -Path $trySignalTarget -Force | Out-Null
    Copy-Item -Path (Join-Path $trySignalRoot.FullName '*') -Destination $trySignalTarget -Recurse -Force
    Remove-Item -LiteralPath $trySignalTemp -Recurse -Force

    if (-not (Test-Path -LiteralPath $required)) {
        throw "libtorrent source is missing required submodule file '$required'."
    }
}

if ($Clean -and (Test-Path -LiteralPath $WorkRoot)) {
    Assert-ChildPath $WorkRoot $scriptRoot
    Remove-Item -LiteralPath $WorkRoot -Recurse -Force
}

New-Item -ItemType Directory -Path $downloads,$sources,$builds,$installed,$stage -Force | Out-Null

if (-not $VcpkgRoot) {
    $candidate = Get-ChildItem 'C:\Program Files\Microsoft Visual Studio' -Filter vcpkg.exe -Recurse -ErrorAction SilentlyContinue |
        Select-Object -First 1 -ExpandProperty DirectoryName
    if (-not $candidate) { throw 'vcpkg.exe was not found. Install the Visual Studio C++ vcpkg component or pass -VcpkgRoot.' }
    $VcpkgRoot = $candidate
}
$vcpkg = Join-Path $VcpkgRoot 'vcpkg.exe'
if (-not (Test-Path -LiteralPath $vcpkg)) { throw "vcpkg.exe was not found under '$VcpkgRoot'." }

$cmake = Get-Command cmake.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -First 1
if (-not $cmake) {
    $cmake = Get-ChildItem 'C:\Program Files\Microsoft Visual Studio' -Filter cmake.exe -Recurse -ErrorAction SilentlyContinue |
        Where-Object FullName -Match 'CommonExtensions\\Microsoft\\CMake' | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $cmake) { throw 'CMake was not found. Install Desktop development with C++.' }
$generators = (& $cmake --help) -join "`n"
$generator = if ($generators -match 'Visual Studio 18 2026') { 'Visual Studio 18 2026' } elseif ($generators -match 'Visual Studio 17 2022') { 'Visual Studio 17 2022' } else { throw 'Visual Studio 2022 or newer CMake generator is required.' }
$qbitArchive = Join-Path $downloads 'qbittorrent-release-5.2.3.zip'
$libtorrentArchive = Join-Path $downloads 'libtorrent-v2.0.13.zip'
$trySignalArchive = Join-Path $downloads 'try_signal-105cce59972f925a33aa6b1c3109e4cd3caf583d.zip'
$pythonArchive = Join-Path $downloads 'python-3.13.14-embed-amd64.zip'
$searchPluginsArchive = Join-Path $downloads 'qbittorrent-search-plugins-73613af6545fd2d4d72f59591309a8908b340c62.zip'
Get-VerifiedFile 'https://github.com/qbittorrent/qBittorrent/archive/refs/tags/release-5.2.3.zip' $qbitArchive '0EADBCA2C98610B7F1F95B2DE1A9E76348668E865FDC025E3122E1CEEDA0D7C5'
Get-VerifiedFile 'https://github.com/arvidn/libtorrent/archive/refs/tags/v2.0.13.zip' $libtorrentArchive '9DB3BF42A14F8D3FBFA41FABAC9DD0A698777759DF03FE85FE04A9E389DA94B2'
Get-VerifiedFile 'https://github.com/arvidn/try_signal/archive/105cce59972f925a33aa6b1c3109e4cd3caf583d.zip' $trySignalArchive 'EB29241D96046B60E54AA4CC55BB6051C51F4EE07002C5EC72ECB877DECD78F5'
Get-VerifiedFile 'https://www.python.org/ftp/python/3.13.14/python-3.13.14-embed-amd64.zip' $pythonArchive '90B4E5B9898B72D744650524BFF92377C367F44BD5FBD09E3148656C080AD907'
Get-VerifiedFile 'https://github.com/qbittorrent/search-plugins/archive/73613af6545fd2d4d72f59591309a8908b340c62.zip' $searchPluginsArchive 'E71DF8E6046F74C10166A2473173A578BC209BC837117328F96A60BBBFE10160'

$qbitSource = Join-Path $sources 'qBittorrent-release-5.2.3'
$libtorrentSource = Join-Path $sources 'libtorrent-2.0.13'
$searchPluginsSource = Join-Path $sources 'search-plugins-73613af6545fd2d4d72f59591309a8908b340c62'
if (-not (Test-Path -LiteralPath $qbitSource)) { Expand-Archive -LiteralPath $qbitArchive -DestinationPath $sources }
Initialize-LibtorrentSource $libtorrentArchive $libtorrentSource $trySignalArchive
if (-not (Test-Path -LiteralPath $searchPluginsSource)) { Expand-Archive -LiteralPath $searchPluginsArchive -DestinationPath $sources }

$env:VCPKG_FEATURE_FLAGS = 'manifests,versions'
Invoke-Native $vcpkg @(
    'install', '--triplet', 'x64-windows',
    '--x-manifest-root', $scriptRoot,
    '--x-install-root', $installed
)

$toolchain = Join-Path $VcpkgRoot 'scripts\buildsystems\vcpkg.cmake'
$tripletRoot = Join-Path $installed 'x64-windows'
$libtorrentBuild = Join-Path $builds 'libtorrent'
$libtorrentStage = Join-Path $stage 'libtorrent'
$libtorrentDll = Join-Path $libtorrentStage 'bin\torrent-rasterbar.dll'
if (-not (Test-Path -LiteralPath $libtorrentDll)) {
    Invoke-Native $cmake @(
        '-S', $libtorrentSource, '-B', $libtorrentBuild,
        '-G', $generator, '-A', 'x64',
        "-DCMAKE_TOOLCHAIN_FILE=$toolchain",
        "-DVCPKG_INSTALLED_DIR=$installed",
        '-DVCPKG_TARGET_TRIPLET=x64-windows',
        "-DCMAKE_INSTALL_PREFIX=$libtorrentStage",
        '-DBUILD_SHARED_LIBS=ON', '-Dstatic_runtime=OFF',
        '-Dbuild_tests=OFF', '-Dbuild_examples=OFF', '-Dbuild_tools=OFF', '-Dpython-bindings=OFF'
    )
    Invoke-Native $cmake @('--build', $libtorrentBuild, '--config', 'Release', '--parallel')
    Invoke-Native $cmake @('--install', $libtorrentBuild, '--config', 'Release')
}
else {
    Write-Host "Reusing existing libtorrent stage at $libtorrentStage"
}

$qbitBuild = Join-Path $builds 'qbittorrent'
$qbitStage = Join-Path $stage 'qbittorrent'
$prefixPath = "$libtorrentStage;$tripletRoot"
Invoke-Native $cmake @(
    '-S', $qbitSource, '-B', $qbitBuild,
    '-G', $generator, '-A', 'x64',
    "-DCMAKE_TOOLCHAIN_FILE=$toolchain",
    "-DVCPKG_INSTALLED_DIR=$installed",
    '-DVCPKG_TARGET_TRIPLET=x64-windows',
    "-DCMAKE_PREFIX_PATH=$prefixPath",
    "-DCMAKE_INSTALL_PREFIX=$qbitStage",
    '-DGUI=OFF', '-DWEBUI=ON', '-DSTACKTRACE=OFF'
)
Invoke-Native $cmake @('--build', $qbitBuild, '--config', 'Release', '--parallel')
Invoke-Native $cmake @('--install', $qbitBuild, '--config', 'Release')

Assert-ChildPath $OutputRoot $projectRoot
if (Test-Path -LiteralPath $OutputRoot) { Remove-Item -LiteralPath $OutputRoot -Recurse -Force }
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
$backendExecutable = Get-ChildItem -LiteralPath $qbitStage -Filter 'qbittorrent-nox.exe' -Recurse | Select-Object -First 1
if (-not $backendExecutable) { throw 'The qBittorrent build completed without qbittorrent-nox.exe.' }
Copy-Item -LiteralPath $backendExecutable.FullName -Destination (Join-Path $OutputRoot 'qbittorrent-nox.exe')

foreach ($runtimeRoot in @((Join-Path $tripletRoot 'bin'), (Join-Path $libtorrentStage 'bin'))) {
    if (Test-Path -LiteralPath $runtimeRoot) {
        Get-ChildItem -LiteralPath $runtimeRoot -Filter '*.dll' |
            Where-Object { $_.Name -notmatch '^(python\d*|boost_python).*\.dll$' } |
            Copy-Item -Destination $OutputRoot -Force
    }
}
$pluginSource = Join-Path $tripletRoot 'Qt6\plugins\sqldrivers'
if (Test-Path -LiteralPath $pluginSource) {
    $pluginOutput = Join-Path $OutputRoot 'sqldrivers'
    New-Item -ItemType Directory -Path $pluginOutput -Force | Out-Null
    Copy-Item -Path (Join-Path $pluginSource '*.dll') -Destination $pluginOutput -Force
}

$pythonOutput = Join-Path $OutputRoot 'Python'
Expand-Archive -LiteralPath $pythonArchive -DestinationPath $pythonOutput
$searchPlugins = Join-Path $OutputRoot 'SearchPlugins'
New-Item -ItemType Directory -Path $searchPlugins -Force | Out-Null
$novaRuntimeOutput = Join-Path $searchPlugins 'nova3'
$novaEnginesOutput = Join-Path $searchPlugins 'engines'
New-Item -ItemType Directory -Path $novaRuntimeOutput,$novaEnginesOutput -Force | Out-Null
Copy-Item -Path (Join-Path $qbitSource 'src\searchengine\nova3\*.py') -Destination $novaRuntimeOutput -Force
$searchEngineSource = Join-Path $searchPluginsSource 'nova3\engines'
if (Test-Path -LiteralPath $searchEngineSource) {
    Copy-Item -Path (Join-Path $searchEngineSource '*.py') -Destination $novaEnginesOutput -Force
}
else {
    throw "Pinned qBittorrent search-plugins source is missing nova3 engines at '$searchEngineSource'."
}

$licenseOutput = Join-Path $OutputRoot 'Licenses'
New-Item -ItemType Directory -Path $licenseOutput -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $qbitSource 'COPYING') -Destination (Join-Path $licenseOutput 'qBittorrent-COPYING.txt')
Copy-Item -LiteralPath (Join-Path $libtorrentSource 'COPYING') -Destination (Join-Path $licenseOutput 'libtorrent-COPYING.txt')
foreach ($package in 'boost','openssl','qtbase','qttools','zlib') {
    $copyright = Join-Path $tripletRoot "share\$package\copyright"
    if (Test-Path -LiteralPath $copyright) { Copy-Item -LiteralPath $copyright -Destination (Join-Path $licenseOutput "$package-copyright.txt") }
}
Copy-Item -LiteralPath (Join-Path $scriptRoot 'SOURCE-OFFER.txt') -Destination $licenseOutput
$versions | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputRoot 'versions.json') -Encoding utf8

$header = [IO.File]::ReadAllBytes((Join-Path $OutputRoot 'qbittorrent-nox.exe'))[0..1]
if ($header[0] -ne 0x4D -or $header[1] -ne 0x5A) { throw 'Packaged backend is not a Windows PE executable.' }
Write-Host "Backend package created at $OutputRoot"
