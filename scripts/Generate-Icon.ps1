Add-Type -AssemblyName System.Drawing

$outputPath = Join-Path $PSScriptRoot 'app.ico'
$bitmap = New-Object System.Drawing.Bitmap 256, 256
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.Clear([System.Drawing.Color]::Transparent)

$background = New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml('#0F0E12'))
$plum = New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml('#2E1E38'))
$accent = New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml('#F2FF59'))
$silver = New-Object System.Drawing.Pen ([System.Drawing.ColorTranslator]::FromHtml('#E8E6E1')), 10

$rounded = New-Object System.Drawing.Drawing2D.GraphicsPath
$rounded.AddArc(8, 8, 56, 56, 180, 90)
$rounded.AddArc(192, 8, 56, 56, 270, 90)
$rounded.AddArc(192, 192, 56, 56, 0, 90)
$rounded.AddArc(8, 192, 56, 56, 90, 90)
$rounded.CloseFigure()
$graphics.FillPath($background, $rounded)

$graphics.FillEllipse($plum, 54, 42, 148, 148)
$graphics.FillRectangle($accent, 112, 54, 32, 96)
$arrow = [System.Drawing.Point[]]@(
    (New-Object System.Drawing.Point 76, 132),
    (New-Object System.Drawing.Point 180, 132),
    (New-Object System.Drawing.Point 128, 186)
)
$graphics.FillPolygon($accent, $arrow)
$graphics.DrawLine($silver, 66, 202, 190, 202)

$pngStream = New-Object System.IO.MemoryStream
$bitmap.Save($pngStream, [System.Drawing.Imaging.ImageFormat]::Png)
$pngBytes = $pngStream.ToArray()

$fileStream = [System.IO.File]::Open($outputPath, [System.IO.FileMode]::Create)
$writer = New-Object System.IO.BinaryWriter $fileStream
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
$writer.Dispose()
$fileStream.Dispose()

$pngStream.Dispose()
$silver.Dispose()
$accent.Dispose()
$plum.Dispose()
$background.Dispose()
$rounded.Dispose()
$graphics.Dispose()
$bitmap.Dispose()

Write-Output $outputPath
