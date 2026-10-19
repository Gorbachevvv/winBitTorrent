#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PackagePath,
    [string]$ReportPath
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$package = (Resolve-Path -LiteralPath $PackagePath).Path
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$scratch = Join-Path $tempRoot ('WinBitTorrent-StoreVerify-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch | Out-Null
try {
    $payload = Join-Path $scratch 'payload'
    if ([IO.Path]::GetExtension($package) -in '.msixupload', '.appxupload') {
        $upload = Join-Path $scratch 'upload'
        [IO.Compression.ZipFile]::ExtractToDirectory($package, $upload)
        $inner = @(Get-ChildItem -LiteralPath $upload -File | Where-Object { $_.Extension -in '.msix', '.appx' })
        if ($inner.Count -ne 1) { throw 'Expected one x64 package in the upload.' }
        [IO.Compression.ZipFile]::ExtractToDirectory($inner[0].FullName, $payload)
    } else { [IO.Compression.ZipFile]::ExtractToDirectory($package, $payload) }
    $files = @(Get-ChildItem -LiteralPath $payload -File -Recurse)
    $forbidden = @($files | Where-Object {
        $_.FullName -match '\\(Python|SearchPlugins|Assets\\Trackers)\\' -or
        $_.Name -match '^(python.*\.(exe|dll)|qbittorrent.*\.exe|Qt6.*\.dll|qsqlite\.dll|.*setup.*\.exe)$' -or
        $_.Extension -eq '.py' -or $_.Name -match '^(TrackerSearchView|CatalogView|SearchView|SearchPluginsWindow|CookiesWindow)\.xbf$'
    })
    if ($forbidden.Count) { throw ('Forbidden Store payload: ' + ($forbidden.Name -join ', ')) }
    $deniedTypes = @('RuTrackerProvider','PirateBayProvider','TmdbCatalogProvider','GitHubUpdateService',
        'TrackerSearchViewModel','CatalogViewModel','SearchPluginsWindow','RunSearchAsync','InstallSearchPluginAsync')
    foreach ($relative in @('WinBitTorrent.dll','WinBitTorrent.Infrastructure.dll','EngineHost\WinBitTorrent.EngineHost.dll')) {
        $binary = Join-Path $payload $relative
        if (-not (Test-Path -LiteralPath $binary)) { throw "Missing required binary: $relative" }
        $metadata = [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($binary))
        if (-not $metadata.Contains('MicrosoftStore')) { throw "Non-Store binary in package: $relative" }
        foreach ($name in $deniedTypes) {
            if ($metadata.Contains($name)) { throw "Excluded implementation $name is present in $relative" }
        }
    }
    [xml]$manifest = Get-Content -LiteralPath (Join-Path $payload 'AppxManifest.xml')
    $expected = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'store.settings.json') -Raw | ConvertFrom-Json
    if ($manifest.Package.Identity.Name -ne $expected.identityName -or $manifest.Package.Identity.Publisher -ne $expected.publisher) {
        throw 'Package identity does not match Partner Center settings.'
    }
    $startup = $manifest.SelectSingleNode('//*[local-name()="StartupTask"]')
    if (-not $startup -or $startup.Enabled -ne 'false') { throw 'Startup must default to disabled.' }
    $toastActivation = $manifest.SelectSingleNode('//*[local-name()="ToastNotificationActivation"]')
    $toastServer = $manifest.SelectSingleNode('//*[local-name()="ExeServer"]')
    if (-not $toastActivation -or -not $toastServer -or $toastServer.Executable -ne 'WinBitTorrent.exe' -or
        $toastServer.Arguments -ne '----AppNotificationActivated:') {
        throw 'Packaged notification activation is missing or invalid.'
    }
    $report = [ordered]@{
        artifact = $package
        sha256 = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash
        identity = $manifest.Package.Identity.Name
        publisher = $manifest.Package.Identity.Publisher
        version = $manifest.Package.Identity.Version
        architecture = $manifest.Package.Identity.ProcessorArchitecture
        fileCount = $files.Count
        excludedImplementations = $deniedTypes
        packageInspection = 'passed'
        certification = 'Not a Microsoft certification result; run WACK and complete Partner Center review.'
    }
    if ($ReportPath) { $report | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $ReportPath -Encoding UTF8 }
    Write-Host "Store payload inspection passed ($($files.Count) files)." -ForegroundColor Green
} finally {
    $resolved = [IO.Path]::GetFullPath($scratch)
    if (-not $resolved.StartsWith((Join-Path $tempRoot 'WinBitTorrent-StoreVerify-'), [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Unexpected verification cleanup path.'
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
