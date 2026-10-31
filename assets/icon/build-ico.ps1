# Rebuilds app.ico from the owner's app.png (D-14): padded to a centred transparent square, then the same image
# scaled to 16/20/24/32/40/48/64/256 px, 32 bpp with alpha (256 stored as PNG, the others as 32-bit DIBs).
# Needs ImageMagick 7 (magick) on PATH. Run from anywhere: pwsh assets/icon/build-ico.ps1
$ErrorActionPreference = 'Stop'
$dir = $PSScriptRoot
$png = Join-Path $dir 'app.png'
$ico = Join-Path $dir 'app.ico'

$size = (& magick identify -format '%[fx:max(w,h)]' $png)
& magick $png -background none -gravity center -extent "${size}x${size}" `
    -define icon:auto-resize=256,64,48,40,32,24,20,16 -type TrueColorAlpha -depth 8 $ico
if ($LASTEXITCODE -ne 0) { throw "magick failed ($LASTEXITCODE)" }
Write-Host "wrote $ico"
