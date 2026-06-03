param(
    [string]$Configuration = "Release",
    [string]$Output = ".\artifacts\agent",
    [int]$HeartbeatSeconds = 0,
    [string]$LogPath,
    [string]$LogLevel,
    [string]$RuntimeIdentifier,
    [switch]$SelfContained
)

$ErrorActionPreference = "Stop"
$commonScript = Join-Path $PSScriptRoot "Common.ps1"
. $commonScript

$project = Join-Path $PSScriptRoot "..\src\LanAgent\LanAgent.csproj"
$outputPath = Get-OutputPath -RelativePath $Output

if ($RuntimeIdentifier) {
    dotnet restore $project -r $RuntimeIdentifier
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet restore failed for LanAgent"
    }
}

$publishArguments = @(
    "publish",
    $project,
    "-c", $Configuration,
    "-o", $outputPath
)

if ($RuntimeIdentifier) {
    $publishArguments += @("-r", $RuntimeIdentifier)
}

if ($SelfContained) {
    $publishArguments += @("--self-contained", "true")
}

dotnet @publishArguments
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed for LanAgent"
}

$appSettingsPath = Join-Path $outputPath "appsettings.json"
Update-JsonFile -Path $appSettingsPath -Mutator {
    param($json)

    if ($HeartbeatSeconds -gt 0) {
        $json.Agent.HeartbeatSeconds = $HeartbeatSeconds
    }

    if ($LogPath) {
        $json.FileLogging.Path = $LogPath
    }

    if ($LogLevel) {
        $json.FileLogging.MinimumLevel = $LogLevel
    }
}

Write-Host "Published LanAgent to $outputPath"
