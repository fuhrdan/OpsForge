@echo off
setlocal
cd /d "%~dp0"
echo.
echo ============================================================
echo  OPSFORGE v0.7.2 - RELIABILITY COMMAND CENTER - FULL BUILD
echo ============================================================
echo.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Start-OpsForge.ps1"
if errorlevel 1 (
    echo.
    echo OpsForge did not start successfully.
    pause
)
endlocal
