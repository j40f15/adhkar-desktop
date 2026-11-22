@echo off
rem Installs "Adhkar" for this Windows user (no admin rights needed).
powershell -NoProfile -ExecutionPolicy Bypass -STA -WindowStyle Hidden -File "%~dp0install.ps1"
