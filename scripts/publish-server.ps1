param(
    [string]$Configuration = "Release",
    [string]$Output = ".\artifacts\server",
    [string]$ListenUrl,
    [string]$ServerBaseUrl,
    [string]$DatabasePath,
    [int]$OfflineThresholdSeconds = 0,
    [string]$LogPath,
    [string]$LogLevel,
    [string]$RuntimeIdentifier,
    [switch]$SelfContained
)

$ErrorActionPreference = "Stop"
$commonScript = Join-Path $PSScriptRoot "Common.ps1"
. $commonScript

$project = Join-Path $PSScriptRoot "..\src\LanAdmin.Server\LanAdmin.Server.csproj"
$outputPath = Get-OutputPath -RelativePath $Output

if ($RuntimeIdentifier) {
    dotnet restore $project -r $RuntimeIdentifier
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet restore failed for LanAdmin.Server"
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
    throw "dotnet publish failed for LanAdmin.Server"
}

$runtimeDataDirectory = Join-Path $outputPath "data"
$runtimeDatabasePath = Join-Path $runtimeDataDirectory "lanadmin.db"
if (Test-Path $runtimeDatabasePath) {
    Remove-Item -LiteralPath $runtimeDatabasePath -Force
}

$runtimeLogsDirectory = Join-Path $outputPath "logs"
if (Test-Path $runtimeLogsDirectory) {
    Get-ChildItem -LiteralPath $runtimeLogsDirectory -File | Remove-Item -Force
}

$appSettingsPath = Join-Path $outputPath "appsettings.json"
Update-JsonFile -Path $appSettingsPath -Mutator {
    param($json)

    if ($ListenUrl) {
        $json.Kestrel.Endpoints.Http.Url = $ListenUrl
    }

    if ($DatabasePath) {
        $json.Database.Path = $DatabasePath
    }

    if ($OfflineThresholdSeconds -gt 0) {
        $json.Agent.OfflineThresholdSeconds = $OfflineThresholdSeconds
    }

    if ($ServerBaseUrl) {
        $json.Bootstrap.ServerBaseUrl = $ServerBaseUrl
    }

    if ($LogPath) {
        $json.FileLogging.Path = $LogPath
    }

    if ($LogLevel) {
        $json.FileLogging.MinimumLevel = $LogLevel
    }
}

Write-Host "Published LanAdmin.Server to $outputPath"
