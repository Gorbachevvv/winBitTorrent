# Repository layout

```text
WinBitTorrent.slnx           Visual Studio solution (x64 and ARM64)
Directory.Build.*           Shared version, architecture and build settings
src/
  WinBitTorrent/            WinUI application, XAML, assets and translations
  WinBitTorrent.Core/       Domain models, backend contracts and pure services
  WinBitTorrent.Infrastructure/
                            IPC client, remote qBittorrent API, storage and updates
  WinBitTorrent.EngineHost/ Local worker, SQLite state, RSS and search
native/WinBitTorrent.Native/ Native libtorrent bridge (CMake)
tests/                      Unit, EngineHost integration and UI tests
  compatibility/qbittorrent/ Optional legacy oracle and its separate build recipe
build/                      Release console, runtime builders, installer and Store tooling
docs/                       Documentation, screenshots and third-party notices
artifacts/                  Generated local files; ignored by Git
  runtime/Engine/            Staged x64 engine DLLs (ARM64 in arm64/)
  runtime/Backend/           Python, Nova plugins, GeoDB and dependency licenses
  compatibility/qbittorrent/ Optional qBittorrent test binaries
dist/                       Generated portable packages and installers; ignored by Git
```

Open `WinBitTorrent.slnx` from the repository root. The application project is
`src/WinBitTorrent/WinBitTorrent.csproj`; its existing namespaces, resource URIs
and assembly name are unchanged. Build output remains in each project's
`bin/` and `obj/` folders.

Old local output folders from before the reorganization were preserved under
`artifacts/previous-layout/`. New builds do not use them.

```powershell
dotnet build src/WinBitTorrent/WinBitTorrent.csproj -c Debug -p:Platform=x64
dotnet build src/WinBitTorrent/WinBitTorrent.csproj -c Debug -p:Platform=ARM64
dotnet test WinBitTorrent.slnx
```

Native dependencies must be prepared with `build/build-runtime.ps1` and
`build/build-engine.ps1` for the selected architecture. The Visual Studio
External Tools entry continues to call `build/release.cmd` or
`build/release.ps1`; its path and architecture menu have not changed.

The installed application keeps its existing payload layout (`EngineHost/`,
`Backend/Python/`, `Backend/SearchPlugins/`, `Licenses/`) and user data under
`%LOCALAPPDATA%/WinBitTorrent`. Installer identities and update artifact names
are unchanged.

Remote qBittorrent profiles, Nova search compatibility and migration of old
local profiles are supported features. Only the old process launcher and its
Qt build recipe belong to the optional test oracle. They are excluded from
the application and release packages. Builds run locally through `build/`;
no GitHub Actions workflow is required.
