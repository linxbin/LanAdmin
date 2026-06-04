param(
    [string]$Configuration = "Release",
    [string]$ServerOutput = ".\artifacts\server",
    [string]$AgentOutput = ".\artifacts\agent",
    [string]$ConsoleOutput = ".\artifacts\console",
    [string]$ServerListenUrl,
    [string]$ServerBaseUrl,
    [string]$DatabasePath,
    [int]$OfflineThresholdSeconds = 0,
    [int]$HeartbeatSeconds = 0,
    [string]$ServerLogPath,
    [string]$AgentLogPath,
    [string]$ServerLogLevel,
    [string]$AgentLogLevel,
    [string]$ServerRuntimeIdentifier,
    [switch]$ServerSelfContained,
    [string]$AgentRuntimeIdentifier,
    [switch]$AgentSelfContained,
    [string]$ConsoleRuntimeIdentifier,
    [switch]$ConsoleSelfContained,
    [string]$SetupWorkerOutput = ".\artifacts\setup-worker",
    [string]$SetupWorkerRuntimeIdentifier = "win-x64"
)

$ErrorActionPreference = "Stop"

$serverScript = Join-Path $PSScriptRoot "publish-server.ps1"
$agentScript = Join-Path $PSScriptRoot "publish-agent.ps1"
$consoleScript = Join-Path $PSScriptRoot "publish-console.ps1"
$setupWorkerScript = Join-Path $PSScriptRoot "publish-setup-worker.ps1"

& $serverScript `
    -Configuration $Configuration `
    -Output $ServerOutput `
    -ListenUrl $ServerListenUrl `
    -ServerBaseUrl $ServerBaseUrl `
    -DatabasePath $DatabasePath `
    -OfflineThresholdSeconds $OfflineThresholdSeconds `
    -LogPath $ServerLogPath `
    -LogLevel $ServerLogLevel `
    -RuntimeIdentifier $ServerRuntimeIdentifier `
    -SelfContained:$ServerSelfContained

& $agentScript `
    -Configuration $Configuration `
    -Output $AgentOutput `
    -HeartbeatSeconds $HeartbeatSeconds `
    -ServerBaseUrl $ServerBaseUrl `
    -LogPath $AgentLogPath `
    -LogLevel $AgentLogLevel `
    -RuntimeIdentifier $AgentRuntimeIdentifier `
    -SelfContained:$AgentSelfContained

& $consoleScript `
    -Configuration $Configuration `
    -Output $ConsoleOutput `
    -ServerBaseUrl $ServerBaseUrl `
    -RuntimeIdentifier $ConsoleRuntimeIdentifier `
    -SelfContained:$ConsoleSelfContained

& $setupWorkerScript `
    -Configuration $Configuration `
    -Output $SetupWorkerOutput `
    -RuntimeIdentifier $SetupWorkerRuntimeIdentifier

Write-Host "Published server, agent, console, and setup worker artifacts."
