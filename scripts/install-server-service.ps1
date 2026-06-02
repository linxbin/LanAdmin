param(
    [string]$PublishPath = ".\artifacts\server",
    [string]$ServiceName = "LanAdminServer",
    [string]$DisplayName = "LanAdmin Server"
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$servicePath = Join-Path $root $PublishPath.TrimStart(".\")
$exePath = Join-Path $servicePath "LanAdmin.Server.exe"

if (-not (Test-Path $exePath)) {
    throw "Server executable not found: $exePath"
}

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    throw "Service '$ServiceName' already exists."
}

New-Service -Name $ServiceName -DisplayName $DisplayName -BinaryPathName "`"$exePath`"" -StartupType Automatic
sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/5000 | Out-Null
Start-Service -Name $ServiceName
Write-Host "Installed and started $ServiceName"
