param(
    [string]$Configuration = "Release",
    [string]$Output = ".\artifacts\console",
    [string]$ServerBaseUrl,
    [string]$RuntimeIdentifier,
    [switch]$SelfContained
)

$ErrorActionPreference = "Stop"
$commonScript = Join-Path $PSScriptRoot "Common.ps1"
. $commonScript

$project = Join-Path $PSScriptRoot "..\src\LanAdmin.Console\LanAdmin.Console.csproj"
$outputPath = Get-OutputPath -RelativePath $Output

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
    throw "dotnet publish failed for LanAdmin.Console"
}

$appSettingsPath = Join-Path $outputPath "appsettings.json"
Update-JsonFile -Path $appSettingsPath -Mutator {
    param($json)

    if ($ServerBaseUrl) {
        $json.Console.ServerBaseUrl = $ServerBaseUrl
    }
}

Write-Host "Published LanAdmin.Console to $outputPath"
