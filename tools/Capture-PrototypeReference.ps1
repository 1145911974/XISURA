param(
    [string]$Executable,
    [Parameter(Mandatory)][string]$Output,
    [int]$WaitSeconds = 0,
    [int]$TargetProcessId = 0,
    [switch]$SkipAccentCheck
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
}
'@

$process = if ($TargetProcessId -gt 0) {
    Get-Process -Id $TargetProcessId -ErrorAction Stop
} else {
    if (-not $Executable) { throw 'Executable is required when TargetProcessId is not supplied.' }
    Start-Process -FilePath (Resolve-Path $Executable) -PassThru
}
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
$stableSamples = 0
$settleDeadline = [DateTime]::UtcNow.AddSeconds(10)
do {
    [void][JiaolongCaptureNative]::MoveWindow($process.MainWindowHandle, $x, $y, 1672, 1045, $true)
    Start-Sleep -Milliseconds 100
    if (-not [JiaolongCaptureNative]::GetWindowRect($process.MainWindowHandle, [ref]$rect)) { throw 'GetWindowRect failed.' }
    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    $stableSamples = if ($width -eq 1672 -and $height -eq 1045) { $stableSamples + 1 } else { 0 }
} while ($stableSamples -lt 5 -and [DateTime]::UtcNow -lt $settleDeadline)
if ($stableSamples -lt 5) { throw "Expected stable 1672x1045, got ${width}x${height}." }
if ($WaitSeconds -gt 0) { Start-Sleep -Seconds $WaitSeconds }

$directory = Split-Path -Parent $Output
if ($directory) { New-Item -ItemType Directory -Force -Path $directory | Out-Null }
$renderDeadline = [DateTime]::UtcNow.AddSeconds(20)
$renderReady = $false
do {
    $bitmap = [System.Drawing.Bitmap]::new($width, $height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $hdc = $graphics.GetHdc()
    try {
        if (-not [JiaolongCaptureNative]::PrintWindow($process.MainWindowHandle, $hdc, 2)) { throw 'PrintWindow failed.' }
    } finally {
        $graphics.ReleaseHdc($hdc)
    }
    if ($SkipAccentCheck) { $renderReady = $true }
    for ($sampleY = 24; $sampleY -lt $height -and -not $renderReady; $sampleY += 32) {
        for ($sampleX = 24; $sampleX -lt $width; $sampleX += 32) {
            $pixel = $bitmap.GetPixel($sampleX, $sampleY)
            if ($pixel.B -gt 80 -and $pixel.B -gt ($pixel.R + 24)) { $renderReady = $true; break }
        }
    }
    if (-not $renderReady) {
        $graphics.Dispose()
        $bitmap.Dispose()
        Start-Sleep -Milliseconds 100
    }
} while (-not $renderReady -and [DateTime]::UtcNow -lt $renderDeadline)
if (-not $renderReady) { throw 'Prototype did not render its office accent before capture.' }

try {
    $bitmap.Save($Output, [System.Drawing.Imaging.ImageFormat]::Png)
} finally {
    $graphics.Dispose()
    $bitmap.Dispose()
    [void][JiaolongCaptureNative]::SetThreadDpiAwarenessContext($previousDpiContext)
}
