param(
  [string]$TaskName = 'MyPhotoGalleryBackend',
  [string]$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
)

$ErrorActionPreference = 'Stop'

Set-Location $ProjectRoot

if (-not (Get-Command node -ErrorAction SilentlyContinue)) {
  throw 'Node.js was not found in PATH. Install Node.js first, then run this script again.'
}

$envFile = Join-Path $ProjectRoot '.env'
if (-not (Test-Path $envFile)) {
  @(
    'PORT=8787',
    'HOST=0.0.0.0',
    'GALLERY_MEDIA_FOLDER=影像备份',
    'GALLERY_DISK_SERIAL=REPLACE_WITH_DISK_SERIAL'
  ) | Where-Object { $_ } | Set-Content -LiteralPath $envFile -Encoding UTF8
}

$scriptPath = Join-Path $ProjectRoot 'scripts\start-gallery.ps1'
$powershellPath = (Get-Command powershell.exe).Source
$arguments = "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$scriptPath`""

$action = New-ScheduledTaskAction -Execute $powershellPath -Argument $arguments -WorkingDirectory $ProjectRoot
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
$principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet `
  -AllowStartIfOnBatteries `
  -DontStopIfGoingOnBatteries `
  -Hidden `
  -ExecutionTimeLimit (New-TimeSpan -Days 365) `
  -MultipleInstances IgnoreNew `
  -RestartCount 3 `
  -RestartInterval (New-TimeSpan -Minutes 1)

Register-ScheduledTask `
  -TaskName $TaskName `
  -Action $action `
  -Trigger $trigger `
  -Principal $principal `
  -Settings $settings `
  -Description 'Starts the local backend for my photo gallery at Windows logon.' `
  -Force | Out-Null

Start-ScheduledTask -TaskName $TaskName

Write-Host "Installed and started scheduled task: $TaskName"
Write-Host "Project root: $ProjectRoot"
Write-Host "Environment file: $envFile"
