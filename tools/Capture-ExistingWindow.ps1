param(
    [Parameter(Mandatory)][int]$TargetProcessId,
    [Parameter(Mandatory)][string]$Output,
    [int]$Width = 0,
    [int]$Height = 0,
    [switch]$Primary,
    [int]$ClickX = -1,
    [int]$ClickY = -1,
    [ValidateRange(-2400,2400)][int]$WheelDelta = 0,
    [switch]$Hover
)
$ErrorActionPreference = 'Stop'
$Output = [IO.Path]::GetFullPath($Output)
if (-not $Output.StartsWith('C:\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Capture output must be on C:.' }
if (Get-Process | Where-Object { $_.ProcessName -eq 'ACShadows' -or $_.ProcessName -like '*-Win64-Shipping' }) { throw 'Game active; window activation deferred.' }
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class WindowCaptureNative {
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr hwnd, int x, int y, int w, int h, bool repaint);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint first, uint second, bool attach);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extra);
}
'@
$oldContext = [WindowCaptureNative]::SetThreadDpiAwarenessContext([IntPtr](-4))
try {
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing
    $process = Get-Process -Id $TargetProcessId
    if ($process.ProcessName -ne 'Jiaolong.ControlCenter' -or $process.MainWindowHandle -eq 0) { throw 'Expected existing control-center window.' }
    $window = $process.MainWindowHandle
    $screen = [System.Windows.Forms.Screen]::AllScreens | Where-Object { $_.Primary -eq [bool]$Primary } | Select-Object -First 1
    if ($null -eq $screen) { $screen = [System.Windows.Forms.Screen]::PrimaryScreen }
    if ($Width -gt 0 -and $Height -gt 0) {
        $area = $screen.WorkingArea
        if ($Width -gt $area.Width -or $Height -gt $area.Height) { throw 'Requested size exceeds display work area.' }
        $sized = $false
        for ($attempt = 0; $attempt -lt 3; $attempt++) {
            if (-not [WindowCaptureNative]::MoveWindow($window, $area.Left + [int](($area.Width-$Width)/2), $area.Top + [int](($area.Height-$Height)/2), $Width, $Height, $true)) { throw 'MoveWindow failed.' }
            Start-Sleep -Milliseconds 450
            $sizingRect = New-Object WindowCaptureNative+Rect
            if ([WindowCaptureNative]::GetWindowRect($window, [ref]$sizingRect) -and ($sizingRect.Right-$sizingRect.Left) -eq $Width -and ($sizingRect.Bottom-$sizingRect.Top) -eq $Height) { $sized = $true; break }
        }
        if (-not $sized) { throw 'Requested window size did not stabilize; no input or capture sent.' }
    }
    [void][WindowCaptureNative]::ShowWindow($window, 9)
    [void][WindowCaptureNative]::SetForegroundWindow($window)
    if ([WindowCaptureNative]::GetForegroundWindow() -ne $window) {
        $ownerId = [uint32]0
        $foregroundThread = [WindowCaptureNative]::GetWindowThreadProcessId([WindowCaptureNative]::GetForegroundWindow(), [ref]$ownerId)
        $targetThread = [WindowCaptureNative]::GetWindowThreadProcessId($window, [ref]$ownerId)
        $currentThread = [WindowCaptureNative]::GetCurrentThreadId()
        $attachedForeground = $false
        $attachedTarget = $false
        try {
            if ($currentThread -ne $foregroundThread) { $attachedForeground = [WindowCaptureNative]::AttachThreadInput($currentThread, $foregroundThread, $true) }
            if ($currentThread -ne $targetThread -and $targetThread -ne $foregroundThread) { $attachedTarget = [WindowCaptureNative]::AttachThreadInput($currentThread, $targetThread, $true) }
            [void][WindowCaptureNative]::BringWindowToTop($window)
            [void][WindowCaptureNative]::SetForegroundWindow($window)
        } finally {
            if ($attachedTarget) { [void][WindowCaptureNative]::AttachThreadInput($currentThread, $targetThread, $false) }
            if ($attachedForeground) { [void][WindowCaptureNative]::AttachThreadInput($currentThread, $foregroundThread, $false) }
        }
    }
    Start-Sleep -Milliseconds 1200
    if ([WindowCaptureNative]::GetForegroundWindow() -ne $window) { throw 'Target window is not foreground; no input or capture sent.' }
    $rect = New-Object WindowCaptureNative+Rect
    if (-not [WindowCaptureNative]::GetWindowRect($window, [ref]$rect)) { throw 'Window disappeared during capture.' }
    if ($ClickX -ge 0 -and $ClickY -ge 0) {
        if ($ClickX -ge $rect.Right-$rect.Left -or $ClickY -ge $rect.Bottom-$rect.Top) { throw 'Click outside target window.' }
        if ($Hover) {
            [void][WindowCaptureNative]::SetCursorPos($rect.Left+8,$rect.Top+8)
            [WindowCaptureNative]::mouse_event(1,1,0,0,[UIntPtr]::Zero)
            Start-Sleep -Milliseconds 150
        }
        [void][WindowCaptureNative]::SetCursorPos($rect.Left+$ClickX,$rect.Top+$ClickY)
        if ($Hover) { [WindowCaptureNative]::mouse_event(1,1,0,0,[UIntPtr]::Zero) }
        Start-Sleep -Milliseconds 150
        if ([WindowCaptureNative]::GetForegroundWindow() -ne $window) { throw 'Target lost foreground; no input sent.' }
        if (-not $Hover -and $WheelDelta -eq 0) {
            [WindowCaptureNative]::mouse_event(2,0,0,0,[UIntPtr]::Zero)
            Start-Sleep -Milliseconds 80
            [WindowCaptureNative]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
        } elseif (-not $Hover) {
            $wheelData=[BitConverter]::ToUInt32([BitConverter]::GetBytes($WheelDelta),0)
            [WindowCaptureNative]::mouse_event(0x0800,0,0,$wheelData,[UIntPtr]::Zero)
        }
        Start-Sleep -Milliseconds $(if ($Hover) { 3000 } else { 1200 })
    }
    if ([WindowCaptureNative]::GetForegroundWindow() -ne $window) { throw 'Target lost foreground; capture skipped.' }
    $bitmap = [System.Drawing.Bitmap]::new($rect.Right-$rect.Left, $rect.Bottom-$rect.Top)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bitmap.Size)
        $bitmap.Save($Output, [System.Drawing.Imaging.ImageFormat]::Png)
    } finally { $graphics.Dispose(); $bitmap.Dispose() }
    [pscustomobject]@{ ProcessId=$TargetProcessId; Dpi=[WindowCaptureNative]::GetDpiForWindow($window); Width=$rect.Right-$rect.Left; Height=$rect.Bottom-$rect.Top; Display=[System.Windows.Forms.Screen]::FromHandle($window).DeviceName; Output=$Output }
} finally { [void][WindowCaptureNative]::SetThreadDpiAwarenessContext($oldContext) }
