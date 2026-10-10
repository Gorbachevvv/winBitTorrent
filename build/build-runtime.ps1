[CmdletBinding()]
#Requires -Version 7.0
param(
    [ValidateSet('x64','arm64')]
    [string] $Platform = 'x64',
    [string] $VcpkgRoot,
    [string] $WorkRoot,
    [string] $OutputRoot,
    [switch] $Clean
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$scriptRoot = if ([string]::IsNullOrWhiteSpace($PSScriptRoot)) { Split-Path -Parent $MyInvocation.MyCommand.Path } else { $PSScriptRoot }
$projectRoot = Split-Path $scriptRoot -Parent
$Platform = $Platform.ToLowerInvariant()
if ([string]::IsNullOrWhiteSpace($WorkRoot)) { $WorkRoot = Join-Path $scriptRoot ".runtime-work\$Platform" }
if ([string]::IsNullOrWhiteSpace($OutputRoot)) { $OutputRoot = Join-Path $projectRoot 'Backend' }
if ($Platform -eq 'arm64') {
    if (-not $PSBoundParameters.ContainsKey('OutputRoot')) { $OutputRoot = Join-Path $OutputRoot 'arm64' }
}
$triplet = "$Platform-windows"
$cmakePlatform = if ($Platform -eq 'arm64') { 'ARM64' } else { 'x64' }
. (Join-Path $scriptRoot 'architecture.ps1')

$versions = [ordered]@{
    Architecture = $Platform
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
$pythonArchitecture = if ($Platform -eq 'arm64') { 'arm64' } else { 'amd64' }
$pythonFile = "python-3.13.14-embed-$pythonArchitecture.zip"
$pythonArchive = Join-Path $downloads $pythonFile
# Checksums: https://www.python.org/downloads/release/python-31314/
$pythonSha256 = if ($Platform -eq 'arm64') { '8B5BFC935A24B55C17410AA0B21016EBEEE225C96ADDF008D1D3CD83FF52EB43' } else { '90B4E5B9898B72D744650524BFF92377C367F44BD5FBD09E3148656C080AD907' }
$pluginsArchive = Join-Path $downloads 'search-plugins-73613af6545fd2d4d72f59591309a8908b340c62.zip'
Get-VerifiedFile 'https://github.com/arvidn/libtorrent/archive/refs/tags/v2.0.13.zip' $libtorrentArchive '9DB3BF42A14F8D3FBFA41FABAC9DD0A698777759DF03FE85FE04A9E389DA94B2'
Get-VerifiedFile 'https://github.com/qbittorrent/qBittorrent/archive/refs/tags/release-5.2.3.zip' $qbitSourceArchive '0EADBCA2C98610B7F1F95B2DE1A9E76348668E865FDC025E3122E1CEEDA0D7C5'
Get-VerifiedFile 'https://github.com/arvidn/try_signal/archive/105cce59972f925a33aa6b1c3109e4cd3caf583d.zip' $trySignalArchive 'EB29241D96046B60E54AA4CC55BB6051C51F4EE07002C5EC72ECB877DECD78F5'
Get-VerifiedFile "https://www.python.org/ftp/python/3.13.14/$pythonFile" $pythonArchive $pythonSha256
Get-VerifiedFile 'https://github.com/qbittorrent/search-plugins/archive/73613af6545fd2d4d72f59591309a8908b340c62.zip' $pluginsArchive 'E71DF8E6046F74C10166A2473173A578BC209BC837117328F96A60BBBFE10160'

$libtorrentSource = Join-Path $sources 'libtorrent-2.0.13'
if (-not (Test-Path -LiteralPath (Join-Path $libtorrentSource 'CMakeLists.txt'))) { Expand-Archive -LiteralPath $libtorrentArchive -DestinationPath $sources }
$trySignalTarget = Join-Path $libtorrentSource 'deps\try_signal'
if (-not (Test-Path -LiteralPath (Join-Path $trySignalTarget 'try_signal.cpp'))) {
    $tryTemp = Join-Path $sources 'try-signal-temp'
    if (Test-Path -LiteralPath $tryTemp) { Assert-ChildPath $tryTemp $sources; Remove-Item -LiteralPath $tryTemp -Recurse -Force }
    Expand-Archive -LiteralPath $trySignalArchive -DestinationPath $tryTemp
    New-Item -ItemType Directory -Path $trySignalTarget -Force | Out-Null
    Copy-Item -Path (Join-Path (Get-ChildItem $tryTemp -Directory | Select-Object -First 1).FullName '*') -Destination $trySignalTarget -Recurse -Force
    Assert-ChildPath $tryTemp $sources
    Remove-Item -LiteralPath $tryTemp -Recurse -Force
}

$env:VCPKG_FEATURE_FLAGS = 'manifests,versions'
$savedLlvmInstallDir = $env:LLVMInstallDir
$savedKeepEnvVars = $env:VCPKG_KEEP_ENV_VARS
try {
    # OpenSSL's ARM64 assembly uses clang. vcpkg sanitizes PATH, so expose an
    # already-installed LLVM through its supported lookup hint instead.
    if ($Platform -eq 'arm64') {
        $clang = Get-Command clang.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($clang) {
            $installedDirLine = & $clang.Source --version | Select-String '^InstalledDir:\s*(.+)$' | Select-Object -First 1
            if ($installedDirLine) {
                $installedDir = $installedDirLine.Matches[0].Groups[1].Value
                if (Test-Path -LiteralPath (Join-Path $installedDir 'clang.exe')) {
                    $env:LLVMInstallDir = Split-Path -Parent $installedDir
                    $env:VCPKG_KEEP_ENV_VARS = (@($savedKeepEnvVars,'LLVMInstallDir') | Where-Object { $_ }) -join ';'
                }
            }
        }
    }
    & $vcpkg install "--vcpkg-root=$VcpkgRoot" --triplet $triplet --x-manifest-root (Join-Path $scriptRoot 'runtime') --x-install-root $installed
    if ($LASTEXITCODE -ne 0) { throw "vcpkg install failed with exit code $LASTEXITCODE." }
}
finally {
    $env:LLVMInstallDir = $savedLlvmInstallDir
    $env:VCPKG_KEEP_ENV_VARS = $savedKeepEnvVars
}
$toolchain = Join-Path $VcpkgRoot 'scripts\buildsystems\vcpkg.cmake'
$libtorrentBuild = Join-Path $builds 'libtorrent'
if (-not (Test-Path -LiteralPath (Join-Path $stage 'bin\torrent-rasterbar.dll'))) {
    & $cmake -S $libtorrentSource -B $libtorrentBuild -G $generator -A $cmakePlatform `
        "-DCMAKE_TOOLCHAIN_FILE=$toolchain" "-DVCPKG_INSTALLED_DIR=$installed" "-DVCPKG_TARGET_TRIPLET=$triplet" `
        "-DCMAKE_INSTALL_PREFIX=$stage" '-DBUILD_SHARED_LIBS=ON' '-Dstatic_runtime=OFF' `
        '-Dbuild_tests=OFF' '-Dbuild_examples=OFF' '-Dbuild_tools=OFF' '-Dpython-bindings=OFF'
    if ($LASTEXITCODE -ne 0) { throw 'libtorrent CMake configure failed.' }
    & $cmake --build $libtorrentBuild --config Release --parallel
    if ($LASTEXITCODE -ne 0) { throw 'libtorrent build failed.' }
    & $cmake --install $libtorrentBuild --config Release
    if ($LASTEXITCODE -ne 0) { throw 'libtorrent install failed.' }
}
Assert-NativeArchitecture (Join-Path $stage 'bin\torrent-rasterbar.dll') $Platform

foreach ($name in 'qbittorrent-nox.exe','Qt6Core.dll','Qt6Network.dll','Qt6Sql.dll','Qt6Xml.dll') {
    $legacy = Join-Path $OutputRoot $name
    if (Test-Path -LiteralPath $legacy) { Remove-Item -LiteralPath $legacy -Force }
}
$legacySql = Join-Path $OutputRoot 'sqldrivers'
if (Test-Path -LiteralPath $legacySql) { Assert-ChildPath $legacySql $OutputRoot; Remove-Item -LiteralPath $legacySql -Recurse -Force }

$pythonOutput = Join-Path $OutputRoot 'Python'
if (Test-Path -LiteralPath $pythonOutput) { Assert-ChildPath $pythonOutput $OutputRoot; Remove-Item -LiteralPath $pythonOutput -Recurse -Force }
Expand-Archive -LiteralPath $pythonArchive -DestinationPath $pythonOutput
if ($Platform -eq 'arm64') {
    # CPython's ARM64 archive includes the x64-only exception helper from the
    # MSVC redist; its native ARM64 modules only import vcruntime140.dll.
    $x64Helper = Join-Path $pythonOutput 'vcruntime140_1.dll'
    if (Test-Path -LiteralPath $x64Helper) { Remove-Item -LiteralPath $x64Helper -Force }
}
Assert-NativeArchitecture (Join-Path $pythonOutput 'python.exe') $Platform
$pluginSource = Join-Path $sources 'search-plugins-73613af6545fd2d4d72f59591309a8908b340c62'
if (-not (Test-Path $pluginSource)) { Expand-Archive -LiteralPath $pluginsArchive -DestinationPath $sources }
$searchOutput = Join-Path $OutputRoot 'SearchPlugins'
if (Test-Path -LiteralPath $searchOutput) { Assert-ChildPath $searchOutput $OutputRoot; Remove-Item -LiteralPath $searchOutput -Recurse -Force }
New-Item -ItemType Directory -Path (Join-Path $searchOutput 'nova3'),(Join-Path $searchOutput 'engines') -Force | Out-Null
$qbitSource = Join-Path $sources 'qBittorrent-release-5.2.3'
if (-not (Test-Path $qbitSource)) { Expand-Archive -LiteralPath $qbitSourceArchive -DestinationPath $sources }
$novaRuntime = Join-Path $qbitSource 'src\searchengine\nova3'
Copy-Item -Path (Join-Path $novaRuntime '*.py') -Destination (Join-Path $searchOutput 'nova3') -Force
Copy-Item -Path (Join-Path $pluginSource 'nova3\engines\*.py') -Destination (Join-Path $searchOutput 'engines') -Force

$licenseOutput = Join-Path $OutputRoot 'Licenses'
New-Item -ItemType Directory -Path $licenseOutput -Force | Out-Null
Copy-Item (Join-Path $libtorrentSource 'COPYING') (Join-Path $licenseOutput 'libtorrent-COPYING.txt') -Force
Copy-Item (Join-Path $installed "$triplet\share\openssl\copyright") (Join-Path $licenseOutput 'openssl-copyright.txt') -Force
Copy-Item (Join-Path $scriptRoot 'SOURCE-OFFER.txt') $licenseOutput -Force
$versions | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputRoot 'versions.json') -Encoding utf8
Write-Host "Runtime assets and pinned libtorrent stage created under $OutputRoot and $stage"
