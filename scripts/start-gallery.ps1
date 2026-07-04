param(
  [string]$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
)

$ErrorActionPreference = 'Stop'

Set-Location $ProjectRoot

$envFile = Join-Path $ProjectRoot '.env'
if (Test-Path $envFile) {
  Get-Content -LiteralPath $envFile -Encoding UTF8 | ForEach-Object {
    $line = $_.Trim()
    if (-not $line -or $line.StartsWith('#')) {
      return
    }

    $separator = $line.IndexOf('=')
    if ($separator -lt 1) {
      return
    }

    $key = $line.Substring(0, $separator).Trim()
    $value = $line.Substring($separator + 1).Trim().Trim('"')
    [Environment]::SetEnvironmentVariable($key, $value, 'Process')
  }
}

if (-not $env:PORT) { $env:PORT = '8787' }
if (-not $env:HOST) { $env:HOST = '0.0.0.0' }
if (-not $env:CHMLFRP_FRPC_PATH) { $env:CHMLFRP_FRPC_PATH = Join-Path $env:APPDATA 'net.chmlfrp.launcher\frpc.exe' }
if (-not $env:CHMLFRP_CONFIG_PATH) { $env:CHMLFRP_CONFIG_PATH = Join-Path $env:APPDATA 'net.chmlfrp.launcher\g_314121.ini' }

$logDir = Join-Path $ProjectRoot 'logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null

$stamp = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
"[$stamp] Starting gallery backend in $ProjectRoot" | Add-Content -LiteralPath (Join-Path $logDir 'gallery-startup.log') -Encoding UTF8

$frpcLog = Join-Path $logDir 'frpc.log'
$frpcErrorLog = Join-Path $logDir 'frpc-error.log'
$frpcProcess = Get-Process -Name frpc -ErrorAction SilentlyContinue
if ($frpcProcess) {
  "[$stamp] frpc is already running. Skipping tunnel startup." | Add-Content -LiteralPath (Join-Path $logDir 'gallery-startup.log') -Encoding UTF8
} elseif ((Test-Path -LiteralPath $env:CHMLFRP_FRPC_PATH) -and (Test-Path -LiteralPath $env:CHMLFRP_CONFIG_PATH)) {
  $frpc = Start-Process `
    -FilePath $env:CHMLFRP_FRPC_PATH `
    -ArgumentList @('-c', $env:CHMLFRP_CONFIG_PATH) `
    -WorkingDirectory (Split-Path -Parent $env:CHMLFRP_FRPC_PATH) `
    -WindowStyle Hidden `
    -RedirectStandardOutput $frpcLog `
    -RedirectStandardError $frpcErrorLog `
    -PassThru
  "[$stamp] Started frpc process $($frpc.Id)." | Add-Content -LiteralPath (Join-Path $logDir 'gallery-startup.log') -Encoding UTF8
} else {
  "[$stamp] frpc startup skipped. Missing CHMLFRP_FRPC_PATH or CHMLFRP_CONFIG_PATH." | Add-Content -LiteralPath (Join-Path $logDir 'gallery-startup.log') -Encoding UTF8
}

$existingListener = netstat -ano | Select-String ":$env:PORT" | Select-String 'LISTENING'
if ($existingListener) {
  "[$stamp] Port $env:PORT already has a listener. Skipping startup." | Add-Content -LiteralPath (Join-Path $logDir 'gallery-startup.log') -Encoding UTF8
  exit 0
}

$stdoutLog = Join-Path $logDir 'gallery-server.log'
$stderrLog = Join-Path $logDir 'gallery-server-error.log'

$process = Start-Process `
  -FilePath 'node' `
  -ArgumentList 'server.js' `
  -WorkingDirectory $ProjectRoot `
  -WindowStyle Hidden `
  -RedirectStandardOutput $stdoutLog `
  -RedirectStandardError $stderrLog `
  -PassThru

"[$stamp] Started node process $($process.Id)." | Add-Content -LiteralPath (Join-Path $logDir 'gallery-startup.log') -Encoding UTF8
