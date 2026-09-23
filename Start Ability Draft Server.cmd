@echo off
setlocal
cd /d "%~dp0"
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0Integration\Start-Local.ps1"
if errorlevel 1 (
    echo.
    echo Startup failed. Read the error above. Logs are in Integration\local.
) else (
    echo.
    echo Local services started. Open Deadlock and choose an Ability Draft mode.
    echo The website and game server run in the background; this window may be closed.
)
echo.
pause
