param(
  [string]$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
)

$ErrorActionPreference = 'Stop'

$sourcePath = Join-Path $PSScriptRoot 'BackendLauncher.cs'
$startExe = Join-Path $ProjectRoot 'start-backend.exe'
$stopExe = Join-Path $ProjectRoot 'stop-backend.exe'
$source = Get-Content -LiteralPath $sourcePath -Raw -Encoding UTF8

Add-Type `
  -TypeDefinition $source `
  -Language CSharp `
  -OutputAssembly $startExe `
  -OutputType WindowsApplication

Copy-Item -LiteralPath $startExe -Destination $stopExe -Force

Write-Host "Built launcher: $startExe"
Write-Host "Built launcher: $stopExe"
