#define MyAppName "LanAdmin Server"
#define MyAppVersion "0.1.0"
#define MyAppPublisher "LanAdmin"
#define MyAppExeName "LanAdmin.Console.exe"
#define ServerServiceName "LanAdminServer"

[Setup]
AppId={{F51000B5-9E7B-4AA0-86FB-3799C5C7D50A}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\LanAdmin
DefaultGroupName=LanAdmin
DisableProgramGroupPage=yes
OutputDir=..\..\artifacts\installer
OutputBaseFilename=LanAdminServerSetup
Compression=lzma
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\console\{#MyAppExeName}

[Files]
Source: "..\..\artifacts\server\*"; DestDir: "{app}\server"; Flags: recursesubdirs ignoreversion
Source: "..\..\artifacts\console\*"; DestDir: "{app}\console"; Flags: recursesubdirs ignoreversion
Source: "..\..\artifacts\installer\LanAgentSetup.exe"; DestDir: "{app}\agent-package"; Flags: ignoreversion
Source: "..\..\artifacts\setup-worker\LanAdmin.SetupWorker.exe"; DestDir: "{app}\tools"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\LanAdmin Console"; Filename: "{app}\console\{#MyAppExeName}"
Name: "{autodesktop}\LanAdmin Console"; Filename: "{app}\console\{#MyAppExeName}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional icons:"

[Run]
Filename: "{app}\tools\LanAdmin.SetupWorker.exe"; Parameters: "configure-server --install-dir ""{app}"" --listen-url ""{code:GetListenUrl}"" --console-server-base-url ""{code:GetConsoleServerBaseUrl}"" --database-path ""{code:GetDatabasePath}"" --offline-threshold-seconds {code:GetOfflineThreshold} --service-name ""{#ServerServiceName}"""; StatusMsg: "Configuring LanAdmin Server and starting service..."; Flags: waituntilterminated

[UninstallRun]
Filename: "{app}\tools\LanAdmin.SetupWorker.exe"; Parameters: "remove-service --service-name ""{#ServerServiceName}"""; Flags: waituntilterminated; RunOnceId: "LanAdminServerUninstall"

[Code]
var
  ListenUrlPage: TInputQueryWizardPage;
  ConsoleUrlPage: TInputQueryWizardPage;
  DatabasePage: TInputQueryWizardPage;
  OfflineThresholdPage: TInputQueryWizardPage;

procedure InitializeWizard;
begin
  ListenUrlPage := CreateInputQueryPage(wpSelectDir,
    'Server Listen URL',
    'Configure the server HTTP endpoint.',
    'This URL will be written into server\appsettings.json.');
  ListenUrlPage.Add('Server listen URL:', False);
  ListenUrlPage.Values[0] := 'http://0.0.0.0:5000';

  ConsoleUrlPage := CreateInputQueryPage(ListenUrlPage.ID,
    'Console Server Address',
    'Configure the API address used by the bundled console and agent bootstrap flow.',
    'This URL will be written into console\appsettings.json and server\appsettings.json. Agents use it during runtime bootstrap after installation.');
  ConsoleUrlPage.Add('Console server base URL:', False);
  ConsoleUrlPage.Values[0] := 'http://127.0.0.1:5000';

  DatabasePage := CreateInputQueryPage(ConsoleUrlPage.ID,
    'Database Settings',
    'Configure the SQLite database location.',
    'The value can be relative to the server directory or an absolute path.');
  DatabasePage.Add('Database path:', False);
  DatabasePage.Values[0] := 'data/lanadmin.db';

  OfflineThresholdPage := CreateInputQueryPage(DatabasePage.ID,
    'Offline Threshold',
    'Configure the offline timeout threshold.',
    'The value is expressed in seconds.');
  OfflineThresholdPage.Add('Offline threshold (seconds):', False);
  OfflineThresholdPage.Values[0] := '90';
end;

function GetListenUrl(Param: string): string;
begin
  Result := ListenUrlPage.Values[0];
end;

function GetConsoleServerBaseUrl(Param: string): string;
begin
  Result := ConsoleUrlPage.Values[0];
end;

function GetDatabasePath(Param: string): string;
begin
  Result := DatabasePage.Values[0];
end;

function GetOfflineThreshold(Param: string): string;
begin
  Result := OfflineThresholdPage.Values[0];
end;
