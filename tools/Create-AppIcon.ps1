Add-Type -AssemblyName System.Drawing

$srcPath = "D:\NEXLauncher\tools\nex-icon-src.jpg"
$icoPath = "D:\NEXLauncher\Assets\nex-icon.ico"
$pngPath = "D:\NEXLauncher\tools\nex-icon-256.png"

$src = [System.Drawing.Bitmap]::FromFile($srcPath)
$w = $src.Width; $h = $src.Height

# Sample dark navy background color from top area of the tile
$bg = $src.GetPixel([int]($w * 0.35), [int]($h * 0.06))

$bmp = New-Object System.Drawing.Bitmap($w, $h, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.Clear($bg)

# Clip to a rounded rectangle so white corners become background color
$radius = [int]($w * 0.19)
$d = $radius * 2
$path = New-Object System.Drawing.Drawing2D.GraphicsPath
$path.AddArc(0, 0, $d, $d, 180, 90)
$path.AddArc($w - $d, 0, $d, $d, 270, 90)
$path.AddArc($w - $d, $h - $d, $d, $d, 0, 90)
$path.AddArc(0, $h - $d, $d, $d, 90, 90)
$path.CloseFigure()
$g.SetClip($path)
$g.DrawImage($src, 0, 0, $w, $h)
$g.ResetClip()

# Cover the bottom-right watermark with background color
$brush = New-Object System.Drawing.SolidBrush($bg)
$g.FillRectangle($brush, [int]($w * 0.70), [int]($h * 0.84), [int]($w * 0.30), [int]($h * 0.16))
$g.Dispose()

# Render each icon size to PNG bytes
$sizes = @(16, 24, 32, 48, 64, 128, 256)
$pngDataList = @()
foreach ($size in $sizes) {
    $resized = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g2 = [System.Drawing.Graphics]::FromImage($resized)
    $g2.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g2.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g2.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g2.DrawImage($bmp, 0, 0, $size, $size)
    $g2.Dispose()
    $ms2 = New-Object System.IO.MemoryStream
    $resized.Save($ms2, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngDataList += ,$ms2.ToArray()
    $resized.Dispose()
}

# Export 256px PNG preview
[System.IO.File]::WriteAllBytes($pngPath, $pngDataList[-1])

# Assemble ICO container (PNG-compressed entries, Vista+)
$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($ms)
$bw.Write([uint16]0)
$bw.Write([uint16]1)
$bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $size = $sizes[$i]
    $data = $pngDataList[$i]
    $sizeByte = [byte]0
    if ($size -lt 256) { $sizeByte = [byte]$size }
    $bw.Write($sizeByte)   # width, 0 means 256
    $bw.Write($sizeByte)   # height
    $bw.Write([byte]0)     # color count
    $bw.Write([byte]0)     # reserved
    $bw.Write([uint16]1)   # planes
    $bw.Write([uint16]32)  # bit count
    $bw.Write([uint32]$data.Length)
    $bw.Write([uint32]$offset)
    $offset += $data.Length
}
foreach ($data in $pngDataList) { $bw.Write($data) }
$bw.Flush()
[System.IO.File]::WriteAllBytes($icoPath, $ms.ToArray())
$bw.Dispose(); $ms.Dispose(); $bmp.Dispose(); $src.Dispose()

Write-Host ("ICO written: " + $icoPath + " (" + (Get-Item $icoPath).Length + " bytes)")
