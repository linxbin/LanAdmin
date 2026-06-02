param(
    [string]$ServiceName = "LanAgent"
)

$ErrorActionPreference = "Stop"
$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $service) {
    throw "Service '$ServiceName' does not exist."
}

if ($service.Status -ne "Stopped") {
    Stop-Service -Name $ServiceName -Force
}

sc.exe delete $ServiceName | Out-Null
Write-Host "Uninstalled $ServiceName"
