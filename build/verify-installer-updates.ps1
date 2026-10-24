#Requires -Version 5.1
<#
.SYNOPSIS
    Tests the real Inno script with a small isolated application, including old updater arguments.
.DESCRIPTION
    Uses a unique AppId, application mutex and temporary install folder. No WinBitTorrent
    profile, association or running client is touched. Logs and fixtures remain under TestRoot.
#>
[CmdletBinding()]
param(
    [string]$TestRoot = (Join-Path $env:TEMP ('WinBitTorrent-InstallerTests-' + [Guid]::NewGuid().ToString('N'))),
    [switch]$ReproduceOnly
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$TestRoot = [IO.Path]::GetFullPath($TestRoot)
New-Item -ItemType Directory -Force -Path $TestRoot | Out-Null
$fixtureId = 'WinBitTorrentUpdateFixture-' + [Guid]::NewGuid().ToString('N')
$mutexName = $fixtureId + '-mutex'
$payload = Join-Path $TestRoot 'payload'
$installed = Join-Path $TestRoot 'installed app'
New-Item -ItemType Directory -Force -Path $payload | Out-Null
$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6.6+ is required.' }
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw '.NET Framework C# compiler is required for the fixture.' }

$fixtureSource = @'
using System;
using System.IO;
using System.Threading;
class Program {
    static void Main(string[] args) {
        if (args.Length > 0 && args[0] == "--hold") {
            using (var mutex = args[1] == "none" ? null : new Mutex(false, args[1]))
            using (var lockedFile = File.Open(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "backend.bin"), FileMode.Open, FileAccess.Read, FileShare.Read)) {
                File.WriteAllText(args[2], "ready");
                Thread.Sleep(int.Parse(args[3]));
            }
        } else if (args.Length == 0) {
            File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "relaunched.txt"), "launched\n");
        }
    }
}
'@
$sourcePath = Join-Path $TestRoot 'Fixture.cs'
Set-Content -LiteralPath $sourcePath -Value $fixtureSource -Encoding UTF8
& $csc /nologo /target:winexe "/out:$payload\WinBitTorrent.exe" $sourcePath
if ($LASTEXITCODE -ne 0) { throw 'Fixture compilation failed.' }
Set-Content -LiteralPath (Join-Path $payload 'backend.bin') -Value 'old backend'
Set-Content -LiteralPath (Join-Path $payload 'version.txt') -Value 'old'

function Compile-FixtureInstaller([string]$script, [string]$name, [string]$version) {
    # Change only identity and asset paths. Upgrade, shutdown and relaunch code stays intact.
    $script = $script.Replace('#define AppName "WinBitTorrent"', ('#define AppName "' + $fixtureId + '"'))
    $script = [regex]::Replace($script, '(?m)^AppId=.*$', 'AppId=' + $fixtureId)
    $script = $script.Replace('WinBitTorrentAppMutex', $mutexName)
    $script = $script.Replace('..\..\LICENSE', (Join-Path $repoRoot 'LICENSE'))
    $script = $script.Replace('..\..\Assets\WinBitTorrent.ico', (Join-Path $repoRoot 'src\WinBitTorrent\Assets\WinBitTorrent.ico'))
    $script = $script.Replace('..\..\src\WinBitTorrent\Assets\WinBitTorrent.ico', (Join-Path $repoRoot 'src\WinBitTorrent\Assets\WinBitTorrent.ico'))
    $script = $script.Replace('assets\WizardImageFile_WinBitTorrent.png', (Join-Path $PSScriptRoot 'installer\assets\WizardImageFile_WinBitTorrent.png'))
    $output = Join-Path $TestRoot $name
    $scriptPath = Join-Path $TestRoot ($name + '.iss')
    Set-Content -LiteralPath $scriptPath -Value $script -Encoding UTF8
    & $iscc /Q $scriptPath "/DAppVersion=$version" "/DPayloadDir=$payload" "/DOutputDir=$output"
    if ($LASTEXITCODE -ne 0) { throw "ISCC failed for $name." }
    return Join-Path $output "$fixtureId-$version-setup.exe"
}

function Start-Setup([string]$setup, [string]$logName, [string[]]$extra = @()) {
    $log = Join-Path $TestRoot ($logName + '.log')
    $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER', '/TASKS=""', "/LOG=`"$log`"") + $extra
    return Start-Process -FilePath $setup -ArgumentList $arguments -WindowStyle Hidden -PassThru
}

function Finish-Setup($process) {
    if (-not $process.WaitForExit(90000)) {
        $process.Kill()
        throw 'Fixture installer timed out. Inspect the test logs.'
    }
    $process.Refresh()
    return $process.ExitCode
}

