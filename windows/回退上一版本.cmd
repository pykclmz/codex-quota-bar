@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" -InstallDirectory "%~dp0." -Rollback
if errorlevel 1 pause
