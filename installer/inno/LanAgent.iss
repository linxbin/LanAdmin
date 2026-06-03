#define MyAppName "LanAdmin Agent"
#define MyAppVersion "0.1.0"
#define MyAppPublisher "LanAdmin"
#define AgentServiceName "LanAgent"

[Setup]
AppId={{A46A3B18-59C7-4E24-A3E5-0A7531E15F22}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\LanAdmin\Agent
DefaultGroupName=LanAdmin
DisableProgramGroupPage=yes
OutputDir=..\..\artifacts\installer
OutputBaseFilename=LanAgentSetup
Compression=lzma
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible
DisableDirPage=yes
DisableReadyMemo=yes
UsePreviousAppDir=no

[Files]
Source: "..\..\artifacts\agent\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion
Source: "..\..\artifacts\setup-worker\LanAdmin.SetupWorker.exe"; DestDir: "{app}\tools"; Flags: ignoreversion

[Run]
Filename: "{app}\tools\LanAdmin.SetupWorker.exe"; Parameters: "configure-agent --install-dir ""{app}"" --service-name ""{#AgentServiceName}"""; StatusMsg: "Configuring LanAgent and starting service..."; Flags: waituntilterminated

[UninstallRun]
Filename: "{app}\tools\LanAdmin.SetupWorker.exe"; Parameters: "remove-service --service-name ""{#AgentServiceName}"""; Flags: waituntilterminated; RunOnceId: "LanAgentUninstall"
