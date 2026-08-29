#define MyAppName "HeatTurbo"
#define MyAppVersion "0.4.0"
#define MyAppPublisher "HeatTurbo"
#define MyAppExeName "HeatTurbo.exe"

[Setup]
AppId={{C0467732-215D-4890-B766-A21567EBB6E1}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\HeatTurbo
DefaultGroupName=HeatTurbo
DisableProgramGroupPage=yes
OutputDir=..\release\installer
OutputBaseFilename=HeatTurbo-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
SetupLogging=yes
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupIconFile=..\Assets\HeatTurbo.ico

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Files]
Source: "..\release\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\HeatTurbo"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\HeatTurbo"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Criar atalho na área de trabalho"; GroupDescription: "Atalhos adicionais:"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Abrir HeatTurbo"; Verb: "runas"; Flags: shellexec nowait postinstall skipifsilent
