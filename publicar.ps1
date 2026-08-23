# Genera el .exe autocontenido (un solo archivo, sin .NET en la PC destino).
# Requiere .NET 8 SDK solo en ESTA máquina de desarrollo.

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$proj = Join-Path $root "SBBackup\SBBackup.csproj"
$out = Join-Path $root "publish"
$exeName = "ST2 - Herramientas SQL.exe"

# win-x64 = Windows 64 bits (la mayoría). Para PCs muy viejas 32 bits use: win-x86
$rid = if ($args -contains "-x86") { "win-x86" } else { "win-x64" }

Write-Host "Publicando $exeName ($rid) en: $out" -ForegroundColor Cyan

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Path $out | Out-Null

dotnet publish $proj `
  -c Release `
  -r $rid `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -o $out

Get-ChildItem $out -File | Where-Object { $_.Name -ne $exeName } | Remove-Item -Force

$exe = Join-Path $out $exeName
if (-not (Test-Path $exe)) { throw "No se generó $exeName" }

$mb = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host ""
Write-Host "Listo: $exe ($mb MB)" -ForegroundColor Green
Write-Host "Copiá solo ese archivo a las PCs de los usuarios." -ForegroundColor Green
