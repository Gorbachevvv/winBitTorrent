# Build tooling

## Release builder — portable + installer in one action

`release.ps1` publishes a self-contained `win-x64` or `win-arm64` build and packages it into:

- `dist/WinBitTorrent-<version>-<architecture>-portable/` — unzip-and-run folder
- `dist/WinBitTorrent-<version>-x64-installer/WinBitTorrent-<version>-setup.exe` — x64 Inno Setup installer
- `dist/WinBitTorrent-<version>-arm64-installer/WinBitTorrent-<version>-setup-arm64.exe` — ARM64 Inno Setup installer

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
| `platform` | Target platform: `x64` (default) or `arm64`; override with `-Platform` |
| `createPortableZip` | Also produce a `.zip` of the portable folder |
| `openOutputFolder` | Open `dist/` in Explorer when the build finishes |

### Add it to the Visual Studio *Tools* menu (optional)

Visual Studio 18 stores External Tools in its own private hive, so it must be
added through the IDE (not the registry):

1. **Tools → External Tools… → Add**
2. Title: `Build WinBitTorrent (Portable + Installer)`
3. Command: `pwsh.exe` (PowerShell 7)
4. Arguments: `-NoExit -NoProfile -ExecutionPolicy Bypass -File "$(SolutionDir)build\release.ps1"`
5. Initial directory: `$(SolutionDir)`

### Prerequisites

- PowerShell 7, a .NET SDK that supports the solution, and Visual Studio 2022 or
  newer with the C++ compiler and Windows SDK for the target architecture.
- Build the pinned runtime with `build-runtime.ps1`, then the native libtorrent
  bridge with `build-engine.ps1`. The resulting `Engine\WinBitTorrent.Native.dll`
  and `Backend\Python\python.exe` are required by the release builder.
- [Inno Setup 6.6 or newer](https://jrsoftware.org/isinfo.php) for the installer
  (`winget install JRSoftware.InnoSetup`). Not needed for portable-only builds.
  Version 6.6+ is required for the installer's automatic light/dark Windows theme.

### Native ARM64

From PowerShell 7:

```powershell
.\build\build-runtime.ps1 -Platform arm64
.\build\build-engine.ps1 -Platform arm64
dotnet build WinBitTorrent.csproj -c Debug -p:Platform=ARM64
.\build\release.ps1 -Platform arm64 -Portable -Zip
# Omit -Portable to also build the ARM64 installer.
```

The C++ build uses MSVC `ARM64` and vcpkg `arm64-windows`; CPython uses the
official ARM64 embeddable archive with a pinned SHA-256. ARM64 caches live under
`build/.runtime-work/arm64` and `build/.engine-work/arm64`, with staged assets in
`Backend/arm64` and `Engine/arm64`. The existing x64 asset locations stay unchanged;
x64 build caches now live under `build/.runtime-work/x64` and `build/.engine-work/x64`.
The packaged layout is identical on both architectures (`Backend/Python` and
`EngineHost`), and packaging checks the PE architecture of executable runtime
assets. The updater selects the installer matching the running app's architecture.
ARM64 installers end in `-setup-arm64.exe` so older x64 clients, which select
the first `setup.exe` release asset, cannot mistakenly download an ARM64 installer.
The bridge also stages the matching MSVC C++ runtime beside EngineHost.
OpenSSL's ARM64 assembly uses clang; the runtime builder exposes an installed
LLVM to vcpkg when available, without changing PATH. Use `-VcpkgRoot` on both
native scripts to select your vcpkg checkout and executable.

All three scripts default to x64. If supplying custom native cache paths, pass
the runtime builder's `-WorkRoot` as the engine builder's `-RuntimeWorkRoot`.
Use separate cache and output folders for each architecture.

## Other scripts

- `build-runtime.ps1` — builds/restores pinned libtorrent, Boost, OpenSSL,
  CPython and the Nova-compatible search runtime. It does not stage qBittorrent
  or Qt binaries.
- `build-engine.ps1` — builds `WinBitTorrent.Native.dll` and copies its runtime
  dependencies into `Engine\`.
- `build-backend.ps1` — development-only legacy qBittorrent oracle used for
  compatibility testing; its output is forbidden in public packages.
- `installer/WinBitTorrent.iss` — Inno Setup script (driven by `release.ps1`).
