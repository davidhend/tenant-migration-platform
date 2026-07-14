@echo off
REM Double-click this to start the M365 Migration Platform (postgres + api + web).
REM It just runs start.ps1. Requires Docker Desktop to be installed and running.
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0start.ps1" %*
echo.
pause
