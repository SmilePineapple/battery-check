@echo off
rem Basic battery check (no admin needed): wear, cycles, measured consumption, verdict.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0BatteryCheck.ps1" %*
echo.
pause
