param(
    [string]$Source = (Join-Path $PSScriptRoot '../assets/icon/source.png'),
    [string]$Destination = (Join-Path $PSScriptRoot '../assets/icon/app.ico')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$sourcePath = [IO.Path]::GetFullPath($Source)
$destinationPath = [IO.Path]::GetFullPath($Destination)
if ($sourcePath -eq $destinationPath) { throw 'Source and destination must be different.' }
$image = [Drawing.Bitmap]::new($sourcePath)
$frames = [Collections.Generic.List[object]]::new()
try {
    foreach ($size in @(16, 20, 24, 32, 40, 48, 64, 128, 256)) {
        $bitmap = [Drawing.Bitmap]::new($size, $size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        $memory = [IO.MemoryStream]::new()
        try {
            $graphics.Clear([Drawing.Color]::Transparent)
            $graphics.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceCopy
            $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $scale = [Math]::Min($size / [double]$image.Width, $size / [double]$image.Height)
            $width = [single]($image.Width * $scale)
            $height = [single]($image.Height * $scale)
            $rectangle = [Drawing.RectangleF]::new([single](($size - $width) / 2), [single](($size - $height) / 2), $width, $height)
            $graphics.DrawImage($image, $rectangle)
            $bitmap.Save($memory, [Drawing.Imaging.ImageFormat]::Png)
            $frames.Add([PSCustomObject]@{ Size = $size; Bytes = $memory.ToArray() })
        } finally { $memory.Dispose(); $graphics.Dispose(); $bitmap.Dispose() }
    }
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destinationPath)) | Out-Null
    $stream = [IO.File]::Create($destinationPath)
    $writer = [IO.BinaryWriter]::new($stream)
    try {
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]$frames.Count)
        $offset = 6 + 16 * $frames.Count
        foreach ($frame in $frames) {
            $dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
            $writer.Write([byte]$dimension)
            $writer.Write([byte]$dimension)
            $writer.Write([byte]0)
            $writer.Write([byte]0)
            $writer.Write([uint16]1)
            $writer.Write([uint16]32)
            $writer.Write([uint32]$frame.Bytes.Length)
            $writer.Write([uint32]$offset)
            $offset += $frame.Bytes.Length
        }
        foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Bytes) }
    } finally { $writer.Dispose(); $stream.Dispose() }
} finally { $image.Dispose() }
Write-Output "Created: $destinationPath (16-256px, transparent PNG frames)"
