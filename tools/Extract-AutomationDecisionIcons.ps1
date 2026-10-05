param(
    [string]$Reference = "$PSScriptRoot/../docs/design/automation/automation-page-16x10-v2-preview.png",
    [string]$OutputDirectory = "$PSScriptRoot/../src/Jiaolong.ControlCenter/Assets/Automation",
    [string]$PreviewPath
)

Add-Type -AssemblyName System.Drawing
$null = New-Item -ItemType Directory -Path $OutputDirectory -Force
$source = [System.Drawing.Bitmap]::new((Resolve-Path -LiteralPath $Reference).Path)
# These rectangles contain only reference strokes, excluding labels and card edges.
$regions = @(
    @{ Name = 'DecisionArrow'; X = 559; Y = 291; Width = 34; Height = 32 },
    @{ Name = 'DecisionPower'; X = 1136; Y = 268; Width = 32; Height = 34 },
    @{ Name = 'DecisionCpu'; X = 1238; Y = 268; Width = 32; Height = 34 },
    @{ Name = 'DecisionGpu'; X = 1340; Y = 268; Width = 32; Height = 34 },
    @{ Name = 'DecisionThermal'; X = 1451; Y = 268; Width = 32; Height = 34 },
    @{ Name = 'StrategyQuiet'; X = 411; Y = 480; Width = 52; Height = 52 },
    @{ Name = 'StrategyBalanced'; X = 808; Y = 479; Width = 52; Height = 52 },
    @{ Name = 'StrategyResponse'; X = 1203; Y = 478; Width = 44; Height = 54 },
    @{ Name = 'TargetOffice'; X = 363; Y = 712; Width = 36; Height = 34 },
    @{ Name = 'TargetGame'; X = 360; Y = 776; Width = 42; Height = 34 },
    @{ Name = 'TargetTurbo'; X = 359; Y = 835; Width = 44; Height = 34 },
    @{ Name = 'ConditionReturn'; X = 973; Y = 823; Width = 34; Height = 35 },
    @{ Name = 'RulesDocument'; X = 969; Y = 886; Width = 29; Height = 32 },
    @{ Name = 'InlineInfo'; X = 343; Y = 898; Width = 30; Height = 31 },
    @{ Name = 'SelectorChevron'; X = 681; Y = 717; Width = 24; Height = 22 }
)
try {
    foreach ($region in $regions) {
        $result = [System.Drawing.Bitmap]::new($region.Width, $region.Height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $visiblePixels = 0
        try {
            for ($y = 0; $y -lt $region.Height; $y++) {
                # Estimate the smooth dark background using this row's outer columns.
                $left = $source.GetPixel($region.X, $region.Y + $y)
                $right = $source.GetPixel($region.X + $region.Width - 1, $region.Y + $y)
                for ($x = 0; $x -lt $region.Width; $x++) {
                    $pixel = $source.GetPixel($region.X + $x, $region.Y + $y)
                    $t = $x / ($region.Width - 1.0)
                    $background = (1 - $t) * ($left.R + $left.G + $left.B) / 3.0 + $t * ($right.R + $right.G + $right.B) / 3.0
                    $signal = ($pixel.R + $pixel.G + $pixel.B) / 3.0 - $background
                    $alpha = if ($signal -le 9) { 0 } else { [int][Math]::Clamp(255 * $signal / (240 - $background), 0, 255) }
                    if ($alpha -gt 0) { $visiblePixels++ }
                    # Neutral RGB removes source tint; alpha retains the original antialiasing.
                    $result.SetPixel($x, $y, [System.Drawing.Color]::FromArgb($alpha, 240, 240, 240))
                }
            }
            if ($visiblePixels -lt 15) { throw "No usable strokes found in $($region.Name)." }
            for ($x = 0; $x -lt $region.Width; $x++) {
                if ($result.GetPixel($x, 0).A -ne 0 -or $result.GetPixel($x, $region.Height - 1).A -ne 0) { throw "Clipped edge in $($region.Name)." }
            }
            $result.Save((Join-Path $OutputDirectory "$($region.Name).png"), [System.Drawing.Imaging.ImageFormat]::Png)
            [pscustomobject]@{ Name = $region.Name; VisiblePixels = $visiblePixels; Format = 'Neutral RGB + transparent alpha' }
        }
        finally { $result.Dispose() }
    }
}
finally { $source.Dispose() }

if ($PreviewPath) {
    $rows = [int][Math]::Ceiling($regions.Count / 5.0)
    $sheet = [System.Drawing.Bitmap]::new(800, 200 * $rows)
    $graphics = [System.Drawing.Graphics]::FromImage($sheet)
    try {
        $graphics.Clear([System.Drawing.Color]::FromArgb(18, 20, 25))
        for ($row = 0; $row -lt $rows; $row++) {
            $graphics.FillRectangle([System.Drawing.Brushes]::LightSlateGray, 0, 100 + 200 * $row, 800, 100)
        }
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
        for ($i = 0; $i -lt $regions.Count; $i++) {
            $icon = [System.Drawing.Bitmap]::new((Join-Path $OutputDirectory "$($regions[$i].Name).png"))
            try {
                $x = 30 + 160 * ($i % 5)
                $y = 200 * [int][Math]::Floor($i / 5.0)
                $scale = [Math]::Min(2.5, 90.0 / $icon.Height)
                $graphics.DrawImage($icon, [int]$x, [int]($y + 4), [int]($icon.Width * $scale), [int]($icon.Height * $scale))
                $graphics.DrawImage($icon, [int]$x, [int]($y + 104), [int]($icon.Width * $scale), [int]($icon.Height * $scale))
            }
            finally { $icon.Dispose() }
        }
        $sheet.Save($PreviewPath, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally { $graphics.Dispose(); $sheet.Dispose() }
}
