<#
.SYNOPSIS
  Regenerates src\ProactiveAI.Desktop\Assets\AppIcon.ico (multi-resolution, PNG-compressed entries).
  Requires Windows PowerShell 5.1 (System.Drawing). Run from the repository root.
#>
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot '..\src\ProactiveAI.Desktop\Assets\AppIcon.ico'
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = @()
foreach ($s in $sizes) {
  $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = 'AntiAlias'
  $g.Clear([System.Drawing.Color]::Transparent)
  $r = [Math]::Max(3, [int]($s * 0.22))
  $path = New-Object System.Drawing.Drawing2D.GraphicsPath
  $d = $r * 2
  $w = $s - 1
  $path.AddArc(0, 0, $d, $d, 180, 90); $path.AddArc($w - $d, 0, $d, $d, 270, 90)
  $path.AddArc($w - $d, $w - $d, $d, $d, 0, 90); $path.AddArc(0, $w - $d, $d, $d, 90, 90); $path.CloseFigure()
  $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.Point 0, 0), (New-Object System.Drawing.Point $s, $s), ([System.Drawing.Color]::FromArgb(255, 37, 99, 235)), ([System.Drawing.Color]::FromArgb(255, 124, 58, 237))
  $g.FillPath($brush, $path)
  # waveform bars
  $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
  $heights = 0.30, 0.55, 0.80, 0.55, 0.30
  $barW = [Math]::Max(1.0, $s * 0.09)
  $gap = $s * 0.045
  $total = $heights.Count * $barW + ($heights.Count - 1) * $gap
  $x = ($s - $total) / 2
  foreach ($h in $heights) {
    $bh = $s * $h * 0.62
    $g.FillRectangle($white, [single]$x, [single](($s - $bh) / 2), [single]$barW, [single]$bh)
    $x += $barW + $gap
  }
  $g.Dispose()
  $ms = New-Object System.IO.MemoryStream
  $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
  $bmp.Dispose()
  $pngs += ,@($s, $ms.ToArray())
}
$fs = [System.IO.File]::Create((Resolve-Path (Split-Path $out)).Path + '\AppIcon.ico')
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$pngs.Count)
$offset = 6 + 16 * $pngs.Count
foreach ($p in $pngs) {
  $s = $p[0]; $data = $p[1]
  $dim = if ($s -ge 256) { 0 } else { $s }
  $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
  $bw.Write([UInt16]1); $bw.Write([UInt16]32); $bw.Write([UInt32]$data.Length); $bw.Write([UInt32]$offset)
  $offset += $data.Length
}
foreach ($p in $pngs) { $bw.Write($p[1]) }
$bw.Close()
Write-Host "Wrote $out"
