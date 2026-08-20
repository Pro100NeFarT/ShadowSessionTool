Add-Type -AssemblyName System.Drawing

function New-RoundedRectPath {
    param($x, $y, $w, $h, $r)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

$size = 256
$bmp = New-Object System.Drawing.Bitmap $size, $size
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.Clear([System.Drawing.Color]::Transparent)

$backPath = New-RoundedRectPath 78 66 148 108 20
$backBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 140, 140, 152))
$g.FillPath($backBrush, $backPath)

$frontPath = New-RoundedRectPath 30 40 150 110 20
$frontBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 37, 99, 235))
$g.FillPath($frontBrush, $frontPath)
$frontPen = New-Object System.Drawing.Pen(([System.Drawing.Color]::FromArgb(255, 29, 78, 216)), 5)
$g.DrawPath($frontPen, $frontPath)

$cx = 105
$cy = 95
$whiteBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
$g.FillEllipse($whiteBrush, $cx - 26, $cy - 26, 52, 52)
$irisBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 29, 78, 216))
$g.FillEllipse($irisBrush, $cx - 12, $cy - 12, 24, 24)

$g.Flush()

function Get-DibBytes {
    param([System.Drawing.Bitmap]$b)

    $w = $b.Width
    $h = $b.Height
    $rect = New-Object System.Drawing.Rectangle 0, 0, $w, $h
    $bmpData = $b.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)

    $stride = $bmpData.Stride
    $xorSize = $stride * $h
    $xorBytes = New-Object byte[] $xorSize
    [System.Runtime.InteropServices.Marshal]::Copy($bmpData.Scan0, $xorBytes, 0, $xorSize)
    $b.UnlockBits($bmpData)

    # BMP rows are bottom-up; LockBits already returns top-down, so reverse row order
    $flipped = New-Object byte[] $xorSize
    for ($row = 0; $row -lt $h; $row++) {
        [Array]::Copy($xorBytes, $row * $stride, $flipped, ($h - 1 - $row) * $stride, $stride)
    }

    # AND mask: 1bpp, rows padded to 4-byte boundary, all zero (fully opaque via alpha channel)
    $maskStride = [Math]::Ceiling($w / 32.0) * 4
    $maskBytes = New-Object byte[] ($maskStride * $h)

    $headerSize = 40
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter $ms
    $bw.Write([UInt32]$headerSize)
    $bw.Write([Int32]$w)
    $bw.Write([Int32]($h * 2))
    $bw.Write([UInt16]1)
    $bw.Write([UInt16]32)
    $bw.Write([UInt32]0)
    $bw.Write([UInt32]($xorSize + $maskBytes.Length))
    $bw.Write([Int32]0)
    $bw.Write([Int32]0)
    $bw.Write([UInt32]0)
    $bw.Write([UInt32]0)
    $bw.Write($flipped)
    $bw.Write($maskBytes)
    $bw.Flush()
    return $ms.ToArray()
}

$sizes = 256, 48, 32, 16
$frames = @()
foreach ($s in $sizes) {
    if ($s -eq $size) {
        $frames += , ([byte[]](Get-DibBytes $bmp))
    }
    else {
        $small = New-Object System.Drawing.Bitmap $s, $s
        $gs = [System.Drawing.Graphics]::FromImage($small)
        $gs.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $gs.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $gs.Clear([System.Drawing.Color]::Transparent)
        $gs.DrawImage($bmp, 0, 0, $s, $s)
        $gs.Dispose()
        $frames += , ([byte[]](Get-DibBytes $small))
        $small.Dispose()
    }
}

$outPath = 'D:\Desktop\ShadowSessionTool\ShadowSessionTool.ico'
$fs = [System.IO.File]::Open($outPath, [System.IO.FileMode]::Create)
$bw = New-Object System.IO.BinaryWriter $fs

$count = $sizes.Count
$bw.Write([UInt16]0)
$bw.Write([UInt16]1)
$bw.Write([UInt16]$count)

$headerSize = 6
$entrySize = 16
$offset = $headerSize + ($entrySize * $count)

for ($i = 0; $i -lt $count; $i++) {
    $s = $sizes[$i]
    $frameBytes = $frames[$i]
    $wByte = if ($s -ge 256) { 0 } else { $s }
    $hByte = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$wByte)
    $bw.Write([byte]$hByte)
    $bw.Write([byte]0)
    $bw.Write([byte]0)
    $bw.Write([UInt16]1)
    $bw.Write([UInt16]32)
    $bw.Write([UInt32]$frameBytes.Length)
    $bw.Write([UInt32]$offset)
    $offset += $frameBytes.Length
}

foreach ($f in $frames) {
    $bw.Write([byte[]]$f)
}

$bw.Flush()
$bw.Close()
$fs.Close()

$g.Dispose()
$bmp.Dispose()
Write-Output "Icon saved: $outPath"
