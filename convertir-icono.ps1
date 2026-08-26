# Regenera SBBackup\Assets\app.ico con varios tamaños (16…256) a partir de app.png o app.ico.
# Así la barra de tareas / alt-tab se ve nítida en DPI altos.

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$assets = Join-Path $PSScriptRoot "SBBackup\Assets"
$png = Join-Path $assets "app.png"
$ico = Join-Path $assets "app.ico"

function New-SquareBitmap([System.Drawing.Image]$src, [int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $bmp.SetResolution(96, 96)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::FromArgb(255, 32, 32, 32))
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $scale = [Math]::Min($size / [double]$src.Width, $size / [double]$src.Height)
    $w = [int][Math]::Round($src.Width * $scale)
    $h = [int][Math]::Round($src.Height * $scale)
    $x = ($size - $w) / 2.0
    $y = ($size - $h) / 2.0
    $g.DrawImage($src, [float]$x, [float]$y, [float]$w, [float]$h)
    $g.Dispose()
    return $bmp
}

function Get-PngBytes([System.Drawing.Bitmap]$bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    return $ms.ToArray()
}

if (Test-Path $png) {
    $src = [System.Drawing.Image]::FromFile($png)
}
elseif (Test-Path $ico) {
    $tmp = New-Object System.Drawing.Icon $ico, 256, 256
    $src = $tmp.ToBitmap()
    $tmp.Dispose()
}
else {
    Write-Error "Colocá SBBackup\Assets\app.png (o app.ico) y volvé a ejecutar."
}

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$pngs = @()
foreach ($s in $sizes) {
    $b = New-SquareBitmap $src $s
    $pngs += ,@($s, (Get-PngBytes $b))
    $b.Dispose()
}
$src.Dispose()

$count = $pngs.Count
$header = New-Object byte[] (6 + 16 * $count)
[BitConverter]::GetBytes([uint16]0).CopyTo($header, 0)
[BitConverter]::GetBytes([uint16]1).CopyTo($header, 2)
[BitConverter]::GetBytes([uint16]$count).CopyTo($header, 4)
$offset = 6 + 16 * $count
$data = New-Object System.Collections.Generic.List[byte]
for ($i = 0; $i -lt $count; $i++) {
    $s = [int]$pngs[$i][0]
    $pngBytes = [byte[]]$pngs[$i][1]
    $o = 6 + $i * 16
    $header[$o] = if ($s -ge 256) { 0 } else { [byte]$s }
    $header[$o + 1] = if ($s -ge 256) { 0 } else { [byte]$s }
    [BitConverter]::GetBytes([uint16]1).CopyTo($header, $o + 4)
    [BitConverter]::GetBytes([uint16]32).CopyTo($header, $o + 6)
    [BitConverter]::GetBytes([uint32]$pngBytes.Length).CopyTo($header, $o + 8)
    [BitConverter]::GetBytes([uint32]$offset).CopyTo($header, $o + 12)
    $data.AddRange($pngBytes)
    $offset += $pngBytes.Length
}

$fs = [IO.File]::Create($ico)
$fs.Write($header, 0, $header.Length)
$payload = $data.ToArray()
$fs.Write($payload, 0, $payload.Length)
$fs.Close()

Write-Host "Generado: $ico ($count tamaños: $($sizes -join ', '))" -ForegroundColor Green
