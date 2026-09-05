# First-run setup

The main window shows `FirstRunDialog` until `onboarding.completed` is true in
`client-settings.json`. Theme, download folder, notification preference, explicit
startup choice and current page are saved in `onboarding.draft` as the user edits
them. Closing the app or choosing **Set up later** does not complete setup. The
wizard can also be opened from **Tools → First-run setup**.

Theme changes are previewed immediately and restored on dismissal. Finishing
verifies folder write access for a local backend, applies and reads back the
backend save path, applies an explicitly changed startup preference, and only
then atomically commits local preferences and the completion flag. External
settings already applied before an error remain applied; setup stays open for
correction or retry. Torrent file/link activations received during setup are
queued until the dialog closes. Automatic update prompts wait for completion.

## Windows integration

Packaged builds declare `WinBitTorrentStartup` in `Package.appxmanifest` with
startup disabled by default and use `Windows.ApplicationModel.StartupTask` to
change it. Windows or administrator refusal is shown in the wizard. See
[Microsoft's desktop startup task documentation](https://learn.microsoft.com/en-us/uwp/schemas/appxpackage/uapmanifestschema/element-desktop-startuptask).

Unpackaged builds use the current user's Run registry entry and recognize the
existing Inno Setup startup shortcut, avoiding a second launch entry. File and
magnet associations use the existing package declarations or unpackaged
registration; the user chooses default handlers in Windows Settings.

## Validation

```powershell
dotnet build WinBitTorrent.csproj -p:Platform=x64
dotnet test tests/WinBitTorrent.Infrastructure.Tests/WinBitTorrent.Infrastructure.Tests.csproj
$env:WINBITTORRENT_UI_TESTS = '1'
dotnet test tests/WinBitTorrent.UiTests/WinBitTorrent.UiTests.csproj --filter FullyQualifiedName~FirstRunTests
```

Close other WinBitTorrent instances before the interactive tests. These tests
launch with unique `WINBITTORRENT_DATA_ROOT` directories and do not change the
user's startup registrations or default apps. They cover interrupted setup,
dismissal, invalid folders, persistence, completion, light/dark themes, Russian
localization, a small window and Escape. Set `WINBITTORRENT_SETUP_CAPTURES` to an
output directory to save screenshots.

Before Store release, install the signed MSIX and verify startup enable/disable,
Windows-disabled startup, and .torrent/magnet activation from Windows. These
package-specific checks cannot be covered by the unpackaged UI tests.
