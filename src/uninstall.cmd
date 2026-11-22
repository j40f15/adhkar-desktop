@echo off
rem Removes "Adhkar" completely: scheduled tasks, desktop widget, right-click entries, notifications, files.
echo Removing Hisn...
powershell -NoProfile -ExecutionPolicy Bypass -Command "& (Join-Path $env:USERPROFILE 'Adhkar\adhkar.ps1') -Mode Uninstall; Start-Sleep -Seconds 1; Remove-Item (Join-Path $env:USERPROFILE 'Adhkar') -Recurse -Force -ErrorAction SilentlyContinue"
echo Done.
pause
