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
if ([string]::IsNullOrWhiteSpace($WorkRoot)) { $WorkRoot = Join-Path $scriptRoot '.engine-work' }
if ([string]::IsNullOrWhiteSpace($OutputRoot)) { $OutputRoot = Join-Path $projectRoot 'Engine' }

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

$runtimeWork = Join-Path $scriptRoot '.runtime-work'
$installed = Join-Path $runtimeWork 'vcpkg_installed'
$libtorrentStage = Join-Path $runtimeWork 'stage\libtorrent'
$libtorrentDll = Join-Path $libtorrentStage 'bin\torrent-rasterbar.dll'
if (-not (Test-Path -LiteralPath $libtorrentDll)) {
    & (Join-Path $scriptRoot 'build-runtime.ps1') -VcpkgRoot $VcpkgRoot
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $libtorrentDll)) { throw 'Unable to prepare the pinned libtorrent runtime.' }
}

if (-not $VcpkgRoot) {
    $candidate = Get-ChildItem 'C:\Program Files\Microsoft Visual Studio' -Filter vcpkg.exe -Recurse -ErrorAction SilentlyContinue |
        Select-Object -First 1 -ExpandProperty DirectoryName
    if (-not $candidate) { throw 'vcpkg.exe was not found. Install the Visual Studio C++ vcpkg component or pass -VcpkgRoot.' }
    $VcpkgRoot = $candidate
}
$toolchain = Join-Path $VcpkgRoot 'scripts\buildsystems\vcpkg.cmake'

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

& $cmake -S $source -B $build -G $generator -A x64 `
    "-DCMAKE_TOOLCHAIN_FILE=$toolchain" `
    "-DVCPKG_INSTALLED_DIR=$installed" `
    '-DVCPKG_TARGET_TRIPLET=x64-windows' `
    "-DCMAKE_PREFIX_PATH=$libtorrentStage" `
    "-DCMAKE_INSTALL_PREFIX=$stage"
if ($LASTEXITCODE -ne 0) { throw "CMake configure failed with exit code $LASTEXITCODE." }
& $cmake --build $build --config Release --parallel
if ($LASTEXITCODE -ne 0) { throw "Native build failed with exit code $LASTEXITCODE." }
& $cmake --install $build --config Release
if ($LASTEXITCODE -ne 0) { throw "Native install failed with exit code $LASTEXITCODE." }

Assert-ChildPath $OutputRoot $projectRoot
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $stage 'WinBitTorrent.Native.dll') -Destination $OutputRoot -Force
Copy-Item -LiteralPath $libtorrentDll -Destination $OutputRoot -Force
foreach ($dependency in 'libcrypto-3-x64.dll','libssl-3-x64.dll') {
    $path = Join-Path $installed "x64-windows\bin\$dependency"
    if (Test-Path -LiteralPath $path) { Copy-Item -LiteralPath $path -Destination $OutputRoot -Force }
}
Get-ChildItem (Join-Path $installed 'x64-windows\bin') -Filter 'boost_json-*.dll' |
    ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $OutputRoot -Force }

Write-Host "Engine native runtime staged at $OutputRoot"
