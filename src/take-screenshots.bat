@echo off
cd /d "%~dp0"
if not exist out\GerberaStudio.exe (
  echo Run build.bat first.
  pause
  exit /b 1
)
echo Taking screenshots... the app will open, flip through a few views, and close by itself. Please don't touch it for about a minute.
start /wait "" "out\GerberaStudio.exe" --shots "%~dp0..\screenshots" "%~dp0..\samples\demo.mmrproj"
echo Done! Screenshots are in Documents\MovieMakerReborn\screenshots
explorer "%~dp0..\screenshots"
pause
