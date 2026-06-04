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
CloseApplications=yes
RestartApplications=no

[Files]
Source: "..\..\artifacts\server\*"; DestDir: "{app}\server"; Flags: recursesubdirs ignoreversion; Excludes: "data\lanadmin.db,logs\*"
Source: "..\..\artifacts\console\*"; DestDir: "{app}\console"; Flags: recursesubdirs ignoreversion
Source: "..\..\artifacts\setup-worker\LanAdmin.SetupWorker.exe"; DestDir: "{app}\tools"; Flags: ignoreversion
Source: "..\..\artifacts\setup-worker\LanAdmin.SetupWorker.exe"; DestName: "LanAdmin.SetupWorker.bootstrap.exe"; Flags: dontcopy

[Dirs]
Name: "{app}\server\data"
Name: "{app}\server\logs"

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

function GetUpgradeLogPath(): string;
begin
  Result := ExpandConstant('{commonappdata}\LanAdmin\Logs\SetupWorker-upgrade.log');
end;

function GetInstalledWorkerPath(): string;
begin
  Result := ExpandConstant('{app}\tools\LanAdmin.SetupWorker.exe');
end;

function GetBootstrapCacheDir(): string;
begin
  Result := ExpandConstant('{commonappdata}\LanAdmin\InstallerCache');
end;

function GetBootstrapWorkerPath(): string;
begin
  Result := GetBootstrapCacheDir() + '\LanAdmin.SetupWorker.bootstrap.exe';
end;

function HasExistingServerInstallation(): Boolean;
begin
  Result :=
    FileExists(GetInstalledWorkerPath()) or
    FileExists(ExpandConstant('{app}\server\LanAdmin.Server.exe')) or
    FileExists(ExpandConstant('{app}\console\LanAdmin.Console.exe'));
end;

function StageBootstrapWorker(var WorkerPath: string): Boolean;
var
  TemporaryWorkerPath: string;
begin
  Result := False;
  ExtractTemporaryFile('LanAdmin.SetupWorker.bootstrap.exe');
  TemporaryWorkerPath := ExpandConstant('{tmp}\LanAdmin.SetupWorker.bootstrap.exe');
  WorkerPath := GetBootstrapWorkerPath();

  if not ForceDirectories(GetBootstrapCacheDir()) then
  begin
    exit;
  end;

  if FileExists(WorkerPath) and not DeleteFile(WorkerPath) then
  begin
    exit;
  end;

  Result := CopyFile(TemporaryWorkerPath, WorkerPath, False);
end;

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
    'Configure the API address used by the bundled console and separately packaged agents.',
    'This URL will be written into console\appsettings.json and server\appsettings.json. Configure packaged agents to use the same address.');
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

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  WorkerPath: string;
  ResultCode: Integer;
  ExecErrorCode: Integer;
begin
  Result := '';
  ExecErrorCode := 0;

  if not HasExistingServerInstallation() then
  begin
    exit;
  end;

  if FileExists(GetInstalledWorkerPath()) then
  begin
    WorkerPath := GetInstalledWorkerPath();
  end
  else if not StageBootstrapWorker(WorkerPath) then
  begin
    Result :=
      'Failed to stage LanAdmin Server upgrade helper. ' +
      'Expected worker path: ' + GetBootstrapWorkerPath() + '. ' +
      'Check directory permissions for ' + GetBootstrapCacheDir() + '.';
    exit;
  end;

  if not Exec(
      WorkerPath,
      'prepare-server-upgrade --install-dir "' + ExpandConstant('{app}') + '" --service-name "{#ServerServiceName}" --log-path "' + GetUpgradeLogPath() + '"',
      ExpandConstant('{app}'),
      SW_HIDE,
      ewWaitUntilTerminated,
      ResultCode) then
  begin
    ExecErrorCode := DLLGetLastError();
    Result :=
      'Failed to prepare LanAdmin Server for upgrade. ' +
      'Windows error code: ' + IntToStr(ExecErrorCode) + '. ' +
      'Worker path: ' + WorkerPath + '. ' +
      'Check security software and review log if present: ' + GetUpgradeLogPath();
    exit;
  end;

  if ResultCode <> 0 then
  begin
    Result :=
      'LanAdmin Server upgrade preparation exited with code ' + IntToStr(ResultCode) + '. ' +
      'Review log: ' + GetUpgradeLogPath();
  end;
end;
