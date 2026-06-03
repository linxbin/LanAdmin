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
DisableStartupPrompt=yes
DisableWelcomePage=yes
DisableDirPage=yes
DisableReadyPage=yes
DisableReadyMemo=yes
DisableFinishedPage=yes
UsePreviousAppDir=no
CloseApplications=yes
RestartApplications=no

[Files]
Source: "..\..\artifacts\agent\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion
Source: "..\..\artifacts\setup-worker\LanAdmin.SetupWorker.exe"; DestDir: "{app}\tools"; Flags: ignoreversion
Source: "..\..\artifacts\setup-worker\LanAdmin.SetupWorker.exe"; DestName: "LanAdmin.SetupWorker.bootstrap.exe"; Flags: dontcopy

[Run]
Filename: "{app}\tools\LanAdmin.SetupWorker.exe"; Parameters: "configure-agent --install-dir ""{app}"" --service-name ""{#AgentServiceName}"""; StatusMsg: "Configuring LanAgent and starting service..."; Flags: waituntilterminated

[UninstallRun]
Filename: "{app}\tools\LanAdmin.SetupWorker.exe"; Parameters: "remove-service --service-name ""{#AgentServiceName}"""; Flags: waituntilterminated; RunOnceId: "LanAgentUninstall"

[Code]
const
  BM_CLICK = $00F5;

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

function HasExistingAgentInstallation(): Boolean;
begin
  Result :=
    FileExists(GetInstalledWorkerPath()) or
    FileExists(ExpandConstant('{app}\LanAgent.exe'));
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

function PostMessage(hWnd: Integer; Msg: Integer; wParam: Integer; lParam: Integer): Boolean;
  external 'PostMessageW@user32.dll stdcall';

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = wpReady then
  begin
    PostMessage(WizardForm.NextButton.Handle, BM_CLICK, 0, 0);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  WorkerPath: string;
  ResultCode: Integer;
  ExecErrorCode: Integer;
begin
  Result := '';
  ExecErrorCode := 0;

  if not HasExistingAgentInstallation() then
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
      'Failed to stage LanAgent upgrade helper. ' +
      'Expected worker path: ' + GetBootstrapWorkerPath() + '. ' +
      'Check directory permissions for ' + GetBootstrapCacheDir() + '.';
    exit;
  end;

  if not Exec(
      WorkerPath,
      'prepare-agent-upgrade --install-dir "' + ExpandConstant('{app}') + '" --service-name "{#AgentServiceName}" --process-name "{#AgentServiceName}" --log-path "' + GetUpgradeLogPath() + '"',
      ExpandConstant('{app}'),
      SW_HIDE,
      ewWaitUntilTerminated,
      ResultCode) then
  begin
    ExecErrorCode := DLLGetLastError();
    Result :=
      'Failed to prepare LanAgent for upgrade. ' +
      'Windows error code: ' + IntToStr(ExecErrorCode) + '. ' +
      'Worker path: ' + WorkerPath + '. ' +
      'Check security software and review log if present: ' + GetUpgradeLogPath();
    exit;
  end;

  if ResultCode <> 0 then
  begin
    Result :=
      'LanAgent upgrade preparation exited with code ' + IntToStr(ResultCode) + '. ' +
      'Review log: ' + GetUpgradeLogPath();
  end;
end;
