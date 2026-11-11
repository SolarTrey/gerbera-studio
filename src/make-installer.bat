@echo off
cd /d "%~dp0"
set ISCC=
for %%V in (7 6) do (
  if exist "%ProgramFiles(x86)%\Inno Setup %%V\ISCC.exe" if not defined ISCC set "ISCC=%ProgramFiles(x86)%\Inno Setup %%V\ISCC.exe"
  if exist "%ProgramFiles%\Inno Setup %%V\ISCC.exe" if not defined ISCC set "ISCC=%ProgramFiles%\Inno Setup %%V\ISCC.exe"
  if exist "%LocalAppData%\Programs\Inno Setup %%V\ISCC.exe" if not defined ISCC set "ISCC=%LocalAppData%\Programs\Inno Setup %%V\ISCC.exe"
)
if not defined ISCC for /f "delims=" %%P in ('where iscc 2^>nul') do if not defined ISCC set "ISCC=%%P"
if not defined ISCC (
  echo Inno Setup was not found. Get it free from https://jrsoftware.org/isdl.php then run this again.
  pause
  exit /b 1
)
if not exist out\GerberaStudio.exe (
  echo Run build.bat first.
  pause
  exit /b 1
)
echo Creating installer with "%ISCC%"...
"%ISCC%" installer.iss > installer-log.txt 2>&1
if errorlevel 1 (
  echo INSTALLER FAILED - see installer-log.txt
) else (
  echo Done! Your installer is in the "installer" folder.
  explorer installer
)
pause
