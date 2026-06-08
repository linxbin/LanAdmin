param(
    [string]$Configuration = "Release",
    [string]$ServerListenUrl,
    [string]$ServerBaseUrl,
    [string]$DatabasePath,
    [int]$OfflineThresholdSeconds = 0,
    [string]$ServerRuntimeIdentifier = "win-x64",
    [string]$AgentRuntimeIdentifier = "win-x64",
    [string]$ConsoleRuntimeIdentifier = "win-x64",
    [string]$SetupWorkerRuntimeIdentifier = "win-x64"
)

$ErrorActionPreference = "Stop"
$commonScript = Join-Path $PSScriptRoot "Common.ps1"
. $commonScript

$iscc = Get-InnoSetupCompiler

$buildAgentInstallerScript = Join-Path $PSScriptRoot "build-inno-agent.ps1"
& $buildAgentInstallerScript `
    -Configuration $Configuration `
    -RuntimeIdentifier $AgentRuntimeIdentifier `
    -SetupWorkerRuntimeIdentifier $SetupWorkerRuntimeIdentifier

$publishServerScript = Join-Path $PSScriptRoot "publish-server.ps1"
& $publishServerScript `
    -Configuration $Configuration `
    -ListenUrl $ServerListenUrl `
    -ServerBaseUrl $ServerBaseUrl `
    -DatabasePath $DatabasePath `
    -OfflineThresholdSeconds $OfflineThresholdSeconds `
    -RuntimeIdentifier $ServerRuntimeIdentifier `
    -SelfContained

$publishConsoleScript = Join-Path $PSScriptRoot "publish-console.ps1"
& $publishConsoleScript `
    -Configuration $Configuration `
    -ServerBaseUrl $ServerBaseUrl `
    -RuntimeIdentifier $ConsoleRuntimeIdentifier `
    -SelfContained

$publishSetupWorkerScript = Join-Path $PSScriptRoot "publish-setup-worker.ps1"
& $publishSetupWorkerScript `
    -Configuration $Configuration `
    -RuntimeIdentifier $SetupWorkerRuntimeIdentifier

$issPath = Join-Path (Get-ProjectRoot) "installer\inno\LanAdminServer.iss"
& $iscc $issPath
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup compilation failed for LanAdminServer.iss"
}
