param(
    [string]$Configuration = "Release",
    [string]$ServerListenUrl,
    [string]$ServerBaseUrl,
    [string]$DatabasePath,
    [int]$OfflineThresholdSeconds = 0,
    [int]$AgentHeartbeatSeconds = 30,
    [string]$ServerRuntimeIdentifier = "win-x64",
    [string]$ConsoleRuntimeIdentifier = "win-x64",
    [string]$AgentRuntimeIdentifier = "win-x64",
    [string]$SetupWorkerRuntimeIdentifier = "win-x64"
)

$ErrorActionPreference = "Stop"
$commonScript = Join-Path $PSScriptRoot "Common.ps1"
. $commonScript

$iscc = Get-InnoSetupCompiler

$publishAllScript = Join-Path $PSScriptRoot "publish-all.ps1"
& $publishAllScript `
    -Configuration $Configuration `
    -ServerListenUrl $ServerListenUrl `
    -ServerBaseUrl $ServerBaseUrl `
    -DatabasePath $DatabasePath `
    -OfflineThresholdSeconds $OfflineThresholdSeconds `
    -ServerRuntimeIdentifier $ServerRuntimeIdentifier `
    -ServerSelfContained `
    -ConsoleRuntimeIdentifier $ConsoleRuntimeIdentifier `
    -ConsoleSelfContained `
    -SetupWorkerRuntimeIdentifier $SetupWorkerRuntimeIdentifier

$buildAgentScript = Join-Path $PSScriptRoot "build-inno-agent.ps1"
& $buildAgentScript `
    -Configuration $Configuration `
    -HeartbeatSeconds $AgentHeartbeatSeconds `
    -RuntimeIdentifier $AgentRuntimeIdentifier

$issPath = Join-Path (Get-ProjectRoot) "installer\inno\LanAdminServer.iss"
& $iscc $issPath
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup compilation failed for LanAdminServer.iss"
}
