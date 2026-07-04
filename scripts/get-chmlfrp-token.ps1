param(
  [string]$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
  [string]$AccessToken = ""
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

function Read-SharedBytes {
  param(
    [string]$Path,
    [int]$MaxBytes = 2097152
  )

  $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete)
  try {
    $length = [Math]::Min($stream.Length, $MaxBytes)
    $bytes = New-Object byte[] $length
    $read = $stream.Read($bytes, 0, $length)
    if ($read -eq $bytes.Length) {
      return $bytes
    }

    $trimmed = New-Object byte[] $read
    [Array]::Copy($bytes, $trimmed, $read)
    return $trimmed
  } finally {
    $stream.Dispose()
  }
}

function Read-LauncherAccessTokens {
  $levelDbDir = Join-Path $env:LOCALAPPDATA 'net.chmlfrp.launcher\EBWebView\Default\Local Storage\leveldb'
  $tokens = @()
  if (-not (Test-Path -LiteralPath $levelDbDir)) {
    return $tokens
  }

  $builder = [System.Text.StringBuilder]::new()
  foreach ($file in Get-ChildItem -LiteralPath $levelDbDir -File -ErrorAction SilentlyContinue) {
    if ($file.Name -ieq 'LOCK') {
      continue
    }

    try {
      $bytes = Read-SharedBytes -Path $file.FullName
      [void]$builder.Append([System.Text.Encoding]::UTF8.GetString($bytes))
      [void]$builder.Append([System.Text.Encoding]::Unicode.GetString($bytes))
    } catch {}
  }

  $compact = [regex]::Replace($builder.ToString(), '[\x00\s]', '')
  $matches = [regex]::Matches($compact, 'accessToken\":\"(?<token>[A-Za-z0-9_.-]{80,2000})\".*?accessTokenExpiresAt\":(?<expires>\d+)')
  foreach ($match in $matches) {
    $token = $match.Groups['token'].Value
    $expires = [int64]$match.Groups['expires'].Value
    if ($tokens | Where-Object { $_.Token -eq $token }) {
      continue
    }

    $tokens += [pscustomobject]@{
      Token = $token
      ExpiresAt = $expires
    }
  }

  return $tokens | Sort-Object ExpiresAt -Descending
}

function Exchange-AccessToken {
  param([string]$Token)

  $url = "https://cf-v2.uapis.cn/login?access_token=$([uri]::EscapeDataString($Token))"
  return Invoke-RestMethod -Method Get -Uri $url -TimeoutSec 25
}

function Extract-UserToken {
  param($Response)

  if (-not $Response) {
    return ""
  }

  if ($Response.data) {
    if ($Response.data.usertoken) { return [string]$Response.data.usertoken }
    if ($Response.data.userToken) { return [string]$Response.data.userToken }
    if ($Response.data.token) { return [string]$Response.data.token }
  }

  if ($Response.usertoken) { return [string]$Response.usertoken }
  if ($Response.userToken) { return [string]$Response.userToken }
  if ($Response.token) { return [string]$Response.token }
  return ""
}

try {
  [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

  Write-Host "ChmlFrp token helper"
  Write-Host "This helper imports the login state from ChmlFrp Launcher."
  Write-Host ""
  Write-Host "Before running it:"
  Write-Host "1. Open ChmlFrp Launcher."
  Write-Host "2. If it was open for a while, log out and log in again."
  Write-Host "3. Keep ChmlFrp Launcher open, then run this command again."
  Write-Host ""

  $accessTokens = @()
  if (-not [string]::IsNullOrWhiteSpace($AccessToken)) {
    $accessTokens += [pscustomobject]@{ Token = $AccessToken; ExpiresAt = [int64]::MaxValue }
  }
  $accessTokens += Read-LauncherAccessTokens

  if ($accessTokens.Count -eq 0) {
    throw "No Launcher accessToken was found. Please log in to ChmlFrp Launcher first."
  }

  $nowMs = [DateTimeOffset]::Now.ToUnixTimeMilliseconds()
  $lastMessage = ""
  $userToken = ""
  foreach ($item in $accessTokens) {
    if ($item.ExpiresAt -lt $nowMs) {
      $expiresText = [DateTimeOffset]::FromUnixTimeMilliseconds($item.ExpiresAt).LocalDateTime
      $lastMessage = "Launcher accessToken expired at $expiresText."
      continue
    }

    Write-Host "Exchanging Launcher accessToken..."
    $response = Exchange-AccessToken -Token $item.Token
    $userToken = Extract-UserToken -Response $response
    if ($userToken) {
      break
    }

    if ($response.msg) {
      $lastMessage = [string]$response.msg
    } else {
      $lastMessage = "The login API did not return usertoken."
    }
  }

  if (-not $userToken) {
    throw "Could not get ChmlFrp usertoken. $lastMessage"
  }

  $envPath = Join-Path $ProjectRoot '.env'
  Set-EnvValue -Path $envPath -Key 'CHMLFRP_USER_TOKEN' -Value $userToken

  $sdkDir = Join-Path $env:APPDATA 'ChmlFrp'
  $sdkPath = Join-Path $sdkDir 'user.json'
  New-Item -ItemType Directory -Force -Path $sdkDir | Out-Null
  [System.IO.File]::WriteAllText($sdkPath, ('{"usertoken":"' + $userToken + '"}'), [System.Text.Encoding]::UTF8)

  $masked = if ($userToken.Length -gt 10) { $userToken.Substring(0, 4) + '****' + $userToken.Substring($userToken.Length - 4) } else { '****' }
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
  Write-Host "Do this and retry:"
  Write-Host "1. Open ChmlFrp Launcher."
  Write-Host "2. Log out, then log in again."
  Write-Host "3. Keep Launcher open."
  Write-Host "4. Run: npm run chmlfrp:login"
  exit 1
}
