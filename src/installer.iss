; Gerbera Studio installer (Inno Setup 6)
#define AppName "Gerbera Studio"
#define AppVersion "1.1"
#define AppExe "GerberaStudio.exe"

[Setup]
AppId={{6E2B9D4A-7F1C-4C2E-9B7A-A1B2C3D4E5F6}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Gerbera Studio
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
SetupIconFile=app.ico
OutputDir=installer
OutputBaseFilename=GerberaStudio-Setup-{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
WizardStyle=modern dynamic
ChangesAssociations=yes
CloseApplications=yes

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"
Name: "associate"; Description: "Open Gerbera Studio projects (.mmrproj) with Gerbera Studio"; GroupDescription: "File types:"

[Files]
Source: "out\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "app.ico"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
Root: HKA; Subkey: "Software\Classes\.mmrproj"; ValueType: string; ValueName: ""; ValueData: "GerberaStudio.Project"; Flags: uninsdeletevalue; Tasks: associate
Root: HKA; Subkey: "Software\Classes\GerberaStudio.Project"; ValueType: string; ValueName: ""; ValueData: "Gerbera Studio project"; Flags: uninsdeletekey; Tasks: associate
Root: HKA; Subkey: "Software\Classes\GerberaStudio.Project\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\app.ico"; Tasks: associate
Root: HKA; Subkey: "Software\Classes\GerberaStudio.Project\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: associate

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[Code]
const
  UninstKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{6E2B9D4A-7F1C-4C2E-9B7A-A1B2C3D4E5F6}_is1';

function GetInstalledVersion(): String;
var
  v: String;
begin
  Result := '';
  if RegQueryStringValue(HKCU, UninstKey, 'DisplayVersion', v) then Result := v
  else if RegQueryStringValue(HKLM64, UninstKey, 'DisplayVersion', v) then Result := v
  else if RegQueryStringValue(HKLM32, UninstKey, 'DisplayVersion', v) then Result := v;
end;

function InitializeSetup(): Boolean;
var
  old: String;
begin
  Result := True;
  old := GetInstalledVersion();
  if old = '' then Exit;
  if old = '{#AppVersion}' then
    Result := MsgBox('Gerbera Studio {#AppVersion} is already installed on this PC.' + #13#10#13#10 +
                     'Do you want to reinstall it?', mbConfirmation, MB_YESNO) = IDYES
  else
    Result := MsgBox('Gerbera Studio ' + old + ' is installed on this PC.' + #13#10#13#10 +
                     'Would you like to update it to version {#AppVersion}?' + #13#10 +
                     'Your settings and projects will be kept.', mbConfirmation, MB_YESNO) = IDYES;
end;
