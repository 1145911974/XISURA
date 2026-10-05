param([Parameter(Mandatory)][string]$Executable)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$app = Start-Process -FilePath (Resolve-Path $Executable) -PassThru
try {
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do { Start-Sleep -Milliseconds 100; $app.Refresh() }
    while ($app.MainWindowHandle -eq [IntPtr]::Zero -and -not $app.HasExited -and [DateTime]::UtcNow -lt $deadline)
    if ($app.HasExited -or $app.MainWindowHandle -eq [IntPtr]::Zero) { throw 'Application window not available.' }

    $root = [System.Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle)
    function Find-ById([string]$id) {
        $condition = [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
            $id)
        $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    }
    function Invoke-ById([string]$id) {
        $element = Find-ById $id
        if ($null -eq $element) { throw "$id not found." }
        ([System.Windows.Automation.InvokePattern]$element.GetCurrentPattern(
            [System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
    }
    function Get-RadarValues([string]$name) {
        return @([regex]::Matches($name, '\d+') | ForEach-Object { [int]$_.Value })
    }
    function Matches-RadarValues([string]$name, [int[]]$expected) {
        $actual = @(Get-RadarValues $name)
        return $actual.Count -eq $expected.Count -and -not (Compare-Object $actual $expected -SyncWindow 0)
    }
    function Wait-ForRadar([int[]]$expected) {
        $until = [DateTime]::UtcNow.AddSeconds(2)
        do {
            $radar = Find-ById 'PerformanceRadar'
            if ($null -ne $radar -and (Matches-RadarValues $radar.Current.Name $expected)) { return $radar.Current.Name }
            Start-Sleep -Milliseconds 15
        } while ([DateTime]::UtcNow -lt $until)
        throw "Radar did not reach values: $($expected -join ',')"
    }
    function Wait-ForIntermediate([string]$from, [int[]]$target) {
        $until = [DateTime]::UtcNow.AddMilliseconds(300)
        do {
            $radar = Find-ById 'PerformanceRadar'
            if ($null -ne $radar -and $radar.Current.Name -ne $from -and -not (Matches-RadarValues $radar.Current.Name $target)) { return $radar.Current.Name }
            Start-Sleep -Milliseconds 10
        } while ([DateTime]::UtcNow -lt $until)
        throw 'Radar skipped the required intermediate frame.'
    }

    Invoke-ById 'HeroView.Performance'
    $officeValues = @(42, 38, 34, 48, 88)
    $turboValues = @(88, 92, 86, 94, 28)
    $quietValues = @(76, 82, 58, 80, 68)
    $office = Wait-ForRadar $officeValues

    Invoke-ById 'Mode.Turbo'
    $modeIntermediate = Wait-ForIntermediate $office $turboValues
    $turbo = Wait-ForRadar $turboValues
    Invoke-ById 'Turbo.Quiet'
    $tierIntermediate = Wait-ForIntermediate $turbo $quietValues
    $quiet = Wait-ForRadar $quietValues

    [pscustomobject]@{
        ModeIntermediate = $modeIntermediate
        TierIntermediate = $tierIntermediate
        Final = $quiet
    } | Format-List
} finally {
    if ($null -ne $app -and -not $app.HasExited) { $app.CloseMainWindow() | Out-Null; Start-Sleep -Milliseconds 300 }
    if ($null -ne $app -and -not $app.HasExited) { $app.Kill() }
}
