@echo off
setlocal
cd /d "%~dp0.."
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0release.ps1" -Msix %*
set "buildExit=%ERRORLEVEL%"
echo.
echo Press any key to close this window . . .
pause >nul
exit /b %buildExit%
