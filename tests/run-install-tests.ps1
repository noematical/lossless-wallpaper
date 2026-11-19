# Installer tests on the real system. CHANGES the wallpaper (and, with -LockScreen, the lock screen)
# for a short time and then undoes it through the app's own undo. Run run-core-tests.ps1 first.
#   powershell -ExecutionPolicy Bypass -File tests\run-install-tests.ps1 [-Python path] [-LockScreen]
param([string]$Python = "python", [switch]$LockScreen)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $root 'out\LosslessWallpaper.exe'
$data = Join-Path $PSScriptRoot 'data'
$res = Join-Path $data 'out'
$trans = Join-Path $env:APPDATA 'Microsoft\Windows\Themes\TranscodedWallpaper'
$desk = 'HKCU:\Control Panel\Desktop'
$hist = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Wallpapers'
New-Item -ItemType Directory -Force $res | Out-Null
$script:fails = 0
function Report($name, $pass, $detail) {
    $tag = if ($pass) { 'PASS' } else { 'FAIL'; $script:fails++ }
    "{0,-4} {1,-30} {2}" -f $tag, $name, $detail
}
function Run-App([string[]]$a) {
    $r = Join-Path $res ('run-' + [guid]::NewGuid().ToString('N') + '.txt')
    $p = Start-Process $exe -ArgumentList (($a + @('--result', $r)) | ForEach-Object { "`"$_`"" }) -Wait -PassThru
    $t = Get-Content $r -Raw; Remove-Item $r
    @{ Code = $p.ExitCode; Text = $t }
}
function Val($text, $key) { ([regex]::Match($text, "(?m)^$key=(.*)$")).Groups[1].Value.Trim() }
function PyDiff($a, $b) { $o = & $Python -I (Join-Path $PSScriptRoot 'pycheck.py') diff $a $b; @{ Code = $LASTEXITCODE; Text = ($o -join ' ') } }

$before = Get-ItemProperty $desk
$beforeWp = $before.WallPaper
$beforeTrans = Join-Path $res 'before-transcoded.png'
Copy-Item $trans $beforeTrans -Force

# 1. Install exact-size noise as wallpaper.
$r = Run-App @('--install', (Join-Path $data 'noise.png'), '--target', 'wallpaper', '--fit', 'exact')
$file = Val $r.Text 'file'
Report 'install wallpaper' ($r.Code -eq 0 -and (Val $r.Text 'pass') -eq 'true') (Val $r.Text 'check')
$d = PyDiff $trans (Join-Path $data 'expected_noise.png')
Report 'TranscodedWallpaper (Pillow)' ($d.Code -eq 0) $d.Text
Report 'mode label' ((Val $r.Text 'mode') -eq 'lossless') (Val $r.Text 'mode')

# 2. Re-apply the same image: same id, still passes, and no new backup (nothing changed).
$r2 = Run-App @('--install', $file, '--target', 'wallpaper', '--fit', 'exact')
Report 're-apply (duplicate)' ((Val $r2.Text 'id') -eq (Val $r.Text 'id') -and (Val $r2.Text 'pass') -eq 'true') ("id " + (Val $r2.Text 'id'))
Report 're-apply makes no backup' (-not ($r2.Text -match '(?m)^snapshot=')) ''

# 3. One undo -> back to the wallpaper from before the test.
$u1 = Run-App @('--undo')
Start-Sleep 2
$after = Get-ItemProperty $desk
Report 'undo restores registry' ($after.WallPaper -eq $beforeWp -and $after.WallpaperStyle -eq $before.WallpaperStyle -and $after.TileWallpaper -eq $before.TileWallpaper) ("WallPaper = " + $after.WallPaper)
$d = PyDiff $trans $beforeTrans
Report 'undo restores pixels' ($d.Code -eq 0) $d.Text

# 4. "Restore what I had before this app": the first backup ever taken is kept and restores the start state.
$orig = Join-Path $env:LOCALAPPDATA 'LosslessWallpaper\undo\original'
Report 'original backup kept' (Test-Path (Join-Path $orig 'state.ini')) $orig
$r = Run-App @('--install', (Join-Path $data 'checker.png'), '--target', 'wallpaper', '--fit', 'exact')
$tmpFile = Val $r.Text 'file'
$o = Run-App @('--undo', '--original')
$origWp = ([regex]::Match((Get-Content (Join-Path $orig 'state.ini') -Raw), '(?m)^WallPaper=(.*)$')).Groups[1].Value.Trim()
Report 'restore original' ((Val $o.Text 'complete') -eq 'true' -and (Get-ItemProperty $desk).WallPaper -eq $origWp -and (Test-Path $orig)) ("WallPaper = " + (Get-ItemProperty $desk).WallPaper)
Run-App @('--undo') | Out-Null    # drop the checker install's own backup
if ($tmpFile -and (Test-Path $tmpFile) -and -not ((Get-ItemProperty $desk).WallPaper -ieq $tmpFile)) { Remove-Item $tmpFile }

# 5. Solid-colour background (no picture) is restored by undo.
Add-Type -Name SPI -Namespace LwTest -MemberDefinition '[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern bool SystemParametersInfo(int a,int b,string c,int d);'
[LwTest.SPI]::SystemParametersInfo(0x14, 0, '', 3) | Out-Null
$r = Run-App @('--install', (Join-Path $data 'noise.png'), '--target', 'wallpaper', '--fit', 'exact')
$u = Run-App @('--undo')
Report 'undo to solid colour' ((Val $u.Text 'complete') -eq 'true' -and (Get-ItemProperty $desk).WallPaper -eq '') (($u.Text -split "`n" | Where-Object { $_ -match '^line=' }) -join '; ')
[LwTest.SPI]::SystemParametersInfo(0x14, 0, $beforeWp, 3) | Out-Null
Start-Sleep 2
$d = PyDiff $trans $beforeTrans
Report 'start wallpaper back' ($d.Code -eq 0 -and (Get-ItemProperty $desk).WallPaper -eq $beforeWp) $d.Text

# 6. Save for later (target none): the checked PNG is kept, nothing on screen changes, no backup.
$snapsBefore = @(Get-ChildItem (Join-Path $env:LOCALAPPDATA 'LosslessWallpaper\undo') -Directory -ErrorAction SilentlyContinue).Count
$transBefore = (Get-FileHash $trans).Hash
$s = Run-App @('--install', (Join-Path $data 'checker.png'), '--target', 'none', '--fit', 'exact')
$savedFile = Val $s.Text 'file'
$transAfter = (Get-FileHash $trans).Hash
$snapsAfter = @(Get-ChildItem (Join-Path $env:LOCALAPPDATA 'LosslessWallpaper\undo') -Directory -ErrorAction SilentlyContinue).Count
Report 'save for later' ($s.Code -eq 0 -and (Val $s.Text 'target') -eq 'none' -and $savedFile -and (Test-Path $savedFile) -and -not ($s.Text -match '(?m)^(snapshot|check)=')) $savedFile
Report 'save changes nothing on screen' ((Get-ItemProperty $desk).WallPaper -eq $beforeWp -and $transAfter -eq $transBefore -and $snapsAfter -eq $snapsBefore) ("backups " + $snapsBefore + " -> " + $snapsAfter)
$d = PyDiff $savedFile (Join-Path $data 'expected_checker.png')
Report 'saved copy is exact (Pillow)' ($d.Code -eq 0) $d.Text
if ($savedFile -and (Test-Path $savedFile) -and -not ((Get-ItemProperty $desk).WallPaper -ieq $savedFile)) { Remove-Item $savedFile }

# 7. Lock screen round trip (optional).
if ($LockScreen) {
    $r = Run-App @('--install', (Join-Path $data 'noise.png'), '--target', 'lockscreen', '--fit', 'exact')
    Report 'install lock screen' ($r.Code -eq 0 -and ((Val $r.Text 'pass') -eq 'true' -or (Val $r.Text 'pending') -eq 'true')) (Val $r.Text 'check')
    $u = Run-App @('--undo')
    Report 'undo lock screen' ($u.Code -eq 0 -and $u.Text -match 'lock screen restored') (($u.Text -split "`n" | Where-Object { $_ -match '^line=' }) -join '; ')
}

# Remove the test image's installed copy (only if nothing uses it now).
if ($file -and (Test-Path $file) -and -not ((Get-ItemProperty $desk).WallPaper -ieq $file)) { Remove-Item $file }

if ($script:fails -gt 0) { "$script:fails test(s) failed"; exit 1 } else { "All install tests passed" }
