param([Parameter(Mandatory)][int]$TargetProcessId, [Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
if (-not [IO.Path]::GetFullPath($OutputDirectory).StartsWith('C:\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Artifacts must stay on C:.' }
Add-Type -AssemblyName UIAutomationClient,System.Drawing,System.Windows.Forms
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class LogoMotionCapture {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left,Top,Right,Bottom; }
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
}
'@
$previous = [LogoMotionCapture]::SetThreadDpiAwarenessContext([IntPtr](-4))
try {
    $process = Get-Process -Id $TargetProcessId
    if ($process.MainWindowTitle -ne 'Jiaolong Logo Lab') { throw 'Only the non-hardware Logo Lab is allowed.' }
    $screen = [Windows.Forms.Screen]::FromHandle($process.MainWindowHandle)
    if ($screen.Primary) { throw 'Move Logo Lab to the secondary display first.' }
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
    $nameProperty = [System.Windows.Automation.AutomationElement]::NameProperty
    $combo = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.PropertyCondition]::new($nameProperty,'Logo Lab 模式'))
    ([System.Windows.Automation.ExpandCollapsePattern]$combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)).Expand()
    $item = $combo.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.AndCondition]::new(
            [System.Windows.Automation.PropertyCondition]::new($nameProperty,'游戏'),
            [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::IsSelectionItemPatternAvailableProperty,$true)))
    if ($null -eq $item) { throw 'Gaming item not found.' }
    $rect = New-Object LogoMotionCapture+RECT
    [void][LogoMotionCapture]::GetWindowRect($process.MainWindowHandle,[ref]$rect)
    New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
    $bitmap = [Drawing.Bitmap]::new($rect.Right-$rect.Left,$rect.Bottom-$rect.Top)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        ([System.Windows.Automation.SelectionItemPattern]$item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
        $watch = [Diagnostics.Stopwatch]::StartNew()
        foreach ($milliseconds in @(0,100,200,500)) {
            $remaining = $milliseconds - $watch.ElapsedMilliseconds
            if ($remaining -gt 0) { Start-Sleep -Milliseconds $remaining }
            $graphics.CopyFromScreen($rect.Left,$rect.Top,0,0,$bitmap.Size)
            $path = Join-Path $OutputDirectory "logo-$milliseconds.png"
            $bitmap.Save($path,[Drawing.Imaging.ImageFormat]::Png)
            [pscustomobject]@{ Frame=$milliseconds; ActualMilliseconds=$watch.ElapsedMilliseconds; Path=$path }
        }
    } finally { $graphics.Dispose(); $bitmap.Dispose() }
} finally { [void][LogoMotionCapture]::SetThreadDpiAwarenessContext($previous) }
