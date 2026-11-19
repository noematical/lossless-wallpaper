# Image-core tests. Changes nothing on the system: converts test images and checks the results
# with Pillow (an independent decoder). Needs Python with Pillow and numpy.
#   powershell -ExecutionPolicy Bypass -File tests\run-core-tests.ps1 [-Python path]
param([string]$Python = "python")
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $root 'out\LosslessWallpaper.exe'
$data = Join-Path $PSScriptRoot 'data'
$res = Join-Path $data 'out'
$W = 2560; $H = 1600
if (-not (Test-Path $exe)) { throw "Build first: build.cmd" }
& $Python -I (Join-Path $PSScriptRoot 'make_images.py') $data $W $H | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Generating test images failed" }
New-Item -ItemType Directory -Force $res | Out-Null

$script:fails = 0
function Report($name, $pass, $detail) {
    $tag = if ($pass) { 'PASS' } else { 'FAIL'; $script:fails++ }
    "{0,-4} {1,-28} {2}" -f $tag, $name, $detail
}
function Convert-Image($src, $fit, $dst) {
    $r = Join-Path $res ([IO.Path]::GetFileNameWithoutExtension($dst) + '.result.txt')
    $p = Start-Process $exe -ArgumentList @('--convert', "`"$src`"", '--out', "`"$dst`"", '--fit', $fit, '--width', $W, '--height', $H, '--result', "`"$r`"") -Wait -PassThru
    @{ Code = $p.ExitCode; Text = (Get-Content $r -Raw) }
}
function PyCheck() { $o = & $Python -I (Join-Path $PSScriptRoot 'pycheck.py') @args; @{ Code = $LASTEXITCODE; Text = ($o -join ' ') } }

foreach ($case in 'noise', 'checker', 'noise16', 'rgba_opaque', 'rgba_partial', 'gamma', 'icc', 'grey', 'palette') {
    $dst = Join-Path $res "$case.png"
    $c = Convert-Image (Join-Path $data "$case.png") 'exact' $dst
    if ($c.Code -ne 0) { Report $case $false ($c.Text -replace "`r?`n", ' '); continue }
    $enc = ([regex]::Match($c.Text, 'encodercheck=(.*)')).Groups[1].Value.Trim()
    $d = PyCheck diff $dst (Join-Path $data "expected_$case.png")
    Report $case ($d.Code -eq 0) ("Pillow: " + $d.Text + "; app self-check: " + $enc)
}

$c = Convert-Image (Join-Path $data 'square.png') 'exact' (Join-Path $res 'refused.png')
Report 'off-size refused (lossless)' ($c.Code -ne 0 -and $c.Text -match 'screen') (([regex]::Match($c.Text, 'message=(.*)')).Groups[1].Value.Trim())

$c = Convert-Image (Join-Path $data 'square.png') 'crop' (Join-Path $res 'crop.png')
Report 'crop to fill' ($c.Code -eq 0 -and $c.Text -match "width=$W" -and $c.Text -match "height=$H") "exit $($c.Code), $W x $H expected"

$c = Convert-Image (Join-Path $data 'square_grey200.png') 'fit' (Join-Path $res 'fit.png')
$x0 = [int](($W - $H) / 2)
$d = PyCheck bars (Join-Path $res 'fit.png') $x0 ($x0 + $H)
Report 'fit with bars' ($c.Code -eq 0 -and $d.Code -eq 0) $d.Text

$c = Convert-Image (Join-Path $data 'photo.jpg') 'exact' (Join-Path $res 'photo.png')
$d = PyCheck diff (Join-Path $res 'photo.png') (Join-Path $data 'photo.jpg')
Report 'JPEG input (info)' ($c.Code -eq 0) ("Windows decoder vs Pillow decoder: " + $d.Text)

$c = Convert-Image (Join-Path $data 'not_an_image.png') 'exact' (Join-Path $res 'x.png')
Report 'non-image refused' ($c.Code -ne 0 -and $c.Text -match 'Not a recognised image') (([regex]::Match($c.Text, 'message=(.*)')).Groups[1].Value.Trim())

$c = Convert-Image (Join-Path $data 'animated.gif') 'exact' (Join-Path $res 'y.png')
Report 'animated GIF refused' ($c.Code -ne 0 -and $c.Text -match 'Animated') (([regex]::Match($c.Text, 'message=(.*)')).Groups[1].Value.Trim())

foreach ($o in 2..8) {
    $w2 = if ($o -ge 5) { 40 } else { 64 }; $h2 = if ($o -ge 5) { 64 } else { 40 }
    $dst = Join-Path $res "orient$o.png"
    $r = Join-Path $res "orient$o.result.txt"
    $p = Start-Process $exe -ArgumentList @('--convert', "`"$(Join-Path $data "orient$o.jpg")`"", '--out', "`"$dst`"", '--fit', 'exact', '--width', $w2, '--height', $h2, '--result', "`"$r`"") -Wait -PassThru
    if ($p.ExitCode -ne 0) { Report "orientation $o" $false ((Get-Content $r -Raw) -replace "`r?`n", ' '); continue }
    $d = PyCheck diff $dst (Join-Path $data "expected_orient$o.png")
    Report "orientation tag $o" ($d.Code -eq 0) ("vs Pillow exif_transpose: " + $d.Text)
}

$r = Join-Path $res 'cmyk.result.txt'
Start-Process $exe -ArgumentList @('--convert', "`"$(Join-Path $data 'cmyk.jpg')`"", '--out', "`"$(Join-Path $res 'cmyk.png')`"", '--fit', 'exact', '--width', 64, '--height', 40, '--result', "`"$r`"") -Wait
$t = Get-Content $r -Raw
Report 'CMYK labelled converted' ($t -match 'note=Converted from') (([regex]::Match($t, 'note=(Converted[^\r\n]*)')).Groups[1].Value)

$d = PyCheck chunks (Join-Path $res 'noise.png')
Report 'output PNG has no extra chunks' ($d.Text -eq 'IHDR IDAT IEND') $d.Text

if ($script:fails -gt 0) { "$script:fails test(s) failed"; exit 1 } else { "All core tests passed" }
