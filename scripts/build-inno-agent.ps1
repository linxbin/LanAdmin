param(
    [string]$Configuration = "Release",
    [int]$HeartbeatSeconds = 30,
    [string]$RuntimeIdentifier = "win-x64",
    [string]$SetupWorkerRuntimeIdentifier = "win-x64"
)

$ErrorActionPreference = "Stop"
$commonScript = Join-Path $PSScriptRoot "Common.ps1"
. $commonScript

$iscc = Get-InnoSetupCompiler

$publishAgentScript = Join-Path $PSScriptRoot "publish-agent.ps1"
& $publishAgentScript `
    -Configuration $Configuration `
    -HeartbeatSeconds $HeartbeatSeconds `
    -RuntimeIdentifier $RuntimeIdentifier `
    -SelfContained

$publishSetupWorkerScript = Join-Path $PSScriptRoot "publish-setup-worker.ps1"
& $publishSetupWorkerScript `
    -Configuration $Configuration `
    -RuntimeIdentifier $SetupWorkerRuntimeIdentifier

$issPath = Join-Path (Get-ProjectRoot) "installer\inno\LanAgent.iss"
& $iscc $issPath
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup compilation failed for LanAgent.iss"
}
