param(
  [string]$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
)

$ErrorActionPreference = 'Stop'

$sourcePath = Join-Path $PSScriptRoot 'BackendControl.cs'
$controlExe = Join-Path $ProjectRoot 'gallery-backend-control.exe'
$source = Get-Content -LiteralPath $sourcePath -Raw -Encoding UTF8

Remove-Item -LiteralPath $controlExe -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $ProjectRoot 'start-backend.exe') -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $ProjectRoot 'stop-backend.exe') -Force -ErrorAction SilentlyContinue

Add-Type `
  -TypeDefinition $source `
  -Language CSharp `
  -ReferencedAssemblies @('System.Windows.Forms.dll', 'System.Drawing.dll') `
  -OutputAssembly $controlExe `
  -OutputType WindowsApplication

Write-Host "Built launcher: $controlExe"
