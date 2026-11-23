@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install.ps1" -HerdrHome "%USERPROFILE%\.config\herdr-app"
pause
