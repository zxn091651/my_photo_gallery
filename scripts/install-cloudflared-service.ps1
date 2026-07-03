param(
  [Parameter(Mandatory = $true)]
  [string]$PublicHostname,

  [string]$TunnelName = 'my-photo-gallery',
  [string]$LocalUrl = 'http://localhost:8787'
)

$ErrorActionPreference = 'Stop'

if (-not (Get-Command cloudflared -ErrorAction SilentlyContinue)) {
  throw 'cloudflared was not found in PATH. Install cloudflared first, then run this script again.'
}

cloudflared tunnel login
cloudflared tunnel create $TunnelName
cloudflared tunnel route dns $TunnelName $PublicHostname

$cloudflaredDir = Join-Path $env:USERPROFILE '.cloudflared'
$configPath = Join-Path $cloudflaredDir 'config.yml'
$certPath = Join-Path $cloudflaredDir 'cert.pem'
$credentials = Get-ChildItem -LiteralPath $cloudflaredDir -Filter '*.json' |
  Where-Object { $_.Name -ne 'cert.pem' } |
  Sort-Object LastWriteTime -Descending |
  Select-Object -First 1

if (-not $credentials) {
  throw "Could not find tunnel credentials in $cloudflaredDir."
}

@"
tunnel: $TunnelName
credentials-file: $($credentials.FullName)

ingress:
  - hostname: $PublicHostname
    service: $LocalUrl
  - service: http_status:404
"@ | Set-Content -LiteralPath $configPath -Encoding UTF8

cloudflared service install

Write-Host "Installed cloudflared service for https://$PublicHostname -> $LocalUrl"
Write-Host "Config: $configPath"
Write-Host "Certificate: $certPath"
