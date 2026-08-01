@echo off
setlocal
cd /d "%~dp0"

set "INNO_COMPILER=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if exist "%INNO_COMPILER%" goto build
set "INNO_COMPILER=%LocalAppData%\Programs\Inno Setup 6\ISCC.exe"
if exist "%INNO_COMPILER%" goto build

where ISCC.exe >nul 2>nul
if not errorlevel 1 goto build

echo Inno Setup 6 is required to create the installer.
choice /C YN /N /M "Install Inno Setup 6 with winget now? [Y/N] "
if errorlevel 2 exit /b 1
winget install --id JRSoftware.InnoSetup --exact --accept-source-agreements --accept-package-agreements
if errorlevel 1 exit /b %errorlevel%

:build
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Build-WindowsInstaller.ps1" %*
exit /b %errorlevel%
