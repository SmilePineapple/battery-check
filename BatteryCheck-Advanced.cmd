@echo off
rem Advanced battery check: adds powercfg /sleepstudy analysis (asks for administrator rights).
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0BatteryCheck.ps1" -Advanced %*
echo.
pause
