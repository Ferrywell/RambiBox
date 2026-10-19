@echo off
rem Builds a single self-contained RambiBox.exe into .\dist (no .NET install needed on the target PC).
setlocal
set DOTNET_CLI_TELEMETRY_OPTOUT=1
set DOTNET_NOLOGO=1
cd /d "%~dp0"
dotnet publish src\RambiBox.App\RambiBox.App.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true ^
  -p:PublishReadyToRun=true -p:DebugType=none -o dist
if errorlevel 1 exit /b 1
echo.
echo Built: %~dp0dist\RambiBox.exe
