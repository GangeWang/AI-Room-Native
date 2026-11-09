# Regenerates AIROOMApp\Assets\airoom.ico from the same upstream artwork the
# macOS app builds its icon from (Sources/AIROOMNative/Resources/airoom.png).
# Sizes up to 64 are stored as 32bpp BMP entries (with an all-zero AND mask —
# the alpha channel carries the shape); 128 and 256 are stored as PNG entries.
# The .ico is committed, so this script only needs to run when the source art
# changes. Requires Windows PowerShell (System.Drawing).
# Run:  powershell -NoProfile -ExecutionPolicy Bypass -File make-ico.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$source = Join-Path $PSScriptRoot '..\..\macos\Sources\AIROOMNative\Resources\airoom.png'
$output = Join-Path $PSScriptRoot '..\AIROOMApp\Assets\airoom.ico'
$sizes = @(16, 24, 32, 48, 64, 128, 256)

$image = [System.Drawing.Image]::FromFile((Resolve-Path $source))

function DrawScaled([System.Drawing.Image]$src, [int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($src, 0, 0, $size, $size)
    $g.Dispose()
    return $bmp
}

function PngEntry([System.Drawing.Image]$src, [int]$size) {
    $bmp = DrawScaled $src $size
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return ,$ms.ToArray()
}

function BmpEntry([System.Drawing.Image]$src, [int]$size) {
    $bmp = DrawScaled $src $size
    $rect = New-Object System.Drawing.Rectangle(0, 0, $size, $size)
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $raw = New-Object byte[] ($size * $size * 4)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $raw, 0, $raw.Length)
    $bmp.UnlockBits($data)
    $bmp.Dispose()
    # ICO bitmaps run bottom-up; the memory copy is top-down. ARGB ints are
    # little-endian, so the byte order is already the BGRA the format wants.
    $stride = $size * 4
    $xor = New-Object byte[] ($size * $size * 4)
    for ($y = 0; $y -lt $size; $y++) {
        [Array]::Copy($raw, $y * $stride, $xor, ($size - 1 - $y) * $stride, $stride)
    }
    # 1bpp AND mask, rows padded to 32 bits: all opaque (alpha decides).
    $maskStride = [int][Math]::Ceiling($size / 32.0) * 4
    $mask = New-Object byte[] ($maskStride * $size)

    $header = New-Object byte[] 40
    [System.BitConverter]::GetBytes([int]40).CopyTo($header, 0)            # biSize
    [System.BitConverter]::GetBytes([int]$size).CopyTo($header, 4)         # biWidth
    [System.BitConverter]::GetBytes([int]($size * 2)).CopyTo($header, 8)   # biHeight (XOR + AND)
    [System.BitConverter]::GetBytes([int16]1).CopyTo($header, 12)          # biPlanes
    [System.BitConverter]::GetBytes([int16]32).CopyTo($header, 14)         # biBitCount

    $ms = New-Object System.IO.MemoryStream
    $ms.Write($header, 0, 40)
    $ms.Write($xor, 0, $xor.Length)
    $ms.Write($mask, 0, $mask.Length)
    return ,$ms.ToArray()
}

$entries = @()
foreach ($size in $sizes) {
    if ($size -le 64) { $entries += ,@{ Size = $size; Bytes = (BmpEntry $image $size) } }
    else { $entries += ,@{ Size = $size; Bytes = (PngEntry $image $size) }
    }
}

$writer = New-Object System.IO.BinaryWriter([System.IO.File]::Create($output))
$writer.Write([uint16]0)                      # reserved
$writer.Write([uint16]1)                      # type: icon
$writer.Write([uint16]$entries.Count)
$offset = 6 + 16 * $entries.Count
foreach ($entry in $entries) {
    $dim = 0
    if ($entry.Size -lt 256) { $dim = $entry.Size }
    $writer.Write([byte]$dim)                 # 0 means 256
    $writer.Write([byte]$dim)
    $writer.Write([byte]0)                    # palette count
    $writer.Write([byte]0)                    # reserved
    $writer.Write([uint16]1)                  # color planes
    $writer.Write([uint16]32)                 # bits per pixel
    $writer.Write([uint32]$entry.Bytes.Length)
    $writer.Write([uint32]$offset)
    $offset += $entry.Bytes.Length
}
foreach ($entry in $entries) { $writer.Write([byte[]]$entry.Bytes) }
$writer.Close()
$image.Dispose()
Write-Host ("Wrote {0} ({1} sizes: {2})" -f $output, $entries.Count, ($sizes -join ', '))
