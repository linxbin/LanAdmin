param(
    [string]$Configuration = "Release",
    [string]$Output = ".\artifacts\server"
)

$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "..\src\LanAdmin.Server\LanAdmin.Server.csproj"
$outputPath = Resolve-Path (Join-Path $PSScriptRoot "..") | ForEach-Object { Join-Path $_ $Output.TrimStart(".\") }

dotnet publish $project -c $Configuration -o $outputPath
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed for LanAdmin.Server"
}

Write-Host "Published LanAdmin.Server to $outputPath"
