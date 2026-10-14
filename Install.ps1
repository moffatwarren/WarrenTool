<#
.SYNOPSIS
    Installs (or uninstalls) WarrenTool - Screen Shot and Wallpaper Picker in one tray app - for the current user.
.DESCRIPTION
    Install:
      - Removes the separate Screen Shot and Wallpaper Picker apps if they are installed
        (their "Start with Windows" setting carries over to WarrenTool)
      - Compiles src\*.cs with the C# compiler that ships with Windows (.NET Framework 4.x)
        into %LOCALAPPDATA%\WarrenTool, along with Set-LockScreen.ps1
      - Adds "Screen Shot" and "Wallpaper Picker" Start Menu shortcuts
      - Creates <Pictures>\Wallpapers if needed, with a starter image if it has no images
      - Turns on "Start with Windows" on a first install; afterwards keeps whatever was chosen in the tray menu
      - Starts the app in the tray
    Uninstall removes the app folder, the shortcuts and the startup entry. Saved screenshots and wallpapers are left alone.
.PARAMETER Uninstall
    Remove WarrenTool instead of installing it.
#>

[CmdletBinding()]
param([switch]$Uninstall)

$ErrorActionPreference = 'Stop'

$appName     = 'WarrenTool'
$installDir  = Join-Path $env:LOCALAPPDATA 'WarrenTool'
$exePath     = Join-Path $installDir 'WarrenTool.exe'
$programs    = [Environment]::GetFolderPath('Programs')
$shotLink    = Join-Path $programs 'Screen Shot.lnk'
$pickerLink  = Join-Path $programs 'Wallpaper Picker.lnk'
$runKey      = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$runValue    = 'WarrenTool'   # must match TrayApp.RunValue in src\WarrenTool.cs
$wallpapers  = Join-Path ([Environment]::GetFolderPath('MyPictures')) 'Wallpapers'
$src         = Join-Path $PSScriptRoot 'src'

# An exe can't be overwritten or deleted while it's running
function Stop-App([string]$name) {
    Get-Process $name -ErrorAction SilentlyContinue | ForEach-Object {
        $_ | Stop-Process -Force
        $_.WaitForExit(5000) | Out-Null
    }
}

if ($Uninstall) {
    Stop-App WarrenTool
    Remove-ItemProperty -Path $runKey -Name $runValue -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $shotLink, $pickerLink -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $installDir -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "[+] $appName uninstalled. Saved screenshots and your wallpapers in $wallpapers were not touched." -ForegroundColor Green
    return
}

# ---------- Icons ----------

function New-Rgb([int]$rgb) {
    [System.Drawing.Color]::FromArgb(255, ($rgb -shr 16) -band 0xFF, ($rgb -shr 8) -band 0xFF, $rgb -band 0xFF)
}

