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
  TemporaryWorkerPath: string;
  ResultCode: Integer;
begin
  Result := '';
  ExtractTemporaryFile('LanAdmin.SetupWorker.bootstrap.exe');
  TemporaryWorkerPath := ExpandConstant('{tmp}\LanAdmin.SetupWorker.bootstrap.exe');

  if not Exec(
      TemporaryWorkerPath,
      'prepare-agent-upgrade --install-dir "' + ExpandConstant('{app}') + '" --service-name "{#AgentServiceName}" --process-name "{#AgentServiceName}"',
      ExpandConstant('{app}'),
      SW_HIDE,
      ewWaitUntilTerminated,
      ResultCode) then
  begin
    Result := 'Failed to prepare LanAgent for upgrade.';
    exit;
  end;

  if ResultCode <> 0 then
  begin
    Result := 'LanAgent upgrade preparation exited with code ' + IntToStr(ResultCode) + '.';
  end;
end;
