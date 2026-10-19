@echo off
rem Installs RambiBox for the current user (no admin needed):
rem  - copies dist\RambiBox.exe to %LocalAppData%\Programs\RambiBox
rem  - adds a Start menu shortcut
rem  - starts it (RambiBox itself manages "Start with Windows")
setlocal
cd /d "%~dp0"
if not exist dist\RambiBox.exe call publish.cmd || exit /b 1
set TARGET=%LocalAppData%\Programs\RambiBox
taskkill /im RambiBox.exe /f >nul 2>&1
if not exist "%TARGET%" mkdir "%TARGET%"
copy /y dist\RambiBox.exe "%TARGET%\RambiBox.exe" >nul || exit /b 1
powershell -NoProfile -Command "$s=(New-Object -ComObject WScript.Shell).CreateShortcut([Environment]::GetFolderPath('Programs')+'\RambiBox.lnk');$s.TargetPath='%TARGET%\RambiBox.exe';$s.WorkingDirectory='%TARGET%';$s.Description='RambiBox ambilight';$s.Save()"
start "" "%TARGET%\RambiBox.exe"
echo Installed to %TARGET%
