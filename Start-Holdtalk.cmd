@echo off
if not exist "%~dp0dist\Holdtalk\Holdtalk.exe" (
  echo Run scripts\setup.ps1 first. See README.md.
  pause
  exit /b 1
)
start "" "%~dp0dist\Holdtalk\Holdtalk.exe"
