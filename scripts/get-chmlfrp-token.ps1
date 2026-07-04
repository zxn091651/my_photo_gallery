param(
  [string]$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
)

$ErrorActionPreference = 'Stop'

function Set-EnvValue {
  param(
    [string]$Path,
    [string]$Key,
    [string]$Value
  )

  if (Test-Path -LiteralPath $Path) {
    $lines = [System.Collections.Generic.List[string]]::new()
    foreach ($line in [System.IO.File]::ReadAllLines($Path, [System.Text.Encoding]::UTF8)) {
      [void]$lines.Add($line)
    }
  } else {
    $lines = [System.Collections.Generic.List[string]]::new()
  }

  $updated = $false
  for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i].TrimStart().StartsWith("$Key=", [System.StringComparison]::OrdinalIgnoreCase)) {
      $lines[$i] = "$Key=$Value"
      $updated = $true
      break
    }
  }

  if (-not $updated) {
    [void]$lines.Add("$Key=$Value")
  }

  [System.IO.File]::WriteAllLines($Path, $lines.ToArray(), [System.Text.Encoding]::UTF8)
}

try {
  [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

  Write-Host "ChmlFrp token helper"
  Write-Host "Use your ChmlFrp Launcher account, not GitHub or Cloudflare."
  $username = Read-Host "Username"
  $securePassword = Read-Host "Password" -AsSecureString

  if ([string]::IsNullOrWhiteSpace($username)) {
    throw "Username is empty."
  }

  $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($securePassword)
  try {
    $password = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
  } finally {
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
  }

  if ([string]::IsNullOrWhiteSpace($password)) {
    throw "Password is empty."
  }

  $loginUrl = "https://cf-v2.uapis.cn/login?username=$([uri]::EscapeDataString($username))&password=$([uri]::EscapeDataString($password))"
  Write-Host "Logging in to ChmlFrp..."
  $login = Invoke-RestMethod -Method Get -Uri $loginUrl -TimeoutSec 25

  $token = $null
  if ($login -and $login.data) {
    $token = $login.data.usertoken
    if (-not $token) { $token = $login.data.userToken }
    if (-not $token) { $token = $login.data.token }
  }

  if (-not $token) {
    $message = if ($login.msg) { $login.msg } else { "The login API did not return usertoken." }
    throw "Login failed: $message"
  }

  Write-Host "Verifying token..."
  $verifyUrl = "https://cf-v2.uapis.cn/userinfo?token=$([uri]::EscapeDataString($token))"
  $verify = Invoke-RestMethod -Method Get -Uri $verifyUrl -TimeoutSec 25
  if (-not $verify -or ($verify.code -and [int]$verify.code -ne 200)) {
    $message = if ($verify.msg) { $verify.msg } else { "The verify API returned an invalid response." }
    throw "Token verify failed: $message"
  }

  $envPath = Join-Path $ProjectRoot '.env'
  Set-EnvValue -Path $envPath -Key 'CHMLFRP_USER_TOKEN' -Value $token

  $sdkDir = Join-Path $env:APPDATA 'ChmlFrp'
  $sdkPath = Join-Path $sdkDir 'user.json'
  New-Item -ItemType Directory -Force -Path $sdkDir | Out-Null
  [System.IO.File]::WriteAllText($sdkPath, ('{"usertoken":"' + $token + '"}'), [System.Text.Encoding]::UTF8)

  $masked = if ($token.Length -gt 10) { $token.Substring(0, 4) + '****' + $token.Substring($token.Length - 4) } else { '****' }
  Write-Host "Success. Token has been written to:"
  Write-Host $envPath
  Write-Host $sdkPath
  Write-Host "Token: $masked"
  Write-Host "Restart gallery-backend-control.exe or click refresh tunnels."
} catch {
  Write-Host ""
  Write-Host "Failed:" -ForegroundColor Red
  Write-Host $_.Exception.Message -ForegroundColor Red
  Write-Host ""
  Write-Host "Please check:"
  Write-Host "1. The username/password are for ChmlFrp."
  Write-Host "2. ChmlFrp Launcher can log in normally."
  Write-Host "3. This computer can access https://cf-v2.uapis.cn."
  exit 1
}
