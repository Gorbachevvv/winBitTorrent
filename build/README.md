# Build tooling

## Release builder — portable + installer in one action

`release.ps1` publishes a self-contained `win-x64` build and packages it into:

- `dist/WinBitTorrent-<version>-portable/` — unzip-and-run folder
- `dist/WinBitTorrent-<version>-installer/WinBitTorrent-<version>-setup.exe` — Inno Setup installer

### Fastest: double-click

Double-click **`release.cmd`** (works from Windows Explorer or from Solution
Explorer inside Visual Studio). It builds both artifacts and keeps the window
open so you can read the result. Arguments are forwarded, e.g. drag-drop is not
needed — just edit the defaults (below) for routine builds.

### From a terminal

```powershell
build\release.ps1                 # both artifacts, version from settings
build\release.ps1 -Version 1.1.0  # one-off version override
build\release.ps1 -Portable -Zip  # portable folder + a .zip of it
build\release.ps1 -Installer      # installer only
build\release.ps1 -SkipPublish    # repackage without recompiling .NET (fast)
```

### Default settings

**Version** is the single source of truth in `Directory.Build.props`
(`<Version>` at the repo root). Bump it there to publish a new release — the
running app reads it back to compare against the latest GitHub release, and
`release.ps1` reads it to name the portable folder and the installer. (`-Version`
still overrides it for a one-off build.)

Edit `release.settings.json` to change the routine build defaults:

| Key | Meaning |
|-----|---------|
| `configuration` | Build configuration (default `Release`) |
| `platform` | Target platform (default `x64`) |
| `createPortableZip` | Also produce a `.zip` of the portable folder |
| `openOutputFolder` | Open `dist/` in Explorer when the build finishes |

### Add it to the Visual Studio *Tools* menu (optional)

Visual Studio 18 stores External Tools in its own private hive, so it must be
added through the IDE (not the registry):

1. **Tools → External Tools… → Add**
2. Title: `Build WinBitTorrent (Portable + Installer)`
3. Command: `powershell.exe`
4. Arguments: `-NoExit -NoProfile -ExecutionPolicy Bypass -File "$(SolutionDir)build\release.ps1"`
5. Initial directory: `$(SolutionDir)`

### Prerequisites

- The native backend must be present at `Backend\qbittorrent-nox.exe`
  (build it with `build-backend.ps1` or extract the CI artifact).
- [Inno Setup 6](https://jrsoftware.org/isinfo.php) for the installer
  (`winget install JRSoftware.InnoSetup`). Not needed for portable-only builds.

## Other scripts

- `build-backend.ps1` — reproducible build of the bundled qBittorrent engine.
- `installer/WinBitTorrent.iss` — Inno Setup script (driven by `release.ps1`).
