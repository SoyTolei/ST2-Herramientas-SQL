# Convierte app.png → app.ico (si tenés PNG en lugar de ICO).
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$assets = Join-Path $PSScriptRoot "SBBackup\Assets"
$png = Join-Path $assets "app.png"
$ico = Join-Path $assets "app.ico"

if (-not (Test-Path $png)) {
    Write-Error "No existe: $png`nColocá tu imagen como SBBackup\Assets\app.png o app.ico"
}

$bmp = [System.Drawing.Bitmap]::FromFile($png)
$ptr = $bmp.GetHicon()
$icon = [System.Drawing.Icon]::FromHandle($ptr)
$fs = [System.IO.File]::Open($ico, [System.IO.FileMode]::Create)
$icon.Save($fs)
$fs.Close()
$icon.Dispose()
$bmp.Dispose()

Write-Host "Generado: $ico" -ForegroundColor Green
