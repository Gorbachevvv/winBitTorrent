# Build tooling

## Release builder — portable + installer in one action

Interactive launches offer a target menu: **1** builds x64 portable + installer,
**2** opens the existing Microsoft Store builder, **3** builds ARM64 portable +
installer, and **4** builds both x64 and ARM64. Enter selects x64.
The existing Visual Studio Tools command uses this menu automatically.
For a direct Store build, double-click `release-msix.cmd` or run
`build\release.ps1 -Msix`. See [Store build and submission notes](../docs/microsoft-store.md).
Set `chooseBuildTarget` to `false` in `release.settings.json` to keep the previous
no-prompt default; explicit target switches always bypass the menu.

`release.ps1` publishes a self-contained `win-x64` or `win-arm64` build. The first
build of an architecture prepares its missing native engine and Python assets automatically.
The existing x64 filenames and folders remain:

- `dist/WinBitTorrent-<version>-portable/` — unzip-and-run folder
- `dist/WinBitTorrent-<version>-installer/WinBitTorrent-<version>-setup.exe` — Inno Setup installer

ARM64 uses `WinBitTorrent-<version>-arm64-portable/` and
`WinBitTorrent-<version>-arm64-installer/WinBitTorrent-<version>-setup-arm64.exe`.

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
build\release.ps1 -Platform arm64 # ARM64 portable + installer
build\release.ps1 -Platform all   # both architectures, separate output folders
build\release.ps1 -Platform arm64 -Portable -Zip
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
| `platform` | `x64` (default), `arm64`, or `all`; overridden by `-Platform` |
| `chooseBuildTarget` | Show the target menu on interactive launches |
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

An existing External Tools entry using these arguments already receives the new menu.
Windows PowerShell 5.1 remains supported. For a dedicated ARM64 entry append
`-Platform arm64` to the arguments; this bypasses the menu.

### Prerequisites

- Build the pinned runtime with `build-runtime.ps1`, then the native libtorrent
  bridge with `build-engine.ps1`. The resulting `artifacts/runtime/Engine/WinBitTorrent.Native.dll`
  and `artifacts/runtime/Backend/Python/python.exe` are required by the release builder.
- For ARM64 cross-compilation on an x64 PC, install the MSVC C++ ARM64/ARM64EC
  build tools and Windows SDK in Visual Studio Installer. Native dependencies are
  built with the ARM64 toolchain; an ARM64 device is needed only to run the result.
  The first build can take substantially longer while vcpkg prepares dependencies.
- [Inno Setup 6.6 or newer](https://jrsoftware.org/isinfo.php) for the installer
  (`winget install JRSoftware.InnoSetup`). Not needed for portable-only builds.
  Version 6.6+ is required for the installer's automatic light/dark Windows theme.

## Architecture isolation and updates

```powershell
build\build-runtime.ps1 -Platform arm64
build\build-engine.ps1 -Platform arm64
dotnet build WinBitTorrent.slnx -p:Platform=ARM64
```

ARM64 assets live in `artifacts/runtime/Engine/arm64` and `artifacts/runtime/Backend/arm64`. Its native caches use
`build/.runtime-work/arm64` and `build/.engine-work/arm64`; x64 retains the existing
asset locations. Custom runtime caches can be passed as `-WorkRoot` to the runtime
builder and `-RuntimeWorkRoot` to the engine builder. Do not share caches between
architectures. Both published apps use the same layout (`EngineHost`, `Backend/Python`).
Packaging checks PE headers and rejects binaries for a different architecture.

Solution builds without an explicit platform stay on x64 through
`Directory.Solution.props`; Visual Studio also exposes ARM64. ARM64 builds can be
cross-compiled but cannot be executed on an x64 machine.

Publish both installers in the **same GitHub release**, with the same version:

- `WinBitTorrent-<version>-setup.exe` for x64.
- `WinBitTorrent-<version>-setup-arm64.exe` for ARM64.

The updater follows the running application's architecture. An x64 application
under ARM emulation continues receiving x64 updates; a native ARM64 application
receives only ARM64 installers. If the matching asset is missing, the app opens
the release page rather than installing another architecture. Previously shipped
x64 clients ignore the new ARM64 suffix and keep selecting the x64 installer.

## Script reference

- `verify-installer-updates.ps1` — reproduces the old silent-update mutex failure and verifies
  upgrades using an isolated fixture and the production Inno script. See
  [EXE auto-update details](../docs/auto-updates.md).
- `build-runtime.ps1` — builds/restores pinned libtorrent, Boost, OpenSSL,
  CPython and the Nova-compatible search runtime. It does not stage qBittorrent
  or Qt binaries.
- `build-engine.ps1` — builds `WinBitTorrent.Native.dll` and copies its runtime
  dependencies into `artifacts/runtime/Engine/`.
- `../tests/compatibility/qbittorrent/build.ps1` — development-only legacy qBittorrent oracle used for
  compatibility testing; its output is forbidden in public packages.
- `installer/WinBitTorrent.iss` — Inno Setup script (driven by `release.ps1`).
