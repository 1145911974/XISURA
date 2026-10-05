param(
    [Parameter(Mandatory)][string]$Executable,
    [Parameter(Mandatory)][string]$Output
)

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class JiaolongCaptureNative
{
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr hwnd, int x, int y, int width, int height, bool repaint);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
'@

$process = Start-Process -FilePath (Resolve-Path $Executable) -PassThru
$deadline = [DateTime]::UtcNow.AddSeconds(20)
do {
    Start-Sleep -Milliseconds 100
    $process.Refresh()
    if ($process.HasExited) { throw "Prototype exited with code $($process.ExitCode)." }
} while ($process.MainWindowHandle -eq [IntPtr]::Zero -and [DateTime]::UtcNow -lt $deadline)
if ($process.MainWindowHandle -eq [IntPtr]::Zero) { throw 'Prototype did not expose a main window.' }

$previousDpiContext = [JiaolongCaptureNative]::SetThreadDpiAwarenessContext([IntPtr](-4))
$screen = [System.Windows.Forms.Screen]::AllScreens | Where-Object { -not $_.Primary } | Select-Object -First 1
if ($null -eq $screen) { $screen = [System.Windows.Forms.Screen]::PrimaryScreen }
$x = $screen.WorkingArea.Left + [Math]::Max(0, [int](($screen.WorkingArea.Width - 1672) / 2))
$y = $screen.WorkingArea.Top + [Math]::Max(0, [int](($screen.WorkingArea.Height - 1045) / 2))
$rect = New-Object JiaolongCaptureNative+RECT
[void][JiaolongCaptureNative]::MoveWindow($process.MainWindowHandle, $x, $y, 1672, 1045, $true)
[void][JiaolongCaptureNative]::SetForegroundWindow($process.MainWindowHandle)
[void][JiaolongCaptureNative]::SetWindowPos($process.MainWindowHandle, [IntPtr]::Zero, 0, 0, 0, 0, 0x0043)
Start-Sleep -Milliseconds 900
if (-not [JiaolongCaptureNative]::GetWindowRect($process.MainWindowHandle, [ref]$rect)) { throw 'GetWindowRect failed.' }
$width = $rect.Right - $rect.Left
$height = $rect.Bottom - $rect.Top

$directory = Split-Path -Parent $Output
if ($directory) { New-Item -ItemType Directory -Force -Path $directory | Out-Null }
$bitmap = [System.Drawing.Bitmap]::new($width, $height)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$windowHdc = $graphics.GetHdc()
try {
    $captured = [JiaolongCaptureNative]::PrintWindow($process.MainWindowHandle, $windowHdc, 2)
} finally {
    $graphics.ReleaseHdc($windowHdc)
}
if (-not $captured) { $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bitmap.Size) }

try {
    $bitmap.Save($Output, [System.Drawing.Imaging.ImageFormat]::Png)
} finally {
    $graphics.Dispose()
    $bitmap.Dispose()
    [void][JiaolongCaptureNative]::SetThreadDpiAwarenessContext($previousDpiContext)
}
