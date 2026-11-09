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
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0
OutputDir=.
OutputBaseFilename=CleanTB-Setup
SetupIconFile=app.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "publish_sc\CleanTB.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "publish\ExplorerTAP.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "publish\ExplorerHooks.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "publish\config.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "publish\app.ico"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\app.ico"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\app.ico"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
