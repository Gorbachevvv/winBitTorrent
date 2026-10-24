[CmdletBinding()]
#Requires -Version 5.1
param(
    [ValidateSet('x64','arm64')]
    [string] $Platform = 'x64',
    [string] $RuntimeWorkRoot,
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
if ([string]::IsNullOrWhiteSpace($WorkRoot)) { $WorkRoot = Join-Path $scriptRoot ".engine-work\$Platform" }
if ([string]::IsNullOrWhiteSpace($OutputRoot)) { $OutputRoot = Join-Path $projectRoot 'artifacts\runtime\Engine' }
if ($Platform -eq 'arm64') {
    if (-not $PSBoundParameters.ContainsKey('OutputRoot')) { $OutputRoot = Join-Path $OutputRoot 'arm64' }
}
$triplet = "$Platform-windows"
$cmakePlatform = if ($Platform -eq 'arm64') { 'ARM64' } else { 'x64' }
. (Join-Path $scriptRoot 'architecture.ps1')
Assert-MsvcBuildTools $Platform
$WorkRoot = [IO.Path]::GetFullPath($WorkRoot)
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)

function Assert-ChildPath([string] $Path, [string] $Root) {
    $full = [IO.Path]::GetFullPath($Path)
    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $full.StartsWith($rootFull, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to mutate path outside '$Root': $full"
    }
}

if ($Clean -and (Test-Path -LiteralPath $WorkRoot)) {
    Assert-ChildPath $WorkRoot $scriptRoot
    Remove-Item -LiteralPath $WorkRoot -Recurse -Force
}

if ([string]::IsNullOrWhiteSpace($RuntimeWorkRoot)) {
    $RuntimeWorkRoot = Join-Path $scriptRoot ".runtime-work\$Platform"
}
$runtimeWork = [IO.Path]::GetFullPath($RuntimeWorkRoot)
$installed = Join-Path $runtimeWork 'vcpkg_installed'
$libtorrentStage = Join-Path $runtimeWork 'stage\libtorrent'
$libtorrentDll = Join-Path $libtorrentStage 'bin\torrent-rasterbar.dll'
if (-not (Test-Path -LiteralPath $libtorrentDll)) {
    & (Join-Path $scriptRoot 'build-runtime.ps1') -Platform $Platform -VcpkgRoot $VcpkgRoot -WorkRoot $runtimeWork
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $libtorrentDll)) { throw 'Unable to prepare the pinned libtorrent runtime.' }
}
Assert-NativeArchitecture $libtorrentDll $Platform

if (-not $VcpkgRoot) {
    $candidate = Get-ChildItem 'C:\Program Files\Microsoft Visual Studio' -Filter vcpkg.exe -Recurse -ErrorAction SilentlyContinue |
        Select-Object -First 1 -ExpandProperty DirectoryName
    if (-not $candidate) { throw 'vcpkg.exe was not found. Install the Visual Studio C++ vcpkg component or pass -VcpkgRoot.' }
    $VcpkgRoot = $candidate
}
$toolchain = Join-Path $VcpkgRoot 'scripts\buildsystems\vcpkg.cmake'
$toolchain = [IO.Path]::GetFullPath($toolchain)

$cmake = Get-ChildItem 'C:\Program Files\Microsoft Visual Studio' -Filter cmake.exe -Recurse -ErrorAction SilentlyContinue |
    Where-Object FullName -Match 'CommonExtensions\\Microsoft\\CMake' |
    Select-Object -First 1 -ExpandProperty FullName
if (-not $cmake) { throw 'Visual Studio CMake was not found.' }
$generators = (& $cmake --help) -join "`n"
$generator = if ($generators -match 'Visual Studio 18 2026') { 'Visual Studio 18 2026' } elseif ($generators -match 'Visual Studio 17 2022') { 'Visual Studio 17 2022' } else { throw 'Visual Studio 2022 or newer is required.' }

$source = Join-Path $projectRoot 'native\WinBitTorrent.Native'
$build = Join-Path $WorkRoot 'native'
$stage = Join-Path $WorkRoot 'stage'
New-Item -ItemType Directory -Path $build,$stage -Force | Out-Null

Invoke-BuildTool $cmake @('-S', $source, '-B', $build, '-G', $generator, '-A', $cmakePlatform,
    "-DCMAKE_TOOLCHAIN_FILE=$toolchain", "-DVCPKG_INSTALLED_DIR=$installed",
    "-DVCPKG_TARGET_TRIPLET=$triplet", "-DCMAKE_PREFIX_PATH=$libtorrentStage", "-DCMAKE_INSTALL_PREFIX=$stage")
Invoke-BuildTool $cmake @('--build', $build, '--config', 'Release', '--parallel')
Invoke-BuildTool $cmake @('--install', $build, '--config', 'Release')

Assert-ChildPath $OutputRoot $projectRoot
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
foreach ($binary in Get-ChildItem -LiteralPath $stage -Filter '*.dll' -File) {
    Assert-NativeArchitecture $binary.FullName $Platform
    Copy-Item -LiteralPath $binary.FullName -Destination $OutputRoot -Force
}
Copy-Item -LiteralPath $libtorrentDll -Destination $OutputRoot -Force
$dependencyRoot = Join-Path $installed "$triplet\bin"
foreach ($pattern in 'libcrypto-3*.dll','libssl-3*.dll','boost_json-*.dll') {
    $dependencies = @(Get-ChildItem -LiteralPath $dependencyRoot -Filter $pattern -File)
    if ($dependencies.Count -eq 0) { throw "Missing native dependency '$pattern' in $dependencyRoot." }
    foreach ($dependency in $dependencies) { Copy-Item -LiteralPath $dependency.FullName -Destination $OutputRoot -Force }
}
foreach ($binary in Get-ChildItem -LiteralPath $OutputRoot -Filter '*.dll' -File) {
    Assert-NativeArchitecture $binary.FullName $Platform
}

Write-Host "Engine native runtime staged at $OutputRoot"