function Start-HoldingClient([string]$name, [string]$mutex) {
    $ready = Join-Path $TestRoot ($name + '-ready.txt')
    $client = Start-Process -FilePath (Join-Path $installed 'WinBitTorrent.exe') -ArgumentList @('--hold', $mutex, "`"$ready`"", '6000') -WindowStyle Hidden -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    while (-not (Test-Path $ready)) {
        if ($client.HasExited -or [DateTime]::UtcNow -gt $deadline) { throw 'Fixture client failed to start.' }
        Start-Sleep -Milliseconds 100
    }
    return $client
}

$client = $null
try {
    $legacy = (& git -C $repoRoot show 'f49c6b9:build/installer/WinBitTorrent.iss') -join "`n"
    if ($LASTEXITCODE -ne 0) { throw 'Legacy installer source could not be read from Git.' }
    $legacySetup = Compile-FixtureInstaller $legacy 'legacy' '1.0.0'
    $exitCode = Finish-Setup (Start-Setup $legacySetup 'initial-install' @("/DIR=`"$installed`""))
    if ($exitCode -ne 0) { throw "Initial fixture install failed ($exitCode)." }

    $client = Start-HoldingClient 'legacy' $mutexName
    # These are exactly the old client's arguments: no PID and no installation directory.
    $exitCode = Finish-Setup (Start-Setup $legacySetup 'legacy-race' @('/FORCECLOSEAPPLICATIONS', '/RELAUNCH'))
    if ($exitCode -eq 0) { throw 'The legacy startup-mutex race was not reproduced.' }
    Write-Host "Confirmed: legacy installer exits with code $exitCode while the client holds its mutex."
    $client.WaitForExit()
    $client.Dispose()
    $client = $null

    if (-not $ReproduceOnly) {
        Set-Content -LiteralPath (Join-Path $payload 'version.txt') -Value 'new'
        Set-Content -LiteralPath (Join-Path $payload 'backend.bin') -Value 'new backend'
        Set-Content -LiteralPath (Join-Path $installed 'resources.pri') -Value 'legacy PRI'
        $current = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'installer\WinBitTorrent.iss') -Raw
        $currentSetup = Compile-FixtureInstaller $current 'current' '2.0.0'

        $client = Start-HoldingClient 'current' $mutexName
        $exitCode = Finish-Setup (Start-Setup $currentSetup 'upgrade-old-client' @('/FORCECLOSEAPPLICATIONS', '/RELAUNCH'))
        if ($exitCode -ne 0) { throw "Upgrade with old updater arguments failed ($exitCode)." }
        if (-not $client.HasExited) { throw 'Installer completed before the old client stopped.' }
        $client.Dispose()
        $client = $null
        if ((Get-Content -LiteralPath (Join-Path $installed 'version.txt') -Raw).Trim() -ne 'new') { throw 'The original installation was not upgraded.' }
        if ((Get-Content -LiteralPath (Join-Path $installed 'backend.bin') -Raw).Trim() -ne 'new backend') { throw 'The backend file was not upgraded.' }
        if (Test-Path (Join-Path $installed 'resources.pri')) { throw 'Legacy PRI was not removed.' }
        $deadline = [DateTime]::UtcNow.AddSeconds(10)
        while (-not (Test-Path (Join-Path $installed 'relaunched.txt'))) {
            if ([DateTime]::UtcNow -gt $deadline) { throw 'The updated app was not relaunched.' }
            Start-Sleep -Milliseconds 100
        }
        if (@(Get-Content -LiteralPath (Join-Path $installed 'relaunched.txt')).Count -ne 1) { throw 'The updated app relaunched more than once.' }
        Write-Host 'Passed: old updater, delayed shutdown, backend replacement, custom directory, PRI cleanup and one relaunch.'

        # Early releases had no application mutex. Restart Manager must still release their files.
        $client = Start-HoldingClient 'without-mutex' 'none'
        $exitCode = Finish-Setup (Start-Setup $currentSetup 'upgrade-without-mutex' @('/FORCECLOSEAPPLICATIONS', '/RELAUNCH'))
        if ($exitCode -ne 0) { throw "Upgrade without an application mutex failed ($exitCode)." }
        $client.WaitForExit()
        $client.Dispose()
        $client = $null
        Write-Host 'Passed: upgrade from a client without an application mutex.'
    }
}
finally {
    if ($client) {
        if (-not $client.HasExited) { $client.Kill(); $client.WaitForExit() }
        $client.Dispose()
    }
    $uninstaller = Join-Path $installed 'unins000.exe'
    if (Test-Path $uninstaller) {
        $cleanup = Start-Process -FilePath $uninstaller -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART' -WindowStyle Hidden -PassThru
        if (-not $cleanup.WaitForExit(30000)) { throw 'Fixture uninstaller timed out.' }
    }
    Write-Host "Installer verification logs: $TestRoot"
}
