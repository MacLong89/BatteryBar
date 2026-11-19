; BatteryBar installer — build with Inno Setup 6:  ISCC.exe installer\BatteryBar.iss
#define AppName "BatteryBar"
#define AppVersion "1.0.0"
#define AppExe "BatteryBar.exe"
#define BuildDir "..\BatteryBar\bin\Release\net48"

[Setup]
AppId={{6B0F3C1E-5A2D-4C8B-9E71-2F4A8D3B7C10}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=BatteryBar contributors
AppPublisherURL=https://github.com/MacLong89/BatteryBar
AppSupportURL=https://github.com/MacLong89/BatteryBar/issues
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=yes
; Per-user install: no admin prompt.
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=BatteryBar-Setup-{#AppVersion}
SetupIconFile=..\BatteryBar\BatteryBar.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
LicenseFile=..\LICENSE
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
MinVersion=10.0
ArchitecturesAllowed=x64compatible arm64

[Tasks]
Name: "startup"; Description: "Start {#AppName} when I sign in to Windows"; GroupDescription: "Options:"
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Options:"; Flags: unchecked

[Files]
Source: "{#BuildDir}\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#BuildDir}\{#AppExe}.config"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"
Source: "..\THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{userdesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#AppName}"; ValueData: """{app}\{#AppExe}"""; Tasks: startup; Flags: uninsdeletevalue
; Also clean up the value if the user enabled startup from the app's own menu.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "{#AppName}"; Flags: dontcreatekey uninsdeletevalue
Root: HKCU; Subkey: "Software\{#AppName}"; Flags: uninsdeletekey

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName} now"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\{#AppName}"

[Code]
procedure StopRunningApp();
var Code: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

function HasNetFx48(): Boolean;
var Release: Cardinal;
begin
  Result := RegQueryDWordValue(HKLM, 'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full', 'Release', Release) and (Release >= 528040);
end;

function InitializeSetup(): Boolean;
begin
  Result := True;
  if not HasNetFx48() then
  begin
    MsgBox('{#AppName} needs .NET Framework 4.8, which is included with Windows 10 (May 2019 update or later) and Windows 11.' + #13#10 +
           'Please run Windows Update and try again.', mbError, MB_OK);
    Result := False;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopRunningApp();
  Result := '';
end;

function InitializeUninstall(): Boolean;
begin
  StopRunningApp();
  Result := True;
end;
