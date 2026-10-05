param(
    [Parameter(Mandatory)][string]$Executable,
    [Parameter(Mandatory)][ValidateSet('A', 'B')][string]$Profile,
    [Parameter(Mandatory)][string]$OutputDirectory
)

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class JiaolongMotionCaptureNative
{
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr hwnd, int x, int y, int width, int height, bool repaint);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
}
'@

function Wait-Window([System.Diagnostics.Process]$Process) {
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        Start-Sleep -Milliseconds 100
        $Process.Refresh()
        if ($Process.HasExited) { throw "Prototype exited with code $($Process.ExitCode)." }
    } while ($Process.MainWindowHandle -eq [IntPtr]::Zero -and [DateTime]::UtcNow -lt $deadline)
    if ($Process.MainWindowHandle -eq [IntPtr]::Zero) { throw 'Prototype did not expose a window.' }
}

function Find-Element([System.Windows.Automation.AutomationElement]$Root, [string]$Property, [string]$Value) {
    $automationProperty = if ($Property -eq 'Id') {
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty
    } else {
        [System.Windows.Automation.AutomationElement]::NameProperty
    }
    $condition = [System.Windows.Automation.PropertyCondition]::new($automationProperty, $Value)
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        $element = $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
        if ($null -ne $element) { return $element }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Element not found: $Property=$Value"
}

function Invoke-Element([System.Windows.Automation.AutomationElement]$Element) {
    $pattern = $Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    ([System.Windows.Automation.InvokePattern]$pattern).Invoke()
}

function Toggle-Element([System.Windows.Automation.AutomationElement]$Element) {
    $pattern = $Element.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    ([System.Windows.Automation.TogglePattern]$pattern).Toggle()
}

function Save-Window([IntPtr]$Handle, [string]$Name) {
    $rect = New-Object JiaolongMotionCaptureNative+RECT
    if (-not [JiaolongMotionCaptureNative]::GetWindowRect($Handle, [ref]$rect)) { throw 'GetWindowRect failed.' }
    $bitmap = [System.Drawing.Bitmap]::new($rect.Right - $rect.Left, $rect.Bottom - $rect.Top)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $hdc = $graphics.GetHdc()
    try {
        if (-not [JiaolongMotionCaptureNative]::PrintWindow($Handle, $hdc, 2)) { throw 'PrintWindow failed.' }
    } finally {
        $graphics.ReleaseHdc($hdc)
    }
    try { $bitmap.Save((Join-Path $OutputDirectory $Name), [System.Drawing.Imaging.ImageFormat]::Png) }
    finally { $graphics.Dispose(); $bitmap.Dispose() }
}

function Start-Video([string]$Name, [double]$Duration) {
    $output = Join-Path $OutputDirectory $Name
    $arguments = @(
        '-y', '-loglevel', 'error', '-f', 'gdigrab', '-framerate', '30',
        '-offset_x', '40', '-offset_y', '40', '-video_size', '1672x941',
        '-i', 'desktop', '-t', $Duration.ToString([Globalization.CultureInfo]::InvariantCulture),
        '-vf', 'pad=iw:ceil(ih/2)*2', '-c:v', 'libx264', '-preset', 'veryfast',
        '-crf', '19', '-pix_fmt', 'yuv420p', $output
    )
    Start-Process -FilePath (Get-Command ffmpeg).Source -ArgumentList $arguments -PassThru -WindowStyle Hidden
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$env:JIAOLONG_MOTION_PREVIEW = $Profile
$process = Start-Process -FilePath (Resolve-Path $Executable) -PassThru
try {
    Wait-Window $process
    [void][JiaolongMotionCaptureNative]::MoveWindow($process.MainWindowHandle, 40, 40, 1672, 941, $true)
    [void][JiaolongMotionCaptureNative]::SetWindowPos($process.MainWindowHandle, [IntPtr]::Zero, 40, 40, 1672, 941, 0x0040)
    [void][JiaolongMotionCaptureNative]::SetForegroundWindow($process.MainWindowHandle)
    Start-Sleep -Milliseconds 1500
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
    $strongCooling = Find-Element $root Id 'StrongCooling'
    $office = Find-Element $root Id 'Mode.Office'
    $turbo = Find-Element $root Id 'Mode.Turbo'
    $custom = Find-Element $root Id 'Mode.Custom'

    Save-Window $process.MainWindowHandle '00-office-off.png'
    Toggle-Element $strongCooling
    Start-Sleep -Milliseconds 90
    Save-Window $process.MainWindowHandle '01-cooling-enter.png'
    Start-Sleep -Milliseconds 450
    Save-Window $process.MainWindowHandle '02-cooling-on.png'
    Toggle-Element $strongCooling
    Start-Sleep -Milliseconds 450
    Save-Window $process.MainWindowHandle '03-cooling-off.png'

    Invoke-Element $turbo
    foreach ($sample in @(@(70, '10-turbo-070.png'), @(160, '11-turbo-160.png'), @(300, '12-turbo-300.png'), @(620, '13-turbo-620.png'))) {
        Start-Sleep -Milliseconds $sample[0]
        Save-Window $process.MainWindowHandle $sample[1]
    }
    Start-Sleep -Milliseconds 500
    Save-Window $process.MainWindowHandle '14-turbo-final.png'

    Invoke-Element $custom
    foreach ($sample in @(@(90, '20-custom-090.png'), @(220, '21-custom-220.png'), @(420, '22-custom-420.png'))) {
        Start-Sleep -Milliseconds $sample[0]
        Save-Window $process.MainWindowHandle $sample[1]
    }
    Start-Sleep -Milliseconds 600
    Save-Window $process.MainWindowHandle '23-custom-final.png'

    Invoke-Element $office
    Start-Sleep -Milliseconds 1000
    $video = Start-Video '30-cooling-cycle.mp4' 2.2
    Start-Sleep -Milliseconds 300
    Toggle-Element $strongCooling
    Start-Sleep -Milliseconds 650
    Toggle-Element $strongCooling
    $video.WaitForExit()

    $video = Start-Video '31-office-to-turbo.mp4' 2.2
    Start-Sleep -Milliseconds 300
    Invoke-Element $turbo
    $video.WaitForExit()

    $video = Start-Video '32-turbo-to-custom.mp4' 2.2
    Start-Sleep -Milliseconds 300
    Invoke-Element $custom
    $video.WaitForExit()

    $measurements = @($turbo, $custom) | ForEach-Object {
        $bounds = $_.Current.BoundingRectangle
        [pscustomobject]@{ Name = $_.Current.AutomationId; Width = $bounds.Width; Height = $bounds.Height }
    }
    $measurements | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'uia-measurements.json') -Encoding utf8
} finally {
    if ($null -ne $process -and -not $process.HasExited) {
        [void][JiaolongMotionCaptureNative]::SetWindowPos($process.MainWindowHandle, [IntPtr](-2), 0, 0, 0, 0, 0x0003)
    }
    if ($null -ne $process -and -not $process.HasExited) { $process.CloseMainWindow() | Out-Null; Start-Sleep -Milliseconds 300 }
    if ($null -ne $process -and -not $process.HasExited) { $process.Kill() }
    Remove-Item Env:JIAOLONG_MOTION_PREVIEW -ErrorAction SilentlyContinue
}
