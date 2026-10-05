param([Parameter(Mandatory)][int]$TargetProcessId)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
$root = [System.Windows.Automation.AutomationElement]::FromHandle((Get-Process -Id $TargetProcessId).MainWindowHandle)
function Find-Control($id) {
    $node = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id))
    if (!$node) { throw "Missing control: $id" }
    return $node
}
function Input-Control($name) {
    $condition = [System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, $name),
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit))
    $node = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    if (!$node) { throw "Missing input: $name" }
    return $node
}
function Input-Value($name) {
    return [System.Windows.Automation.ValuePattern](Input-Control $name).GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
}
function Set-Mode($name) {
    $combo = Find-Control 'CurveModeSelector'
    ([System.Windows.Automation.ExpandCollapsePattern]$combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)).Expand()
    Start-Sleep -Milliseconds 150
    $item = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, $name))
    ([System.Windows.Automation.SelectionItemPattern]$item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
    Start-Sleep -Milliseconds 250
}
# Exercise draft editors only. Never save or apply hardware settings.
Set-Mode '全核统一'
$previous = (Input-Value '曲线优化').Current.Value
try {
    (Input-Value '曲线优化').SetValue('7')
    ([System.Windows.Automation.InvokePattern](Find-Control 'PerformanceV2AdvancedButton').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
    Start-Sleep -Milliseconds 600
    Set-Mode '逐核设置'
    if ((Input-Control '曲线优化').Current.IsEnabled) { throw 'All-core editor is enabled in per-core mode' }
    Set-Mode '沿用 BIOS'
    if ((Input-Control '曲线优化').Current.IsEnabled) { throw 'All-core editor is enabled in BIOS mode' }
    Set-Mode '全核统一'
    if ((Input-Value '曲线优化').Current.Value -ne '7') { throw 'Positive offset was lost or clamped' }
    (Input-Value '曲线优化').SetValue('-9')
    Set-Mode '逐核设置'
    Set-Mode '全核统一'
    if ((Input-Value '曲线优化').Current.Value -ne '-9') { throw 'Negative offset was lost' }
    foreach ($obsolete in @('CurveOptimizerAllRow', 'AllCoreCurveMode', 'QuickUndervoltToggle')) {
        $node = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $obsolete))
        if ($node) { throw "Duplicate control remains: $obsolete" }
    }
    'PASS: positive/negative CO survives mode changes; redundant advanced controls removed.'
}
finally {
    Set-Mode '全核统一'
    (Input-Value '曲线优化').SetValue($previous)
    Set-Mode '沿用 BIOS'
}
