param(
    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$Path
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing.Common
$resolvedPath = (Resolve-Path -LiteralPath $Path).Path
$bitmap = [System.Drawing.Bitmap]::new($resolvedPath)

try {
    if (($bitmap.PixelFormat -band [System.Drawing.Imaging.PixelFormat]::Alpha) -eq 0 -and
        ($bitmap.PixelFormat -band [System.Drawing.Imaging.PixelFormat]::PAlpha) -eq 0) {
        throw "PNG has no alpha channel: $resolvedPath"
    }

    $minX = $bitmap.Width
    $minY = $bitmap.Height
    $maxX = -1
    $maxY = -1
    $edgeAlphaPixels = 0L
    $transparentRgbPollution = 0L

    for ($y = 0; $y -lt $bitmap.Height; $y++) {
        for ($x = 0; $x -lt $bitmap.Width; $x++) {
            $pixel = $bitmap.GetPixel($x, $y)
            if ($pixel.A -gt 0) {
                if ($x -lt $minX) { $minX = $x }
                if ($y -lt $minY) { $minY = $y }
                if ($x -gt $maxX) { $maxX = $x }
                if ($y -gt $maxY) { $maxY = $y }
                if ($pixel.A -lt 255) { $edgeAlphaPixels++ }
            }
            elseif ($pixel.R -ne 0 -or $pixel.G -ne 0 -or $pixel.B -ne 0) {
                $transparentRgbPollution++
            }
        }
    }

    if ($maxX -lt 0) { throw "PNG is fully transparent: $resolvedPath" }
    $rightMargin = $bitmap.Width - 1 - $maxX
    $bottomMargin = $bitmap.Height - 1 - $maxY
    $margins = @($minX, $minY, $rightMargin, $bottomMargin)
    if (($margins | Measure-Object -Maximum).Maximum -gt 2) {
        throw "Transparent padding exceeds 2 px: $($margins -join ', ')"
    }
    if ($transparentRgbPollution -gt 0) {
        throw "Fully transparent pixels contain RGB data: $transparentRgbPollution"
    }

    [pscustomobject]@{
        Path = $resolvedPath
        Width = $bitmap.Width
        Height = $bitmap.Height
        Bounds = "$minX,$minY,$maxX,$maxY"
        EdgeAlphaPixels = $edgeAlphaPixels
        TransparentRgbPollution = $transparentRgbPollution
        Result = 'PASS'
    } | ConvertTo-Json -Compress
}
finally {
    $bitmap.Dispose()
}