# Screen Shot (and, with -Cards, WarrenTool): viewfinder brackets on a Catppuccin Macchiato tile, around
# a red pen dot - or around Wallpaper Picker's three slanted cards
function Draw-ViewfinderArt($g, [int]$size, [switch]$Cards) {
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'Half'

    # Whole-pixel metrics: tile margin, bracket inset, bracket thickness, bracket arm length, dot diameter.
    # The tray sizes are set by hand; scaling the large artwork down would leave them blurry.
    switch ($size) {
        16      { $m = 0; $i = 2; $t = 2; $a = 5; $d = 4 }
        20      { $m = 0; $i = 3; $t = 2; $a = 6; $d = 4 }
        24      { $m = 0; $i = 4; $t = 2; $a = 7; $d = 4 }
        default {
            $u = $size / 32.0
            $m = [int][math]::Round(1 * $u); $i = [int][math]::Round(6.5 * $u); $t = [int][math]::Round(2.75 * $u)
            $a = [int][math]::Round(8.5 * $u); $d = [int][math]::Round(6 * $u)
            if (($d % 2) -ne 0) { $d++ }
        }
    }

    # Tile (base)
    $w = $size - 2 * $m
    $r = [single]($w * 0.23)
    $tile = New-Object System.Drawing.Drawing2D.GraphicsPath
    $tile.AddArc($m, $m, 2 * $r, 2 * $r, 180, 90)
    $tile.AddArc($m + $w - 2 * $r, $m, 2 * $r, 2 * $r, 270, 90)
    $tile.AddArc($m + $w - 2 * $r, $m + $w - 2 * $r, 2 * $r, 2 * $r, 0, 90)
    $tile.AddArc($m, $m + $w - 2 * $r, 2 * $r, 2 * $r, 90, 90)
    $tile.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush (New-Rgb 0x24273A)), $tile)

    # Brackets (mauve to blue)
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush (
        (New-Object System.Drawing.PointF $m, $m), (New-Object System.Drawing.PointF ($m + $w), ($m + $w)),
        (New-Rgb 0xC6A0F6), (New-Rgb 0x8AADF4))
    $pen = New-Object System.Drawing.Pen $grad, $t
    $pen.LineJoin = 'Round'
    $rounded = $size -ge 64      # round ends only where there are enough pixels to show them
    if ($rounded) { $pen.StartCap = 'Round'; $pen.EndCap = 'Round' }
    $lo = [single]($i + $t / 2.0); $hi = [single]($size - $i - $t / 2.0)
    $reach = if ($rounded) { $a - $t } else { $a - $t / 2.0 }
    foreach ($c in @(@($lo, $lo, 1, 1), @($hi, $lo, -1, 1), @($hi, $hi, -1, -1), @($lo, $hi, 1, -1))) {
        $x = [single]$c[0]; $y = [single]$c[1]
        $g.DrawLines($pen, [System.Drawing.PointF[]]@(
            (New-Object System.Drawing.PointF $x, ([single]($y + $c[3] * $reach))),
            (New-Object System.Drawing.PointF $x, $y),
            (New-Object System.Drawing.PointF ([single]($x + $c[2] * $reach)), $y)))
    }

    if (-not $Cards) {
        # Pen dot (red)
        $o = [single](($size - $d) / 2.0)
        $g.FillEllipse((New-Object System.Drawing.SolidBrush (New-Rgb 0xED8796)), $o, $o, $d, $d)
        return
    }

    # Cards (text, peach, text): Wallpaper Picker's artwork, shrunk into the space between the brackets
    $inner = $size - 2 * ($i + $t)
    $k = $inner * 0.82 / 32.0
    $side = New-Object System.Drawing.SolidBrush (New-Rgb 0xCAD3F5)
    $accent = New-Object System.Drawing.SolidBrush (New-Rgb 0xF5A97F)
    $state = $g.Save()
    $g.TranslateTransform([single]($size / 2.0), [single]($size / 2.0))
    $g.ScaleTransform([single]$k, [single]$k)
    $g.TranslateTransform(-16, -16)
    Draw-Cards $g $side $accent
    $g.Restore($state)
}

# Wallpaper Picker's three slanted cards on a 32x32 grid
function Draw-Cards($g, $side, $accent) {
    foreach ($card in @(@(0, $side), @(10.5, $accent), @(21, $side))) {
        $x = [single]$card[0]
        $g.FillPolygon($card[1], [System.Drawing.PointF[]]@(
            (New-Object System.Drawing.PointF ($x + 5), 3),
            (New-Object System.Drawing.PointF ($x + 11), 3),
            (New-Object System.Drawing.PointF ($x + 6), 29),
            (New-Object System.Drawing.PointF $x, 29)))
    }
}

# Wallpaper Picker: three slanted cards, the middle one in the accent color
function Draw-WallpaperArt($g, [int]$size) {
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.ScaleTransform($size / 32.0, $size / 32.0)   # artwork is designed on a 32x32 grid
    # Catppuccin Mocha text and peach, as in the picker
    $side = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0xCD, 0xD6, 0xF4))
    $accent = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0xFA, 0xB3, 0x87))
    Draw-Cards $g $side $accent
}

