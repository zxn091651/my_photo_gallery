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
if (-not $env:GALLERY_DRIVE) { $env:GALLERY_DRIVE = 'F' }
if (-not $env:GALLERY_VOLUME) { $env:GALLERY_VOLUME = 'WD_BLACK' }

$logDir = Join-Path $ProjectRoot 'logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null

$stamp = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
"[$stamp] Starting gallery backend in $ProjectRoot" | Add-Content -LiteralPath (Join-Path $logDir 'gallery-startup.log') -Encoding UTF8

& node server.js 2>&1 | Tee-Object -FilePath (Join-Path $logDir 'gallery-server.log') -Append
