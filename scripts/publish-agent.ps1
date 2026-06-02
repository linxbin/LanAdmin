param(
    [string]$Configuration = "Release",
    [string]$Output = ".\artifacts\agent"
)

$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "..\src\LanAgent\LanAgent.csproj"
$outputPath = Resolve-Path (Join-Path $PSScriptRoot "..") | ForEach-Object { Join-Path $_ $Output.TrimStart(".\") }

dotnet publish $project -c $Configuration -o $outputPath
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed for LanAgent"
}

Write-Host "Published LanAgent to $outputPath"
