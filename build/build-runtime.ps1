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
$projectRoot = Split-Path $scriptRoot -Parent
if ([string]::IsNullOrWhiteSpace($WorkRoot)) { $WorkRoot = Join-Path $scriptRoot '.runtime-work' }
if ([string]::IsNullOrWhiteSpace($OutputRoot)) { $OutputRoot = Join-Path $projectRoot 'Backend' }

$versions = [ordered]@{
    Libtorrent = '2.0.13'
    LibtorrentCommit = '7d7fc38fac61177fa5e02148f791b2f65250b09d'
    TrySignalCommit = '105cce59972f925a33aa6b1c3109e4cd3caf583d'
    Boost = '1.91.0'
    OpenSsl = '3.6.2'
    Python = '3.13.14'
    SearchPluginsCommit = '73613af6545fd2d4d72f59591309a8908b340c62'
    VcpkgBaseline = '4b1c85d04c9ea3730408fefcabc6123312b714d2'
}

function Assert-ChildPath([string] $Path, [string] $Root) {
    $full = [IO.Path]::GetFullPath($Path)
    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $full.StartsWith($rootFull, [StringComparison]::OrdinalIgnoreCase)) { throw "Refusing to mutate path outside '$Root': $full" }
}

function Get-VerifiedFile([string] $Uri, [string] $Path, [string] $Sha256) {
    if (Test-Path -LiteralPath $Path) {
        if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -eq $Sha256) { return }
        Remove-Item -LiteralPath $Path -Force
    }
    Invoke-WebRequest -Uri $Uri -OutFile $Path
    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    if ($actual -ne $Sha256) { Remove-Item -LiteralPath $Path -Force; throw "SHA-256 mismatch for $Uri. Expected $Sha256, got $actual." }
}

function Find-VisualStudioTool([string] $Name) {
    Get-ChildItem 'C:\Program Files\Microsoft Visual Studio' -Filter $Name -Recurse -ErrorAction SilentlyContinue |
        Select-Object -First 1 -ExpandProperty FullName
}

if ($Clean -and (Test-Path -LiteralPath $WorkRoot)) {
    Assert-ChildPath $WorkRoot $scriptRoot
    Remove-Item -LiteralPath $WorkRoot -Recurse -Force
}
Assert-ChildPath $OutputRoot $projectRoot
$downloads = Join-Path $WorkRoot 'downloads'
$sources = Join-Path $WorkRoot 'sources'
$builds = Join-Path $WorkRoot 'build'
$installed = Join-Path $WorkRoot 'vcpkg_installed'
$stage = Join-Path $WorkRoot 'stage\libtorrent'
New-Item -ItemType Directory -Path $downloads,$sources,$builds,$installed,$OutputRoot -Force | Out-Null

if (-not $VcpkgRoot) {
    $vcpkgExe = Find-VisualStudioTool 'vcpkg.exe'
    if (-not $vcpkgExe) { throw 'vcpkg.exe was not found. Install the Visual Studio C++ vcpkg component.' }
    $VcpkgRoot = Split-Path -Parent $vcpkgExe
}
$vcpkg = Join-Path $VcpkgRoot 'vcpkg.exe'
$cmake = Find-VisualStudioTool 'cmake.exe'
if (-not (Test-Path -LiteralPath $vcpkg) -or -not $cmake) { throw 'Visual Studio vcpkg/CMake tools were not found.' }
$generators = (& $cmake --help) -join "`n"
$generator = if ($generators -match 'Visual Studio 18 2026') { 'Visual Studio 18 2026' } elseif ($generators -match 'Visual Studio 17 2022') { 'Visual Studio 17 2022' } else { throw 'Visual Studio 2022 or newer is required.' }

$libtorrentArchive = Join-Path $downloads 'libtorrent-v2.0.13.zip'
$qbitSourceArchive = Join-Path $downloads 'qbittorrent-release-5.2.3-source.zip'
$trySignalArchive = Join-Path $downloads 'try_signal-105cce59972f925a33aa6b1c3109e4cd3caf583d.zip'
$pythonArchive = Join-Path $downloads 'python-3.13.14-embed-amd64.zip'
$pluginsArchive = Join-Path $downloads 'search-plugins-73613af6545fd2d4d72f59591309a8908b340c62.zip'
Get-VerifiedFile 'https://github.com/arvidn/libtorrent/archive/refs/tags/v2.0.13.zip' $libtorrentArchive '9DB3BF42A14F8D3FBFA41FABAC9DD0A698777759DF03FE85FE04A9E389DA94B2'
Get-VerifiedFile 'https://github.com/qbittorrent/qBittorrent/archive/refs/tags/release-5.2.3.zip' $qbitSourceArchive '0EADBCA2C98610B7F1F95B2DE1A9E76348668E865FDC025E3122E1CEEDA0D7C5'
Get-VerifiedFile 'https://github.com/arvidn/try_signal/archive/105cce59972f925a33aa6b1c3109e4cd3caf583d.zip' $trySignalArchive 'EB29241D96046B60E54AA4CC55BB6051C51F4EE07002C5EC72ECB877DECD78F5'
Get-VerifiedFile 'https://www.python.org/ftp/python/3.13.14/python-3.13.14-embed-amd64.zip' $pythonArchive '90B4E5B9898B72D744650524BFF92377C367F44BD5FBD09E3148656C080AD907'
Get-VerifiedFile 'https://github.com/qbittorrent/search-plugins/archive/73613af6545fd2d4d72f59591309a8908b340c62.zip' $pluginsArchive 'E71DF8E6046F74C10166A2473173A578BC209BC837117328F96A60BBBFE10160'

