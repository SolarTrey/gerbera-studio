@echo off
cd /d "%~dp0"
taskkill /im GerberaStudio.exe /f >nul 2>&1
timeout /t 2 /nobreak >nul
del /q out\*.old >nul 2>&1
if exist out\GerberaStudio.exe ren out\GerberaStudio.exe GerberaStudio.%RANDOM%.old >nul 2>&1
echo Building Gerbera Studio...
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o out > build-log.txt 2>&1
if errorlevel 1 (
  echo BUILD FAILED - see build-log.txt
) else (
  echo Done! The app is in the "out" folder: out\GerberaStudio.exe
)
pause
