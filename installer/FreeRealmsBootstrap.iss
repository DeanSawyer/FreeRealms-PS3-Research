#define MyAppName "Free Realms PS3 Bootstrap Manager"
#define MyAppVersion "0.3.0"
#define MyAppExeName "FreeRealms.Bootstrap.exe"
[Setup]
AppId={{B2EF195C-88CB-4C27-8E84-391DE652C450}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
DefaultDirName={autopf}\FreeRealmsPS3Bootstrap
DefaultGroupName={#MyAppName}
OutputDir=..\dist
OutputBaseFilename=FreeRealmsPS3Bootstrap-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}
[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"
[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch bootstrap manager"; Flags: nowait postinstall skipifsilent
