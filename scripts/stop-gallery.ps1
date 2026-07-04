param(
  [int]$Port = 8787
)

$ErrorActionPreference = 'Stop'

$connections = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue
if (-not $connections) {
  Write-Host "Gallery backend is not running on port $Port."
  exit 0
}

$processIds = $connections | Select-Object -ExpandProperty OwningProcess -Unique
foreach ($processId in $processIds) {
  $process = Get-Process -Id $processId -ErrorAction SilentlyContinue
  if (-not $process) {
    continue
  }

  Stop-Process -Id $processId -Force -ErrorAction SilentlyContinue
  Wait-Process -Id $processId -Timeout 8 -ErrorAction SilentlyContinue
  Write-Host "Stopped gallery backend process $processId."
}

$deadline = (Get-Date).AddSeconds(12)
while ((Get-Date) -lt $deadline) {
  $remainingConnections = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue
  if (-not $remainingConnections) {
    exit 0
  }

  Start-Sleep -Milliseconds 300
}

Write-Host "Gallery backend stop was requested, but port $Port is still busy."
exit 1
