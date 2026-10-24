# Optional qBittorrent oracle

This directory contains the former local qBittorrent process host and its
pinned Qt/qBittorrent build recipe. Integration tests use it to compare the
remote Web API contract with the local libtorrent engine. Production projects
do not reference or compile this code.

From the repository root, on Windows x64 with the Visual Studio C++ tools:

```powershell
./tests/compatibility/qbittorrent/build.ps1
dotnet test tests/WinBitTorrent.IntegrationTests --filter FullyQualifiedName~ManagedBackendTests
```

The default output is `artifacts/compatibility/qbittorrent/`; temporary native
sources and builds go to `build/.legacy-work/`. Alternatively set
`WINBITTORRENT_BACKEND_PATH` to an existing `qbittorrent-nox.exe` and its runtime
dependencies. Tests are skipped when the oracle is not present.

The application's remote qBittorrent adapter and legacy-data migration remain
in `src/`; neither requires this executable.
