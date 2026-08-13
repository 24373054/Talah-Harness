[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\src\Talah.Harness.App\Assets')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$resolvedOutput = [System.IO.Path]::GetFullPath($OutputDirectory)
[System.IO.Directory]::CreateDirectory($resolvedOutput) | Out-Null

function New-BrandBitmap {
    param([int]$Size)

    $bitmap = [System.Drawing.Bitmap]::new($Size, $Size)
    $bitmap.SetResolution(96, 96)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.Clear([System.Drawing.Color]::Transparent)

    $scale = $Size / 256.0
    $rect = [System.Drawing.RectangleF]::new(8 * $scale, 8 * $scale, 240 * $scale, 240 * $scale)
    $radius = 48 * $scale
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $path.AddArc($rect.X, $rect.Y, $radius, $radius, 180, 90)
    $path.AddArc($rect.Right - $radius, $rect.Y, $radius, $radius, 270, 90)
    $path.AddArc($rect.Right - $radius, $rect.Bottom - $radius, $radius, $radius, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $radius, $radius, $radius, 90, 90)
    $path.CloseFigure()

    $background = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        $rect,
        [System.Drawing.ColorTranslator]::FromHtml('#080645'),
        [System.Drawing.ColorTranslator]::FromHtml('#162230'),
        32.0)
    $graphics.FillPath($background, $path)

    $gridPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(24, 98, 199, 229), [Math]::Max(1, $scale))
    for ($i = 32; $i -le 224; $i += 24) {
        $graphics.DrawLine($gridPen, $i * $scale, 24 * $scale, $i * $scale, 232 * $scale)
        $graphics.DrawLine($gridPen, 24 * $scale, $i * $scale, 232 * $scale, $i * $scale)
    }

    $dotColors = @('#62C7E5', '#4BAFD6', '#2C83B6', '#115A82', '#080645')
    for ($row = 0; $row -lt 5; $row++) {
        for ($column = 0; $column -lt 5; $column++) {
            $dotSize = 8 * $scale
            $x = (41 + ($column * 14)) * $scale
            $y = (63 + ($row * 14)) * $scale
            $brush = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml($dotColors[$column]))
            $graphics.FillEllipse($brush, $x, $y, $dotSize, $dotSize)
            $brush.Dispose()
        }
    }

    $tracePen = [System.Drawing.Pen]::new([System.Drawing.ColorTranslator]::FromHtml('#62C7E5'), [Math]::Max(3, 7 * $scale))
    $tracePen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $tracePen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $graphics.DrawLine($tracePen, 78 * $scale, 156 * $scale, 218 * $scale, 156 * $scale)

    $nodeBrush = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#F4F7FA'))
    foreach ($x in @(91, 145, 207)) {
        $graphics.FillEllipse($nodeBrush, ($x - 6) * $scale, 150 * $scale, 12 * $scale, 12 * $scale)
    }

    $gatePen = [System.Drawing.Pen]::new([System.Drawing.ColorTranslator]::FromHtml('#D8B956'), [Math]::Max(3, 8 * $scale))
    $gatePen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $gatePen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $graphics.DrawLine($gatePen, 174 * $scale, 133 * $scale, 174 * $scale, 179 * $scale)

    $gatePen.Dispose()
    $nodeBrush.Dispose()
    $tracePen.Dispose()
    $gridPen.Dispose()
    $background.Dispose()
    $path.Dispose()
    $graphics.Dispose()
    return $bitmap
}

$sizes = @(44, 150, 256)
foreach ($size in $sizes) {
    $bitmap = New-BrandBitmap -Size $size
    $fileName = if ($size -eq 44) { 'Square44x44Logo.png' } elseif ($size -eq 150) { 'Square150x150Logo.png' } else { 'app-256.png' }
    $bitmap.Save((Join-Path $resolvedOutput $fileName), [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
}

# ICO supports a PNG-compressed 256x256 frame. Build the small ICONDIR and
# ICONDIRENTRY deterministically, then append the generated PNG payload.
$pngPath = Join-Path $resolvedOutput 'app-256.png'
$pngBytes = [System.IO.File]::ReadAllBytes($pngPath)
$stream = [System.IO.MemoryStream]::new()
$writer = [System.IO.BinaryWriter]::new($stream)
$writer.Write([uint16]0)
$writer.Write([uint16]1)
$writer.Write([uint16]1)
$writer.Write([byte]0)
$writer.Write([byte]0)
$writer.Write([byte]0)
$writer.Write([byte]0)
$writer.Write([uint16]1)
$writer.Write([uint16]32)
$writer.Write([uint32]$pngBytes.Length)
$writer.Write([uint32]22)
$writer.Write($pngBytes)
$writer.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $resolvedOutput 'app.ico'), $stream.ToArray())
$writer.Dispose()
$stream.Dispose()

Write-Host "Generated brand assets in $resolvedOutput"

