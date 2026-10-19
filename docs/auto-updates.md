# EXE auto-updates

The installer must support already published clients. Those clients launch Setup with
`/SILENT /SUPPRESSMSGBOXES /NORESTART /FORCECLOSEAPPLICATIONS /RELAUNCH`, then shut down
the backend and exit. Fixing only the new client's updater cannot repair that first upgrade.

## Startup race

Commit `f49c6b9` (2026-07-29) added `AppMutex=WinBitTorrentAppMutex` to the installer and
created that mutex in the client. The accompanying comment incorrectly assumed that
`CloseApplications=force` would wait for or close the client before the mutex check.

Inno Setup checks `AppMutex` during initialization, before Restart Manager runs. When the
client is still stopping its backend, the mutex exists. `/SUPPRESSMSGBOXES` selects Cancel
for the application-running dialog and Setup exits with code 1. The client then finishes
exiting, leaving the previous installation unchanged. The next launch offers the same update.

References: [AppMutex](https://jrsoftware.org/ishelp/topic_setup_appmutex.htm),
[CloseApplications](https://jrsoftware.org/ishelp/topic_setup_closeapplications.htm),
and the [initialization order in Inno Setup](https://github.com/jrsoftware/issrc/blob/main/Projects/Src/Setup.MainFunc.pas).

## Fix and release

For `/RELAUNCH`, the new installer's `InitializeSetup` waits for the application mutex to
disappear before Inno's built-in check. The wait allows up to 60 seconds for graceful shutdown.
A timeout aborts before installation and records the failure in the setup log. Interactive
installs retain the normal application-running check. Restart Manager still handles other
processes that lock files, including early clients which did not create the application mutex.

The new client also passes its installation directory explicitly, specifies a setup log in
`%TEMP%\WinBitTorrent-update`, and keeps running if it observes a nonzero installer exit code
during the first 1.5 seconds. This early check is not a guarantee against later failures; the
setup log provides diagnostics after the client exits.

Build and publish `WinBitTorrent-<version>-setup.exe` using the corrected `.iss` script.
Keep the existing installer AppId so Setup preserves the previous install directory and scope.
New clients select the installer for their running process architecture:
`WinBitTorrent-<version>-setup.exe` for x64 and
`WinBitTorrent-<version>-setup-arm64.exe` for ARM64. Publish both in the same
GitHub release. Already shipped clients select assets ending in `setup.exe`, so
the ARM64 suffix deliberately does not match their selector. An x64 client under
Windows ARM emulation keeps using x64 installers. Missing ARM64 assets lead to
the release page, never to an x64 installation. An MSIX asset does not replace
this EXE upgrade path. Use a newer release version than the published version.

```powershell
build\release.ps1 -Installer -OpenOutput:$false
```

## Verification

```powershell
build\verify-installer-updates.ps1
```

This compiles the real installer script with a small fixture, a unique AppId/mutex, disabled
association/shortcut tasks and a temporary custom install directory. It reproduces the old
failure using the script from `f49c6b9`, then checks the corrected installer with the old updater's
arguments: delayed shutdown, replacement of a locked backend file, preservation of the install
directory, cleanup of legacy `resources.pri`, one relaunch, and a client without an application
mutex. It uninstalls the fixture and preserves logs under the printed test directory.

The fixture verifies installer behavior; it does not launch every historic WinBitTorrent binary
or exercise elevation on an all-users installation.
