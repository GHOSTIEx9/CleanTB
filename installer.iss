#define MyAppName "CleanTB"
#define MyAppVersion "1.0.1"
#define MyAppPublisher "CleanTB"
#define MyAppURL "https://github.com/GHOSTIEx9/CleanTB"
#define MyAppExeName "CleanTB.exe"

[Setup]
AppId={{D37F7E84-48E2-4C67-B9C7-8A7B4422D310}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
; Install directly to user programs folder - zero admin/UAC prompts required
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
DisableDirPage=yes
DisableReadyPage=yes
UsePreviousAppDir=no
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=commandline
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
CloseApplications=force
RestartApplications=no
OutputDir=.
OutputBaseFilename=CleanTB-Setup
SetupIconFile=app.ico
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\app.ico
VersionInfoVersion={#MyAppVersion}.0
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} Setup
VersionInfoCopyright=Copyright (C) 2026 {#MyAppPublisher}
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}.0
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "publish_sc\CleanTB.exe"; DestDir: "{app}"; Flags: ignoreversion restartreplace
Source: "ExplorerTAP.dll"; DestDir: "{app}"; Flags: ignoreversion restartreplace
Source: "ExplorerHooks.dll"; DestDir: "{app}"; Flags: ignoreversion restartreplace
Source: "config.json"; DestDir: "{app}"; Flags: ignoreversion restartreplace
Source: "app.ico"; DestDir: "{app}"; Flags: ignoreversion restartreplace

[InstallDelete]
; Delete any legacy or duplicate uninstallers and logs
Type: files; Name: "{app}\unins*.exe"
Type: files; Name: "{app}\unins*.dat"
Type: files; Name: "{app}\uninstaller.exe"
Type: files; Name: "{app}\crash.log"
Type: files; Name: "{app}\stderr.log"
Type: files; Name: "{app}\stdout.log"

[UninstallDelete]
Type: files; Name: "{app}\crash.log"
Type: files; Name: "{app}\config.json"
Type: filesandordirs; Name: "{app}"

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\app.ico"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"; IconFilename: "{app}\app.ico"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\app.ico"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{cmd}"; Parameters: "/C reg delete HKCU\Software\Microsoft\Windows\CurrentVersion\Run /v CleanTB /f"; Flags: runhidden

[Code]
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  // Terminate any running CleanTB instance so files can be updated cleanly
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM CleanTB.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(500);
  Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
  begin
    // Double ensure all previous uninstaller and log files are deleted before new install starts
    DeleteFile(ExpandConstant('{app}\unins000.exe'));
    DeleteFile(ExpandConstant('{app}\unins000.dat'));
    DeleteFile(ExpandConstant('{app}\unins001.exe'));
    DeleteFile(ExpandConstant('{app}\unins001.dat'));
    DeleteFile(ExpandConstant('{app}\uninstaller.exe'));
    DeleteFile(ExpandConstant('{app}\crash.log'));
    DeleteFile(ExpandConstant('{app}\stderr.log'));
    DeleteFile(ExpandConstant('{app}\stdout.log'));
  end;
  if CurStep = ssPostInstall then
  begin
    // If CleanTB is already configured to start on Windows boot, update the path to the newly installed app
    if RegValueExists(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'CleanTB') then
    begin
      RegWriteStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'CleanTB', '"' + ExpandConstant('{app}\CleanTB.exe') + '"');
    end;
  end;
end;
