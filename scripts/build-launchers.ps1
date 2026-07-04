param(
  [string]$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
)

$ErrorActionPreference = 'Stop'

$sourcePath = Join-Path $PSScriptRoot 'BackendControl.cs'
$controlExe = Join-Path $ProjectRoot 'gallery-backend-control.exe'
$assetsDir = Join-Path $ProjectRoot 'assets'
$iconPath = Join-Path $assetsDir 'gallery-backend-control.ico'

Remove-Item -LiteralPath $controlExe -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $ProjectRoot 'start-backend.exe') -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $ProjectRoot 'stop-backend.exe') -Force -ErrorAction SilentlyContinue

New-Item -ItemType Directory -Force -Path $assetsDir | Out-Null

Add-Type -AssemblyName System.Drawing
$bitmap = New-Object System.Drawing.Bitmap 256, 256
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$rect = New-Object System.Drawing.Rectangle 0, 0, 256, 256
$background = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(22, 28, 42)), ([System.Drawing.Color]::FromArgb(7, 10, 18)), 135
$graphics.FillRectangle($background, $rect)
$glowBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(95, 116, 205, 255))
$graphics.FillEllipse($glowBrush, -42, -34, 156, 156)
$violetBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(82, 196, 150, 255))
$graphics.FillEllipse($violetBrush, 134, 142, 164, 142)
$path = New-Object System.Drawing.Drawing2D.GraphicsPath
$path.AddArc(24, 24, 208, 208, 180, 90)
$path.AddArc(24, 24, 208, 208, 270, 90)
$path.AddArc(24, 24, 208, 208, 0, 90)
$path.AddArc(24, 24, 208, 208, 90, 90)
$path.CloseFigure()
$glass = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.Rectangle 24, 24, 208, 208), ([System.Drawing.Color]::FromArgb(74, 255, 255, 255)), ([System.Drawing.Color]::FromArgb(18, 255, 255, 255)), 135
$border = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(110, 255, 255, 255)), 3
$graphics.FillPath($glass, $path)
$graphics.DrawPath($border, $path)
$diskBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(92, 223, 148))
$graphics.FillEllipse($diskBrush, 48, 54, 30, 30)
$font = New-Object System.Drawing.Font 'Segoe UI', 53, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
$textBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
$format = New-Object System.Drawing.StringFormat
$format.Alignment = [System.Drawing.StringAlignment]::Center
$format.LineAlignment = [System.Drawing.StringAlignment]::Center
$graphics.DrawString('ZXN', $font, $textBrush, (New-Object System.Drawing.RectangleF 31, 75, 194, 86), $format)
$smallFont = New-Object System.Drawing.Font 'Segoe UI', 22, ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
$mutedBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(210, 218, 235, 255))
$graphics.DrawString('Gallery', $smallFont, $mutedBrush, (New-Object System.Drawing.RectangleF 31, 145, 194, 42), $format)
$pngStream = New-Object System.IO.MemoryStream
$bitmap.Save($pngStream, [System.Drawing.Imaging.ImageFormat]::Png)
$pngBytes = $pngStream.ToArray()
$icoStream = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter $icoStream
$writer.Write([UInt16]0)
$writer.Write([UInt16]1)
$writer.Write([UInt16]1)
$writer.Write([Byte]0)
$writer.Write([Byte]0)
$writer.Write([Byte]0)
$writer.Write([Byte]0)
$writer.Write([UInt16]1)
$writer.Write([UInt16]32)
$writer.Write([UInt32]$pngBytes.Length)
$writer.Write([UInt32]22)
$writer.Write($pngBytes)
$writer.Flush()
[System.IO.File]::WriteAllBytes($iconPath, $icoStream.ToArray())
$writer.Dispose()
$icoStream.Dispose()
$pngStream.Dispose()
$graphics.Dispose()
$bitmap.Dispose()
$background.Dispose()
$glowBrush.Dispose()
$violetBrush.Dispose()
$glass.Dispose()
$border.Dispose()
$diskBrush.Dispose()
$textBrush.Dispose()
$mutedBrush.Dispose()
$font.Dispose()
$smallFont.Dispose()
$format.Dispose()

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) {
  $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}

& $csc `
  /nologo `
  /target:winexe `
  /platform:anycpu `
  /optimize+ `
  /win32icon:"$iconPath" `
  /out:"$controlExe" `
  /reference:System.Web.Extensions.dll `
  /reference:System.Windows.Forms.dll `
  /reference:System.Drawing.dll `
  "$sourcePath"

Write-Host "Built launcher: $controlExe"
Write-Host "Embedded icon: $iconPath"
