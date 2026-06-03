param(
    [string]$Configuration = "Release",
    [string]$Output = ".\artifacts\setup-worker",
    [string]$RuntimeIdentifier = "win-x64"
)

$ErrorActionPreference = "Stop"
$commonScript = Join-Path $PSScriptRoot "Common.ps1"
. $commonScript

$project = Join-Path $PSScriptRoot "..\src\LanAdmin.SetupWorker\LanAdmin.SetupWorker.csproj"
$outputPath = Get-OutputPath -RelativePath $Output

$publishArguments = @(
    "publish",
    $project,
    "-c", $Configuration,
    "-r", $RuntimeIdentifier,
    "--self-contained", "true",
    "-o", $outputPath
)

dotnet @publishArguments
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed for LanAdmin.SetupWorker"
}

Write-Host "Published LanAdmin.SetupWorker to $outputPath"