$libtorrentSource = Join-Path $sources 'libtorrent-2.0.13'
if (-not (Test-Path -LiteralPath (Join-Path $libtorrentSource 'CMakeLists.txt'))) { Expand-Archive -LiteralPath $libtorrentArchive -DestinationPath $sources }
$trySignalTarget = Join-Path $libtorrentSource 'deps\try_signal'
if (-not (Test-Path -LiteralPath (Join-Path $trySignalTarget 'try_signal.cpp'))) {
    $tryTemp = Join-Path $sources 'try-signal-temp'
    if (Test-Path $tryTemp) { Remove-Item $tryTemp -Recurse -Force }
    Expand-Archive -LiteralPath $trySignalArchive -DestinationPath $tryTemp
    New-Item -ItemType Directory -Path $trySignalTarget -Force | Out-Null
    Copy-Item -Path (Join-Path (Get-ChildItem $tryTemp -Directory | Select-Object -First 1).FullName '*') -Destination $trySignalTarget -Recurse -Force
    Remove-Item $tryTemp -Recurse -Force
}

$env:VCPKG_FEATURE_FLAGS = 'manifests,versions'
& $vcpkg install --triplet x64-windows --x-manifest-root (Join-Path $scriptRoot 'runtime') --x-install-root $installed
if ($LASTEXITCODE -ne 0) { throw "vcpkg install failed with exit code $LASTEXITCODE." }
$toolchain = Join-Path $VcpkgRoot 'scripts\buildsystems\vcpkg.cmake'
$libtorrentBuild = Join-Path $builds 'libtorrent'
if (-not (Test-Path -LiteralPath (Join-Path $stage 'bin\torrent-rasterbar.dll'))) {
    & $cmake -S $libtorrentSource -B $libtorrentBuild -G $generator -A x64 `
        "-DCMAKE_TOOLCHAIN_FILE=$toolchain" "-DVCPKG_INSTALLED_DIR=$installed" '-DVCPKG_TARGET_TRIPLET=x64-windows' `
        "-DCMAKE_INSTALL_PREFIX=$stage" '-DBUILD_SHARED_LIBS=ON' '-Dstatic_runtime=OFF' `
        '-Dbuild_tests=OFF' '-Dbuild_examples=OFF' '-Dbuild_tools=OFF' '-Dpython-bindings=OFF'
    if ($LASTEXITCODE -ne 0) { throw 'libtorrent CMake configure failed.' }
    & $cmake --build $libtorrentBuild --config Release --parallel
    if ($LASTEXITCODE -ne 0) { throw 'libtorrent build failed.' }
    & $cmake --install $libtorrentBuild --config Release
    if ($LASTEXITCODE -ne 0) { throw 'libtorrent install failed.' }
}

foreach ($name in 'qbittorrent-nox.exe','Qt6Core.dll','Qt6Network.dll','Qt6Sql.dll','Qt6Xml.dll') {
    $legacy = Join-Path $OutputRoot $name
    if (Test-Path -LiteralPath $legacy) { Remove-Item -LiteralPath $legacy -Force }
}
$legacySql = Join-Path $OutputRoot 'sqldrivers'
if (Test-Path -LiteralPath $legacySql) { Assert-ChildPath $legacySql $OutputRoot; Remove-Item -LiteralPath $legacySql -Recurse -Force }

$pythonOutput = Join-Path $OutputRoot 'Python'
if (Test-Path $pythonOutput) { Remove-Item $pythonOutput -Recurse -Force }
Expand-Archive -LiteralPath $pythonArchive -DestinationPath $pythonOutput
$pluginSource = Join-Path $sources 'search-plugins-73613af6545fd2d4d72f59591309a8908b340c62'
if (-not (Test-Path $pluginSource)) { Expand-Archive -LiteralPath $pluginsArchive -DestinationPath $sources }
$searchOutput = Join-Path $OutputRoot 'SearchPlugins'
if (Test-Path $searchOutput) { Remove-Item $searchOutput -Recurse -Force }
New-Item -ItemType Directory -Path (Join-Path $searchOutput 'nova3'),(Join-Path $searchOutput 'engines') -Force | Out-Null
$qbitSource = Join-Path $sources 'qBittorrent-release-5.2.3'
if (-not (Test-Path $qbitSource)) { Expand-Archive -LiteralPath $qbitSourceArchive -DestinationPath $sources }
$novaRuntime = Join-Path $qbitSource 'src\searchengine\nova3'
Copy-Item -Path (Join-Path $novaRuntime '*.py') -Destination (Join-Path $searchOutput 'nova3') -Force
Copy-Item -Path (Join-Path $pluginSource 'nova3\engines\*.py') -Destination (Join-Path $searchOutput 'engines') -Force

$licenseOutput = Join-Path $OutputRoot 'Licenses'
New-Item -ItemType Directory -Path $licenseOutput -Force | Out-Null
Copy-Item (Join-Path $libtorrentSource 'COPYING') (Join-Path $licenseOutput 'libtorrent-COPYING.txt') -Force
Copy-Item (Join-Path $installed 'x64-windows\share\openssl\copyright') (Join-Path $licenseOutput 'openssl-copyright.txt') -Force
Copy-Item (Join-Path $scriptRoot 'SOURCE-OFFER.txt') $licenseOutput -Force
$versions | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputRoot 'versions.json') -Encoding utf8
Write-Host "Runtime assets and pinned libtorrent stage created under $OutputRoot and $stage"
