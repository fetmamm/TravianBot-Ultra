#define MyAppName "Tbot Ultra"
#define MyAppPublisher "Tbot Ultra"
#define MyAppExeName "TbotUltra.Desktop.exe"

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

#ifndef SourceDir
  #define SourceDir "."
#endif

#ifndef OutputDir
  #define OutputDir "."
#endif

#ifndef OutputBaseFilename
  #define OutputBaseFilename "TbotUltra-Setup-win-x64"
#endif

#define MyAppVersion AppVersion
#define SourceRoot AddBackslash(SourceDir)

[Setup]
AppId={{8A4CC4B4-98D6-4674-A4EB-4DE9B5E3C4A7}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Tbot Ultra
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename={#OutputBaseFilename}
Compression=lzma
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupIconFile={#SourceRoot}Assets\icon_windows.ico

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#SourceRoot}*"; DestDir: "{app}"; Excludes: ".env,config\bot.json,config\queue.json,config\servers.user.json"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SourceRoot}.env"; DestDir: "{app}"; DestName: ".env"; Flags: ignoreversion onlyifdoesntexist
Source: "{#SourceRoot}config\bot.json"; DestDir: "{app}\config"; DestName: "bot.json"; Flags: ignoreversion onlyifdoesntexist
Source: "{#SourceRoot}config\queue.json"; DestDir: "{app}\config"; DestName: "queue.json"; Flags: ignoreversion onlyifdoesntexist
Source: "{#SourceRoot}config\servers.user.json"; DestDir: "{app}\config"; DestName: "servers.user.json"; Flags: ignoreversion onlyifdoesntexist

[Dirs]
Name: "{app}\logs"
Name: "{app}\playwright\.auth"
Name: "{app}\config\account-analysis"
Name: "{app}\config\cache"
Name: "{app}\config\cache\natar-farms"

[Icons]
Name: "{group}\Tbot Ultra"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\Tbot Ultra"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch Tbot Ultra"; Flags: nowait postinstall skipifsilent
