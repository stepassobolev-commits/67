#define MyAppName "Dynamic Island"
#define MyAppVersion "0.3.0"
#define MyAppPublisher "Dynamic Island"
#define MyAppExeName "DynamicIsland.exe"

[Setup]
AppId={{7C4A7A20-5F93-4F9A-9F4C-1C9A0A5F6D12}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\Dynamic Island
DefaultGroupName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
OutputDir=..\dist
OutputBaseFilename=DynamicIsland-Setup
MinVersion=10.0.19041
CloseApplications=yes
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64
PrivilegesRequired=lowest

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Tasks]
Name: "autostart"; Description: "Запускать вместе с Windows"; GroupDescription: "Дополнительные параметры:"
Name: "desktopicon"; Description: "Создать ярлык на рабочем столе"; GroupDescription: "Дополнительные параметры:"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Запустить Dynamic Island"; Flags: postinstall nowait skipifsilent

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "DynamicIsland"; ValueData: """{app}\{#MyAppExeName}"""; Flags: uninsdeletevalue; Tasks: autostart
