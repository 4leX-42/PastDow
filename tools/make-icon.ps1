#Requires -Version 5.1
<#
.SYNOPSIS
  Genera app.ico (16..256): placa casi negra y flecha de descarga en blanco roto.
  Entradas < 256 en DIB (BMP) y 256 en PNG: System.Drawing no dibuja PNG pequenos.
#>
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$out = Join-Path (Split-Path -Parent $PSScriptRoot) 'app.ico'

function New-Frame([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    $k = $s / 256.0
    $rad = [Math]::Max(2, 52 * $k)
    $inset = [Math]::Max(0.5, 6 * $k)
    $w = $s - 2 * $inset
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $rad
    $path.AddArc($inset, $inset, $d, $d, 180, 90)
    $path.AddArc($inset + $w - $d, $inset, $d, $d, 270, 90)
    $path.AddArc($inset + $w - $d, $inset + $w - $d, $d, $d, 0, 90)
    $path.AddArc($inset, $inset + $w - $d, $d, $d, 90, 90)
    $path.CloseFigure()

    $rect = New-Object System.Drawing.RectangleF 0, 0, $s, $s
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(30, 30, 30)), ([System.Drawing.Color]::FromArgb(12, 12, 12)), 90
    $g.FillPath($bg, $path)

    $gold = [System.Drawing.Color]::FromArgb(238, 238, 236)
    $goldDeep = [System.Drawing.Color]::FromArgb(196, 196, 192)
    # trazos mas gruesos en tamanos pequenos para que se lean
    $bw = [Math]::Max(1.0, 8 * $k)
    if ($s -le 32) { $bw = [Math]::Max(1.0, 1.2 * $s / 16) }
    $pen = New-Object System.Drawing.Pen $gold, $bw
    $g.DrawPath($pen, $path)

    $stroke = 26 * $k
    if ($s -le 48) { $stroke = $s * 0.14 }
    $ab = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, $gold, $goldDeep, 90
    $ap = New-Object System.Drawing.Pen $ab, $stroke
    $ap.StartCap = 'Round'; $ap.EndCap = 'Round'; $ap.LineJoin = 'Round'
    $cx = $s / 2.0
    $g.DrawLine($ap, $cx, $s * 0.22, $cx, $s * 0.60)
    $g.DrawLines($ap, [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF ($s * 0.33), ($s * 0.46)),
        (New-Object System.Drawing.PointF $cx, ($s * 0.62)),
        (New-Object System.Drawing.PointF ($s * 0.67), ($s * 0.46))))
    $g.DrawLine($ap, $s * 0.27, $s * 0.78, $s * 0.73, $s * 0.78)
    $g.Dispose()
    return $bmp
}

function Get-Dib($bmp) {
    $s = $bmp.Width
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter $ms
    $bw.Write([int]40); $bw.Write([int]$s); $bw.Write([int]($s * 2)); $bw.Write([int16]1); $bw.Write([int16]32)
    $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)
    for ($y = $s - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $s; $x++) {
            $c = $bmp.GetPixel($x, $y)
            $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A)
        }
    }
    $maskRow = [int]([Math]::Ceiling($s / 32.0) * 4)
    $bw.Write((New-Object byte[] ($maskRow * $s)))
    $bw.Flush()
    return , $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = @()
foreach ($s in $sizes) {
    $bmp = New-Frame $s
    if ($s -eq 256) {
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $images += , $ms.ToArray()
    } else {
        $images += , (Get-Dib $bmp)
    }
    if ($s -eq 256) { $bmp.Save((Join-Path (Split-Path -Parent $PSScriptRoot) 'tools\icon-preview.png')) }
    $bmp.Dispose()
}

$fs = [System.IO.File]::Create($out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $b = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$b); $w.Write([byte]$b); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([int16]1); $w.Write([int16]32); $w.Write([int]$images[$i].Length); $w.Write([int]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write([byte[]]$img) }
$w.Close()
Write-Host "OK $out"
