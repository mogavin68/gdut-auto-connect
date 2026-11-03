param(
    [string]$InputPng = (Join-Path (Split-Path $PSScriptRoot -Parent) 'assets\app-icon.png'),
    [string]$OutputIco = (Join-Path (Split-Path $PSScriptRoot -Parent) 'assets\app.ico')
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing.Common
$source = [System.Drawing.Bitmap]::new($InputPng)
$sizes = @(256, 128, 64, 48, 32, 16)
$frames = [System.Collections.Generic.List[byte[]]]::new()
try {
    foreach ($size in $sizes) {
        $bitmap = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $stream = [System.IO.MemoryStream]::new()
        try {
            $graphics.Clear([System.Drawing.Color]::Transparent)
            $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.DrawImage($source, 0, 0, $size, $size)
            $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
            $frames.Add($stream.ToArray())
        } finally { $stream.Dispose(); $graphics.Dispose(); $bitmap.Dispose() }
    }
    $output = [System.IO.MemoryStream]::new()
    $writer = [System.IO.BinaryWriter]::new($output)
    try {
        $writer.Write([UInt16]0); $writer.Write([UInt16]1); $writer.Write([UInt16]$sizes.Count)
        $offset = 6 + 16 * $sizes.Count
        for ($index = 0; $index -lt $sizes.Count; $index++) {
            $dimension = if ($sizes[$index] -eq 256) { 0 } else { $sizes[$index] }
            $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
            $writer.Write([byte]0); $writer.Write([byte]0)
            $writer.Write([UInt16]1); $writer.Write([UInt16]32)
            $writer.Write([UInt32]$frames[$index].Length); $writer.Write([UInt32]$offset)
            $offset += $frames[$index].Length
        }
        foreach ($frame in $frames) { $writer.Write($frame) }
        $writer.Flush()
        [System.IO.File]::WriteAllBytes($OutputIco, $output.ToArray())
    } finally { $writer.Dispose(); $output.Dispose() }
} finally { $source.Dispose() }
foreach ($size in @(16, 32, 48, 128)) {
    $testIcon = [System.Drawing.Icon]::new($OutputIco, $size, $size)
    try {
        if ($testIcon.Width -ne $size -or $testIcon.Height -ne $size) { throw 'ICO size validation failed.' }
    } finally { $testIcon.Dispose() }
}
Write-Output 'ICO created and validated: 16/32/48/64/128/256 px.'
