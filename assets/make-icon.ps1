# Generates the app icon and the tray-state icons from code, so they're reproducible
# and tweakable without a binary editor. Run with Windows PowerShell:
#   powershell.exe -ExecutionPolicy Bypass -File assets\make-icon.ps1
#
# Outputs (all in this folder):
#   app.ico              — teal→green gradient (taskbar / window / exe)
#   icon-256.png         — 256px PNG for README / docs
#   tray-connected.ico   — white bell on green   (connection healthy)
#   tray-degraded.ico    — white bell on amber    (reconnecting)
#   tray-disconnected.ico— white bell on red      (down)
#
# Design: white notification bell (Material Design "notifications" glyph, Apache-2.0)
# on a rounded square. The app icon's gradient is sampled from ntfy's own logo palette
# so the icon reads as part of the ntfy family; the bell keeps it distinct from the
# official terminal/speech-bubble mark. Tray variants swap the background to encode
# connection state (matching the title-bar pip colours).

Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase, System.Xaml
$ErrorActionPreference = 'Stop'

# Material Design "notifications" icon (24x24 viewBox). Bell body + clapper as one
# cohesive glyph — no detached pieces.
$bellPath = 'M12 22c1.1 0 2-.9 2-2h-4c0 1.1.9 2 2 2zm6-6v-5c0-3.07-1.63-5.64-4.5-6.32V4c0-.83-.67-1.5-1.5-1.5s-1.5.67-1.5 1.5v.68C7.64 5.36 6 7.92 6 11v5l-2 2v1h16v-1l-2-2z'

function New-Master([System.Windows.Media.Brush]$bgBrush, [double]$target = 178.0, [double]$inset = 8.0, [double]$radius = 48.0) {
    $dv = New-Object System.Windows.Media.DrawingVisual
    $dc = $dv.RenderOpen()

    $bgSize = 256.0 - 2 * $inset
    $rect = New-Object System.Windows.Rect($inset, $inset, $bgSize, $bgSize)
    $dc.DrawRoundedRectangle($bgBrush, $null, $rect, $radius, $radius)

    # Bell: parse the glyph, then scale + centre it into the canvas.
    $geo = [System.Windows.Media.Geometry]::Parse($bellPath)
    $b = $geo.Bounds
    $scale = $target / [Math]::Max($b.Width, $b.Height)
    $sw = $b.Width * $scale
    $sh = $b.Height * $scale

    $m = New-Object System.Windows.Media.Matrix
    $m.Translate(-$b.X, -$b.Y)
    $m.Scale($scale, $scale)
    $m.Translate((256 - $sw) / 2.0, (256 - $sh) / 2.0)

    # Geometry.Parse returns a frozen geometry, so push the transform on the
    # drawing context rather than setting geo.Transform.
    $white = New-Object System.Windows.Media.SolidColorBrush([System.Windows.Media.Colors]::White)
    $white.Freeze()
    $dc.PushTransform((New-Object System.Windows.Media.MatrixTransform($m)))
    $dc.DrawGeometry($white, $null, $geo)
    $dc.Pop()

    $dc.Close()

    $rtb = New-Object System.Windows.Media.Imaging.RenderTargetBitmap(256, 256, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $rtb.Render($dv)
    $rtb.Freeze()
    return $rtb
}

function Get-PngBytes($master, [int]$size) {
    if ($size -eq 256) {
        $src = $master
    }
    else {
        $s = $size / 256.0
        $src = New-Object System.Windows.Media.Imaging.TransformedBitmap($master, (New-Object System.Windows.Media.ScaleTransform($s, $s)))
    }
    $enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($src))
    $ms = New-Object System.IO.MemoryStream
    $enc.Save($ms)
    return $ms.ToArray()
}

function Save-Ico($master, [int[]]$sizes, [string]$path) {
    $blobs = @()
    foreach ($s in $sizes) { $blobs += , (Get-PngBytes $master $s) }

    $fs = New-Object System.IO.FileStream($path, [System.IO.FileMode]::Create)
    $bw = New-Object System.IO.BinaryWriter($fs)
    $bw.Write([UInt16]0)              # reserved
    $bw.Write([UInt16]1)              # type = icon
    $bw.Write([UInt16]$sizes.Count)   # image count

    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $s = $sizes[$i]; $data = $blobs[$i]
        $dim = if ($s -ge 256) { 0 } else { $s }   # 0 means 256 in the ICO dir
        $bw.Write([Byte]$dim)        # width
        $bw.Write([Byte]$dim)        # height
        $bw.Write([Byte]0)           # palette count
        $bw.Write([Byte]0)           # reserved
        $bw.Write([UInt16]1)         # planes
        $bw.Write([UInt16]32)        # bits per pixel
        $bw.Write([UInt32]$data.Length)
        $bw.Write([UInt32]$offset)
        $offset += $data.Length
    }
    # Explicit (buffer, index, count) overload — the single-arg Write($byte[])
    # mis-resolves in PowerShell and writes almost nothing.
    foreach ($data in $blobs) { $bw.Write($data, 0, $data.Length) }
    $bw.Flush(); $bw.Close(); $fs.Close()
}

function Solid([byte]$r, [byte]$g, [byte]$b) {
    $brush = New-Object System.Windows.Media.SolidColorBrush([System.Windows.Media.Color]::FromRgb($r, $g, $b))
    $brush.Freeze()
    return $brush
}

# ---- app icon: teal → green diagonal gradient (ntfy palette) ----
$c1 = [System.Windows.Media.Color]::FromRgb(0x36, 0x8C, 0x7C)  # #368C7C
$c2 = [System.Windows.Media.Color]::FromRgb(0x4F, 0xB7, 0xA2)  # #4FB7A2
$gradient = New-Object System.Windows.Media.LinearGradientBrush($c1, $c2, 45.0)
$gradient.Freeze()

$appMaster = New-Master $gradient
$appSizes = 16, 24, 32, 48, 64, 128, 256
Save-Ico $appMaster $appSizes (Join-Path $PSScriptRoot 'app.ico')
[System.IO.File]::WriteAllBytes((Join-Path $PSScriptRoot 'icon-256.png'), (Get-PngBytes $appMaster 256))

# ---- tray state icons: solid backgrounds matching the title-bar pip palette ----
# Bigger bell + less padding than the app icon, since the tray renders at ~16px.
$traySizes = 16, 24, 32, 48
$trayTarget = 206.0; $trayInset = 4.0; $trayRadius = 40.0
Save-Ico (New-Master (Solid 0x16 0xA3 0x4A) $trayTarget $trayInset $trayRadius) $traySizes (Join-Path $PSScriptRoot 'tray-connected.ico')    # green
Save-Ico (New-Master (Solid 0xEA 0x58 0x0C) $trayTarget $trayInset $trayRadius) $traySizes (Join-Path $PSScriptRoot 'tray-degraded.ico')     # orange
Save-Ico (New-Master (Solid 0xDC 0x26 0x26) $trayTarget $trayInset $trayRadius) $traySizes (Join-Path $PSScriptRoot 'tray-disconnected.ico') # red

Write-Output "Wrote app.ico, icon-256.png, and 3 tray-*.ico into $PSScriptRoot"