# Writes a multi-size .ico, drawing each size with $draw ($g, $size)
function New-IconFile([string]$path, [scriptblock]$draw) {
    Add-Type -AssemblyName System.Drawing
    $sizes = 16, 20, 24, 32, 40, 48, 64, 256

    $images = foreach ($size in $sizes) {
        $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        & $draw $g $size
        $g.Dispose()

        $ms = New-Object System.IO.MemoryStream
        if ($size -ge 256) {
            $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        } else {
            # Classic 32bpp DIB entry (best compatibility for small sizes)
            $w = New-Object System.IO.BinaryWriter $ms
            $w.Write([int]40); $w.Write([int]$size); $w.Write([int]($size * 2)); $w.Write([int16]1); $w.Write([int16]32)
            $w.Write((New-Object byte[] 24))
            for ($y = $size - 1; $y -ge 0; $y--) {          # bottom-up BGRA
                for ($x = 0; $x -lt $size; $x++) {
                    $c = $bmp.GetPixel($x, $y)
                    $w.Write([byte]$c.B); $w.Write([byte]$c.G); $w.Write([byte]$c.R); $w.Write([byte]$c.A)
                }
            }
            $w.Write((New-Object byte[] ([math]::Ceiling($size / 32.0) * 4 * $size)))  # AND mask, unused
            $w.Flush()
        }
        $bmp.Dispose()
        , $ms.ToArray()
    }

    $w = New-Object System.IO.BinaryWriter ([System.IO.File]::Create($path))
    $w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
        $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([int16]0)
        $w.Write([int16]1); $w.Write([int16]32)
        $w.Write([int]$images[$i].Length); $w.Write([int]$offset)
        $offset += $images[$i].Length
    }
    foreach ($img in $images) { $w.Write([byte[]]$img) }
    $w.Close()
}

# ---------- Replace the separate Screen Shot and Wallpaper Picker apps ----------

$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$csc = Join-Path $fw 'csc.exe'
if (-not (Test-Path $csc)) { throw ".NET Framework 4 compiler not found at $csc" }

# Either one starting with Windows means WarrenTool should. Their Start Menu shortcuts are
# replaced below, under the same names.
$firstInstall = -not (Test-Path $exePath)
$startup = [bool](Get-ItemProperty -Path $runKey -Name $runValue -ErrorAction SilentlyContinue)
foreach ($old in 'ScreenShot', 'WallpaperPicker') {
    $oldDir = Join-Path $env:LOCALAPPDATA $old
    $oldRun = [bool](Get-ItemProperty -Path $runKey -Name $old -ErrorAction SilentlyContinue)
    if (-not ($oldRun -or (Test-Path $oldDir))) { continue }
    Stop-App $old
    if ($oldRun) { $startup = $true; Remove-ItemProperty -Path $runKey -Name $old }
    Remove-Item -LiteralPath $oldDir -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "[+] Removed the separate $old app (now part of $appName)" -ForegroundColor Green
}

# ---------- Build ----------

Stop-App WarrenTool
New-Item -ItemType Directory -Force -Path $installDir | Out-Null

# Kept next to the exe so the Start Menu shortcuts can point at them directly
$appIcon = Join-Path $installDir 'WarrenTool.ico'
$shotIcon = Join-Path $installDir 'ScreenShot.ico'
$pickerIcon = Join-Path $installDir 'WallpaperPicker.ico'
New-IconFile $appIcon { param($g, $size) Draw-ViewfinderArt $g $size -Cards }
New-IconFile $shotIcon { param($g, $size) Draw-ViewfinderArt $g $size }
New-IconFile $pickerIcon { param($g, $size) Draw-WallpaperArt $g $size }

