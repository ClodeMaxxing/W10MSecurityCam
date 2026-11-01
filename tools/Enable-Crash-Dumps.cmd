@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Crash-Dumps.ps1" -Enable -List
pause
