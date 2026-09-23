# Draw the Thunderstore icon from simple, editable shapes.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$radioRoot = Split-Path $PSScriptRoot -Parent
$radioLarge = [Drawing.Bitmap]::new(1024, 1024)
$radioSmall = [Drawing.Bitmap]::new(256, 256)
$radioGraphics = [Drawing.Graphics]::FromImage($radioLarge)
$radioSmallGraphics = [Drawing.Graphics]::FromImage($radioSmall)
$radioCabinet = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#E8D3A8'))
$radioCone = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#171A1C'))
$radioCenter = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#2A2D2F'))
$radioEdge = [Drawing.Pen]::new([Drawing.ColorTranslator]::FromHtml('#A98A5D'), 2)
$radioRing = [Drawing.Pen]::new([Drawing.ColorTranslator]::FromHtml('#594631'), 5)
try {
    $radioGraphics.Clear([Drawing.ColorTranslator]::FromHtml('#1C3441'))
    $radioGraphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $radioGraphics.ScaleTransform(4, 4)

    $radioGraphics.FillRectangle($radioCabinet, 69, 36, 118, 184)
    $radioGraphics.DrawRectangle($radioEdge, 69, 36, 118, 184)
    $radioGraphics.FillEllipse($radioCone, 109, 60, 38, 38)
    $radioGraphics.DrawEllipse($radioRing, 109, 60, 38, 38)
    $radioGraphics.FillEllipse($radioCone, 82, 113, 92, 92)
    $radioGraphics.DrawEllipse($radioRing, 82, 113, 92, 92)
    $radioGraphics.FillEllipse($radioCenter, 105, 136, 46, 46)

    $radioSmallGraphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $radioSmallGraphics.DrawImage($radioLarge, [Drawing.Rectangle]::new(0, 0, 256, 256))
    $radioOutput = Join-Path $radioRoot 'icon.png'
    $radioSmall.Save($radioOutput, [Drawing.Imaging.ImageFormat]::Png)

    # System.Drawing adds sRGB/gAMA/pHYs chunks. Keep only image chunks in the
    # public icon, without altering their pixels or CRC values.
    $radioBytes = [IO.File]::ReadAllBytes($radioOutput)
    $radioClean = [IO.MemoryStream]::new()
    try {
        $radioClean.Write($radioBytes, 0, 8)
        $radioOffset = 8
        while ($radioOffset -lt $radioBytes.Length) {
            $radioLength = [Net.IPAddress]::NetworkToHostOrder([BitConverter]::ToInt32($radioBytes, $radioOffset))
            $radioType = [Text.Encoding]::ASCII.GetString($radioBytes, $radioOffset + 4, 4)
            if ($radioLength -lt 0 -or $radioOffset + $radioLength + 12 -gt $radioBytes.Length) {
                throw 'Generated icon has an invalid PNG chunk.'
            }
            if ($radioType -in @('IHDR', 'PLTE', 'tRNS', 'IDAT', 'IEND')) {
                $radioClean.Write($radioBytes, $radioOffset, $radioLength + 12)
            }
            $radioOffset += $radioLength + 12
        }
        [IO.File]::WriteAllBytes($radioOutput, $radioClean.ToArray())
    }
    finally { $radioClean.Dispose() }
}
finally {
    $radioRing.Dispose()
    $radioEdge.Dispose()
    $radioCenter.Dispose()
    $radioCone.Dispose()
    $radioCabinet.Dispose()
    $radioSmallGraphics.Dispose()
    $radioGraphics.Dispose()
    $radioSmall.Dispose()
    $radioLarge.Dispose()
}