$wpf = Join-Path $fw 'WPF'
# Windows' own API metadata, for the built-in OCR (Windows.Media.Ocr) behind Screen Shot's Copy text tool
$winrt = Join-Path $env:WINDIR 'System32\WinMetadata'
$winrtRefs = 'Foundation', 'Globalization', 'Graphics', 'Media', 'Storage' | ForEach-Object { "/r:$winrt\Windows.$_.winmd" }
# ZXing.Net reads QR codes for the same tool. Embedded in the exe (and loaded from there by
# Program.LoadEmbedded), so nothing extra is installed next to it.
$zxing = Join-Path $PSScriptRoot 'lib\zxing.dll'
# WarrenTool.ico is the tray icon; ScreenShot.ico is the screenshot editor's window icon
& $csc /nologo /target:winexe /optimize+ "/out:$exePath" `
    "/win32icon:$appIcon" "/resource:$appIcon,WarrenTool.ico" "/resource:$shotIcon,ScreenShot.ico" `
    "/win32manifest:$(Join-Path $src 'app.manifest')" `
    "/r:$wpf\PresentationCore.dll" "/r:$wpf\PresentationFramework.dll" "/r:$wpf\WindowsBase.dll" `
    /r:System.Xaml.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll `
    "/r:$fw\System.Runtime.dll" $winrtRefs `
    "/r:$zxing" "/resource:$zxing,zxing.dll" `
    (Join-Path $src 'WarrenTool.cs') (Join-Path $src 'ScreenShot.cs') (Join-Path $src 'WallpaperPicker.cs')
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

# The picker calls this to set the Lock Screen; it must sit next to the exe
Copy-Item -LiteralPath (Join-Path $src 'Set-LockScreen.ps1') -Destination $installDir -Force
Write-Host "[+] Installed to $installDir" -ForegroundColor Green

# ---------- Start Menu shortcuts ----------

$shell = New-Object -ComObject WScript.Shell
foreach ($s in @(
        @($shotLink, '--screenshot', 'Capture and mark up a region of the screen (Ctrl+Alt+S)', $shotIcon),
        @($pickerLink, '--wallpaper', 'Browse and set wallpapers (Ctrl+Alt+W)', $pickerIcon))) {
    $lnk = $shell.CreateShortcut($s[0])
    $lnk.TargetPath = $exePath
    $lnk.Arguments = $s[1]
    $lnk.WorkingDirectory = $installDir
    $lnk.Description = $s[2]
    $lnk.IconLocation = "$($s[3]),0"
    $lnk.Save()
}
# Start Menu search caches icons; ask Windows to refresh so it doesn't keep a generic one
& (Join-Path $env:WINDIR 'System32\ie4uinit.exe') -show
Write-Host '[+] Start Menu shortcuts: Screen Shot, Wallpaper Picker' -ForegroundColor Green

# ---------- Start with Windows ----------

if ($firstInstall -or $startup) {
    Set-ItemProperty -Path $runKey -Name $runValue -Value "`"$exePath`" --tray"
    Write-Host '[+] Starts with Windows (untick it in the tray menu to stop)' -ForegroundColor Green
}

# ---------- Wallpapers folder ----------

New-Item -ItemType Directory -Force -Path $wallpapers | Out-Null
$existing = @(Get-ChildItem -LiteralPath $wallpapers -File |
    Where-Object { $_.Extension -match '^\.(jpg|jpeg|png|bmp|webp)$' })
if ($existing.Count -eq 0) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'assets\Lowpoly_Street.png') -Destination $wallpapers
    Write-Host "[+] Added a starter wallpaper to $wallpapers" -ForegroundColor Green
} else {
    Write-Host "[+] Wallpaper folder: $wallpapers ($($existing.Count) images)" -ForegroundColor Green
}

# ---------- Launch ----------

# Start in the tray: the installer's console holds the focus, so a window shown now would lose it right away
Start-Process -FilePath $exePath -ArgumentList '--tray'
Write-Host "[+] $appName is running in the tray. Ctrl+Alt+S takes a screenshot, Ctrl+Alt+W opens the wallpaper picker." -ForegroundColor Green
