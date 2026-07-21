@echo off
rem =====================================================================
rem  WinBitTorrent one-click release builder.
rem
rem  Double-click this file (in Explorer or Solution Explorer) to build
rem  the portable folder AND the installer in one action. Any arguments
rem  are forwarded to release.ps1, e.g.:
rem      release.cmd -Portable -Zip
rem      release.cmd -Version 1.1.0
rem =====================================================================
setlocal
cd /d "%~dp0.."
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0release.ps1" %*
echo.
echo Press any key to close this window . . .
pause >nul
