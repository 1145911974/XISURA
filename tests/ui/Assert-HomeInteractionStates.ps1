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
    function Find-ByProperty($property, [string]$value) {
        $condition = [System.Windows.Automation.PropertyCondition]::new($property, $value)
        $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    }
    function Find-ById([string]$id) {
        Find-ByProperty ([System.Windows.Automation.AutomationElement]::AutomationIdProperty) $id
    }
    function Invoke-Mode([string]$id) {
        $element = Find-ById $id
        if ($null -eq $element) { throw "$id not found." }
        ([System.Windows.Automation.InvokePattern]$element.GetCurrentPattern(
            [System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
    }
    function Wait-ForToggleState($expected) {
        $limit = [DateTime]::UtcNow.AddSeconds(2)
        do {
            $element = Find-ById 'StrongCooling'
            if ($null -ne $element) {
                $pattern = [System.Windows.Automation.TogglePattern]$element.GetCurrentPattern(
                    [System.Windows.Automation.TogglePattern]::Pattern)
                if ($pattern.Current.ToggleState -eq $expected) { return $element }
            }
            Start-Sleep -Milliseconds 50
        } while ([DateTime]::UtcNow -lt $limit)
        throw "StrongCooling did not reach $expected."
    }
    function Wait-ForItemStatus([string]$id, [string]$expected) {
        $limit = [DateTime]::UtcNow.AddSeconds(2)
        $actual = '<missing>'
        do {
            $element = Find-ById $id
            if ($null -ne $element) {
                $actual = $element.Current.ItemStatus
                if ($actual -eq $expected) { return }
            }
            Start-Sleep -Milliseconds 50
        } while ([DateTime]::UtcNow -lt $limit)
        throw "$id did not reach item status $expected; actual '$actual'."
    }

    Invoke-Mode 'Mode.Turbo'
    Start-Sleep -Milliseconds 90
    Invoke-Mode 'Mode.Custom'
    Start-Sleep -Milliseconds 90
    Invoke-Mode 'Mode.Office'
    Start-Sleep -Milliseconds 1000
    if ($appProcess.HasExited) { throw 'Application exited during rapid mode switching.' }

    Invoke-Mode 'Mode.Turbo'
    Start-Sleep -Milliseconds 500
    Invoke-Mode 'Turbo.Quiet'
    Wait-ForItemStatus 'Turbo.Quiet' 'Selected'
    Invoke-Mode 'Turbo.Extreme'
    Wait-ForItemStatus 'Turbo.Extreme' 'Selected'
    Invoke-Mode 'Turbo.Normal'
    Wait-ForItemStatus 'Turbo.Normal' 'Selected'

    $cooling = Wait-ForToggleState ([System.Windows.Automation.ToggleState]::Off)
    $toggle = [System.Windows.Automation.TogglePattern]$cooling.GetCurrentPattern(
        [System.Windows.Automation.TogglePattern]::Pattern)
    $toggle.Toggle()
    $cooling = Wait-ForToggleState ([System.Windows.Automation.ToggleState]::On)
    $toggle = [System.Windows.Automation.TogglePattern]$cooling.GetCurrentPattern(
        [System.Windows.Automation.TogglePattern]::Pattern)
    $toggle.Toggle()
    $null = Wait-ForToggleState ([System.Windows.Automation.ToggleState]::Off)

    [pscustomobject]@{
        RapidModeSequence = 'Turbo -> Custom -> Office'
        TurboTierSequence = 'Quiet -> Extreme -> Normal'
        StrongCooling = 'Off -> On -> Off'
        Responsive = $true
    } | Format-List
} finally {
    if ($null -ne $appProcess -and -not $appProcess.HasExited) {
        $appProcess.CloseMainWindow() | Out-Null
        Start-Sleep -Milliseconds 300
    }
    if ($null -ne $appProcess -and -not $appProcess.HasExited) { $appProcess.Kill() }
}
