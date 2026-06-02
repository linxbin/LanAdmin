param(
    [string]$PublishPath = ".\artifacts\agent",
    [string]$ServiceName = "LanAgent",
    [string]$DisplayName = "LanAdmin Agent"
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$servicePath = Join-Path $root $PublishPath.TrimStart(".\")
$exePath = Join-Path $servicePath "LanAgent.exe"

if (-not (Test-Path $exePath)) {
    throw "Agent executable not found: $exePath"
}

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    throw "Service '$ServiceName' already exists."
}

New-Service -Name $ServiceName -DisplayName $DisplayName -BinaryPathName "`"$exePath`"" -StartupType Automatic
sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/5000 | Out-Null
Start-Service -Name $ServiceName
Write-Host "Installed and started $ServiceName"
