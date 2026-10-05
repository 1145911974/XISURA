param([Parameter(Mandatory)][string]$Executable)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$appProcess = Start-Process -FilePath (Resolve-Path $Executable) -PassThru
try {
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        Start-Sleep -Milliseconds 100
        $appProcess.Refresh()
    } while ($appProcess.MainWindowHandle -eq [IntPtr]::Zero -and -not $appProcess.HasExited -and [DateTime]::UtcNow -lt $deadline)
    if ($appProcess.HasExited -or $appProcess.MainWindowHandle -eq [IntPtr]::Zero) { throw 'Application window not available.' }

    $root = [System.Windows.Automation.AutomationElement]::FromHandle($appProcess.MainWindowHandle)
    function Find-ById([string]$id) {
        $condition = [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
            $id)
        $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    }

    $logo = Find-ById 'HeroView.Logo'
    $performance = Find-ById 'HeroView.Performance'
    $logoVisual = Find-ById 'HeroLogoVisual'
    if ($null -eq $logo) { throw 'HeroView.Logo not found.' }
    if ($null -eq $performance) { throw 'HeroView.Performance not found.' }
    if ($null -eq $logoVisual) { throw 'HeroLogoVisual not found.' }

    $logoBounds = $logo.Current.BoundingRectangle
    $performanceBounds = $performance.Current.BoundingRectangle
    $logoVisualBounds = $logoVisual.Current.BoundingRectangle
    if ([Math]::Abs($logoBounds.Width - $performanceBounds.Width) -gt 1) {
        throw "Unequal widths: Logo=$($logoBounds.Width), Performance=$($performanceBounds.Width)."
    }
    if ([Math]::Abs($logoBounds.Height - $performanceBounds.Height) -gt 1) {
        throw "Unequal heights: Logo=$($logoBounds.Height), Performance=$($performanceBounds.Height)."
    }
    if ($logoBounds.Width -lt 44 -or $logoBounds.Height -lt 44) {
        throw "Hit target below 44 DIP: $($logoBounds.Width)x$($logoBounds.Height)."
    }

    ([System.Windows.Automation.InvokePattern]$performance.GetCurrentPattern(
        [System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
    Start-Sleep -Milliseconds 250
    $radar = Find-ById 'PerformanceRadar'
    if ($null -eq $radar) { throw 'PerformanceRadar not found after switching views.' }
    $radarBounds = $radar.Current.BoundingRectangle
    foreach ($edge in 'X', 'Y', 'Width', 'Height') {
        if ([Math]::Abs($logoVisualBounds.$edge - $radarBounds.$edge) -gt 1) {
            throw "Performance radar does not share the Logo frame: $edge Logo=$($logoVisualBounds.$edge), Radar=$($radarBounds.$edge)."
        }
    }
    if ($radar.Current.Name -notmatch 'CPU 42.*GPU 38.*散热 34.*响应 48.*静音 88') {
        throw "Unexpected office radar values: $($radar.Current.Name)"
    }

    [pscustomobject]@{
        LogoWidth = $logoBounds.Width
        PerformanceWidth = $performanceBounds.Width
        Height = $logoBounds.Height
        RadarFrame = "$($radarBounds.Width)x$($radarBounds.Height)"
        RadarValues = $radar.Current.Name
    } | Format-List
} finally {
    if ($null -ne $appProcess -and -not $appProcess.HasExited) {
        $appProcess.CloseMainWindow() | Out-Null
        Start-Sleep -Milliseconds 300
    }
    if ($null -ne $appProcess -and -not $appProcess.HasExited) { $appProcess.Kill() }
}
