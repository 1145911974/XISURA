param(
    [Parameter(Mandatory)][string]$Executable,
    [Parameter(Mandatory)][string]$Output,
    [string[]]$Arguments = @(),
    [int]$TargetProcessId = 0,
    [int]$WindowWidth = 1672,
    [int]$WindowHeight = 1045,
    [string]$AutomationId = '',
    [int]$ClickOffsetX = -1,
    [int]$ClickOffsetY = -1,
    [switch]$UseApplicationPlacement
)

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class JiaolongSecondaryCapture
{
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll", SetLastError=true)] public static extern bool MoveWindow(IntPtr hwnd, int x, int y, int width, int height, bool repaint);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);
}
'@

$previousDpiContext = [JiaolongSecondaryCapture]::SetThreadDpiAwarenessContext([IntPtr](-4))
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
$screen = [System.Windows.Forms.Screen]::AllScreens |
    Where-Object {
        -not $_.Primary -and
        ($UseApplicationPlacement -or ($_.WorkingArea.Width -ge $WindowWidth -and $_.WorkingArea.Height -ge $WindowHeight))
    } |
    Select-Object -First 1
if ($null -eq $screen) {
    [void][JiaolongSecondaryCapture]::SetThreadDpiAwarenessContext($previousDpiContext)
    throw "No secondary display can contain ${WindowWidth}x${WindowHeight} physical pixels."
}

$process = if ($TargetProcessId -gt 0) {
    Get-Process -Id $TargetProcessId -ErrorAction Stop
}
else {
    Start-Process -FilePath (Resolve-Path $Executable) -ArgumentList $Arguments -PassThru
}
$deadline = [DateTime]::UtcNow.AddSeconds(20)
do {
    Start-Sleep -Milliseconds 150
    $process.Refresh()
} while ($process.MainWindowHandle -eq [IntPtr]::Zero -and [DateTime]::UtcNow -lt $deadline)
if ($process.MainWindowHandle -eq [IntPtr]::Zero) {
    [void][JiaolongSecondaryCapture]::SetThreadDpiAwarenessContext($previousDpiContext)
    throw 'No main window.'
}

if ($UseApplicationPlacement) {
    Start-Sleep -Seconds 4
}
else {
    $x = $screen.WorkingArea.Left + [int](($screen.WorkingArea.Width - $WindowWidth) / 2)
    $y = $screen.WorkingArea.Top + [int](($screen.WorkingArea.Height - $WindowHeight) / 2)
    if (-not [JiaolongSecondaryCapture]::MoveWindow($process.MainWindowHandle, $x, $y, $WindowWidth, $WindowHeight, $true)) {
        $errorCode = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
        [void][JiaolongSecondaryCapture]::SetThreadDpiAwarenessContext($previousDpiContext)
        throw "MoveWindow failed with Win32 error $errorCode."
    }
    [JiaolongSecondaryCapture]::SetForegroundWindow($process.MainWindowHandle) | Out-Null
    Start-Sleep -Seconds 2
}

if ($ClickOffsetX -ge 0 -and $ClickOffsetY -ge 0) {
    $navigationRect = New-Object JiaolongSecondaryCapture+RECT
    [JiaolongSecondaryCapture]::GetWindowRect($process.MainWindowHandle, [ref]$navigationRect) | Out-Null
    [JiaolongSecondaryCapture]::SetCursorPos($navigationRect.Left + $ClickOffsetX, $navigationRect.Top + $ClickOffsetY) | Out-Null
    [JiaolongSecondaryCapture]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    [JiaolongSecondaryCapture]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Seconds 2
}
elseif ($AutomationId) {
    Add-Type -AssemblyName UIAutomationClient
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        $AutomationId)
    $element = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    if ($null -eq $element) {
        [void][JiaolongSecondaryCapture]::SetThreadDpiAwarenessContext($previousDpiContext)
        throw "Automation element '$AutomationId' was not found."
    }

    $pattern = $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    ([System.Windows.Automation.InvokePattern]$pattern).Invoke()
    Start-Sleep -Seconds 2
}

$rect = New-Object JiaolongSecondaryCapture+RECT
[JiaolongSecondaryCapture]::GetWindowRect($process.MainWindowHandle, [ref]$rect) | Out-Null
$actualWidth = $rect.Right - $rect.Left
$actualHeight = $rect.Bottom - $rect.Top
$capturedScreen = [System.Windows.Forms.Screen]::FromHandle($process.MainWindowHandle)
$insideSecondary = -not $capturedScreen.Primary -and
    $capturedScreen.DeviceName -eq $screen.DeviceName -and
    $rect.Left -ge $screen.Bounds.Left -and $rect.Top -ge $screen.Bounds.Top -and
    $rect.Right -le $screen.Bounds.Right -and $rect.Bottom -le $screen.Bounds.Bottom
if (-not $insideSecondary) {
    [void][JiaolongSecondaryCapture]::SetThreadDpiAwarenessContext($previousDpiContext)
    throw "Window is not fully contained by secondary display $($screen.DeviceName): $($rect.Left),$($rect.Top) ${actualWidth}x${actualHeight}."
}

$directory = Split-Path -Parent $Output
if ($directory) { New-Item -ItemType Directory -Force -Path $directory | Out-Null }
$bitmap = [System.Drawing.Bitmap]::new($actualWidth, $actualHeight)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
try {
    $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bitmap.Size, [System.Drawing.CopyPixelOperation]::SourceCopy)
    $bitmap.Save($Output, [System.Drawing.Imaging.ImageFormat]::Png)
}
finally {
    $graphics.Dispose()
    $bitmap.Dispose()
    [void][JiaolongSecondaryCapture]::SetThreadDpiAwarenessContext($previousDpiContext)
}

[pscustomobject]@{
    Pid = $process.Id
    Display = $screen.DeviceName
    IsSecondaryContained = $insideSecondary
    ScreenArea = "$($screen.Bounds.Left),$($screen.Bounds.Top) $($screen.Bounds.Width)x$($screen.Bounds.Height)"
    Window = "$($rect.Left),$($rect.Top) ${actualWidth}x${actualHeight}"
    Screenshot = (Resolve-Path $Output).Path
}
