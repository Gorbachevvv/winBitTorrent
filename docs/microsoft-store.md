# Microsoft Store edition

## Build

Run `build/release.cmd` (or the existing Visual Studio External Tools command)
and choose **2 — Microsoft Store (MSIX)**. Alternatively double-click
`build/release-msix.cmd`, or run:

```powershell
build/release.ps1 -Msix
build/release.ps1 -Msix -Version 2.0.1
```

The build requires Visual Studio with MSIX packaging tools, the Windows SDK,
.NET 8, and `Engine/WinBitTorrent.Native.dll` produced by `build/build-engine.ps1`.
Python and Inno Setup are not required for this target. The output is self-contained
and targets Windows 10 2004 or newer, x64.

Partner Center identity is read from `build/store.settings.json`:

| Field | Value |
| --- | --- |
| Name | KVCH.WinBitTorrent |
| Publisher | CN=FA89AD65-0359-4EC6-A7A6-F63583A6312C |
| PublisherDisplayName | KVCH |

The default version comes from `Directory.Build.props`. Store package versions
use four components, with the last component zero. Increase the version before
submitting an update.

Upload `dist/WinBitTorrent-<version>-store/WinBitTorrent_<version>_x64.msixupload`
to Partner Center. It is intentionally unsigned: Microsoft signs Store deliveries.
The nested `_Test` directory also contains an unsigned `.msix`; it cannot be
installed by ordinary double-click without a trusted test signature. Do not
distribute a development certificate as part of the Store submission.

`store-validation.json` records the artifact hash and automatic payload checks.
It is not a certification report.

## Differences from portable/installer builds

The `StoreBuild=true` MSBuild property selects this edition at compile time;
Store assemblies and intermediates use separate `bin/Store` and `obj/Store` trees.

| Functionality | Store edition |
| --- | --- |
| Main tracker-search tab and RuTracker/PirateBay providers | Excluded from compilation |
| General search tab, Python runtime, installable search plugins | Excluded; engine RPC and HTTP search routes also removed |
| Catalog/TMDB provider and catalog preferences | Excluded |
| Tracker cookies window and credential store | Excluded |
| GitHub update check and installer-based self-update | Excluded; Store delivers updates |
| Transfers, RSS, remote profiles, torrent details | Retained |
| Technical trackers belonging to a torrent, DHT, peers | Retained as normal BitTorrent transport functionality |
| Windows startup | Packaged StartupTask, disabled until the user enables it |
| Torrent/magnet associations | Declared in the package; Windows controls the default app |
| Native notifications | Packaged toast/COM activation registered in the manifest |

Store data uses a separate `WinBitTorrent.Store` data directory, subject to MSIX
filesystem redirection. Existing portable settings/search plugins are not used
as the Store application's data directory.

## Submission requirements

Removing these features reduces the bundled content and extensibility surface;
it does not guarantee certification. Microsoft does not categorically prohibit
the BitTorrent protocol. Listing text, promoted content, privacy disclosures,
licensing, and actual behavior also matter.

Before submitting:

1. Supply a publicly accessible privacy policy URL in Partner Center (required
   for desktop apps). Describe local settings/download state, network disclosure
   of IP addresses to peers/trackers, user-configured RSS and remote servers, and
   any other data flows in the released version.
2. Use screenshots and descriptions from this edition. Do not advertise the
   removed tracker search, plugins, catalog, or installer updater.
3. Complete age rating, support/contact information, and rights/licensing checks.
   Keep the bundled third-party license notices and satisfy source-distribution
   obligations for the application's actual dependencies.
4. Explain `runFullTrust` in certification notes: this is a WinUI desktop torrent
   client using a bundled local torrent engine, download file access, and tray UI.
5. Run Windows App Certification Kit against a test installation and exercise
   first launch, restart, theme changes, torrent/magnet activation, local downloads,
   remote connections, RSS, tray behavior, startup consent, and notification clicks
   with the app both running and closed. Test clean Windows 10/11 machines too.

The build scripts do not publish a listing, change system default associations,
enable Windows startup, install certificates, or submit anything to Microsoft.

References:

- [Microsoft Store policies](https://learn.microsoft.com/en-us/windows/apps/publish/store-policies)
- [Upload app packages](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/upload-app-packages)
- [Packaged app notification setup](https://learn.microsoft.com/en-us/windows/apps/develop/notifications/app-notifications/app-notifications-dotnet#packaged-app-setup)
