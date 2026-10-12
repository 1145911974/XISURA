param(
    [Parameter(Mandatory)] [int] $ClientPid,
    [Parameter(Mandatory)] [string] $OutputPath,
    [switch] $SensorsOnly,
    [switch] $ReadOnly
)
$ErrorActionPreference = 'Stop'
$app = 'C:\Program Files\Jiaolong Control Center'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
foreach ($name in 'LibreHardwareMonitorLib.dll', 'Jiaolong.Contracts.dll', 'Jiaolong.Diagnostics.dll', 'Jiaolong.ControlCenter.dll') {
    [void][Reflection.Assembly]::LoadFrom((Join-Path $app $name))
}
Add-Type 'using System;using System.Threading.Tasks;public static class BoundedCpuModeLoad{public static volatile bool Stop;public static double Sink;public static Task[] Start(int n){Stop=false;var t=new Task[n];for(int i=0;i<n;i++)t[i]=Task.Run(()=>{double x=1;while(!Stop){for(int j=0;j<10000;j++)x=Math.Sqrt(x+1.234567);if(x>1e10)x=1;}Sink=x;});return t;}}'

$process = Get-Process -Id $ClientPid -ErrorAction Stop
if ($process.Path -ne (Join-Path $app 'Jiaolong.ControlCenter.exe')) { throw 'Client identity changed' }
if (Get-Process | Where-Object { $_.ProcessName -eq 'ACShadows' -or $_.ProcessName -like '*-Win64-Shipping' }) { throw 'Game active' }
$window = [Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
$screen = [Windows.Forms.Screen]::AllScreens | Where-Object { -not $_.Primary } | Select-Object -First 1
$bounds = $window.Current.BoundingRectangle
if (-not $screen -or $bounds.Left -lt $screen.Bounds.Left -or $bounds.Right -gt $screen.Bounds.Right) { throw 'Client not on secondary display' }
$computer = [LibreHardwareMonitor.Hardware.Computer]::new()
$computer.IsCpuEnabled = $true
$computer.Open()
$cpu = $computer.Hardware | Where-Object { $_.HardwareType -eq [LibreHardwareMonitor.Hardware.HardwareType]::Cpu } | Select-Object -First 1
if ($SensorsOnly) {
    $cpu.Update()
    $cpu.Sensors | Select-Object Name, SensorType, Value | ConvertTo-Json -Depth 3 |
        Set-Content -LiteralPath $OutputPath -Encoding utf8
    $computer.Close()
    return
}
$client = [Jiaolong_ControlCenter.Services.ControlCenterClient]::new($null)
$rows = [Collections.Generic.List[object]]::new()
$reason = 'completed'
$officeAttempted = $false

function Read-State { $client.GetHomeStateAsync([Threading.CancellationToken]::None).GetAwaiter().GetResult() }
function Set-Mode([string] $id) {
    $element = $window.FindFirst([Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty, $id))
    if (-not $element -or -not $element.Current.IsEnabled) { throw "Mode unavailable: $id" }
    ([Windows.Automation.InvokePattern]$element.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern)).Invoke()
}
if ($ReadOnly) {
    try {
        $state = Read-State
        [pscustomobject]@{
            at = (Get-Date -Format o); mode = [string]$state.Controls.ModeEvidence.ParsedMode
            verification = [string]$state.Controls.ModeEvidence.VerificationLevel
            spl = $state.Controls.CpuTuning.SplWatts; sppt = $state.Controls.CpuTuning.SpptWatts
            acMhz = $state.Controls.CpuTuning.AcMaxFrequencyMhz; dcMhz = $state.Controls.CpuTuning.DcMaxFrequencyMhz
            curve = $state.Controls.CpuTuning.NegativeCurveOptimizer
            gpuSubmission = $state.Controls.GpuClockLimit.SubmittedMhz
        } | ConvertTo-Json | Set-Content -LiteralPath $OutputPath -Encoding utf8
    }
    finally { [void]$client.DisposeAsync().AsTask().GetAwaiter().GetResult(); $computer.Close() }
    return
}
function Sample-Load([string] $phase, [string] $expectedMode, [int] $spl, [int] $sppt) {
    $state = Read-State
    if ([string]$state.Controls.ModeEvidence.ParsedMode -ne $expectedMode -or
        $state.Controls.CpuTuning.SplWatts -ne $spl -or $state.Controls.CpuTuning.SpptWatts -ne $sppt -or
        $state.Telemetry.CpuFanRpm -le 0) {
        throw "Mode/limits not ready for $phase"
    }
    $tasks = [BoundedCpuModeLoad]::Start(16)
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(12)
        while ([DateTime]::UtcNow -lt $deadline) {
            Start-Sleep -Milliseconds 1000
            $cpu.Update()
            $tctl = $cpu.Sensors | Where-Object { $_.SensorType -eq [LibreHardwareMonitor.Hardware.SensorType]::Temperature -and $_.Name -eq 'Core (Tctl/Tdie)' } | Select-Object -First 1
            $package = $cpu.Sensors | Where-Object { $_.SensorType -eq [LibreHardwareMonitor.Hardware.SensorType]::Power -and $_.Name -eq 'Package' } | Select-Object -First 1
            $load = $cpu.Sensors | Where-Object { $_.SensorType -eq [LibreHardwareMonitor.Hardware.SensorType]::Load -and $_.Name -eq 'CPU Total' } | Select-Object -First 1
            $row = [pscustomobject]@{
                phase = $phase; at = (Get-Date -Format o); mode = $expectedMode; spl = $spl; sppt = $sppt
                packageW = $package.Value; cpuLoad = $load.Value; tctlC = $tctl.Value
            }
            $rows.Add($row)
            if ($null -eq $row.packageW -or $null -eq $row.cpuLoad -or $null -eq $row.tctlC) { throw 'Sensor unavailable' }
            if ($row.tctlC -ge 90) { throw 'temperatureStop90' }
        }
    }
    finally {
        [BoundedCpuModeLoad]::Stop = $true
        [void][Threading.Tasks.Task]::WaitAll($tasks, 5000)
    }
}

try {
    $state = Read-State
    if ([string]$state.Controls.ModeEvidence.ParsedMode -ne 'Balanced' -or $state.Controls.AdaptiveAutomation.Enabled) { throw 'Initial mode/automation changed' }
    $officeAttempted = $true
    Set-Mode 'Mode.Office'
    Start-Sleep -Seconds 2
    Sample-Load 'office' 'Quiet' 45 55
    Set-Mode 'Mode.Gaming'
    Start-Sleep -Seconds 2
    Sample-Load 'gaming' 'Balanced' 60 120
}
catch { $reason = 'error: ' + $_.Exception.Message }
finally {
    [BoundedCpuModeLoad]::Stop = $true
    if ($officeAttempted) {
        try { if ([string](Read-State).Controls.ModeEvidence.ParsedMode -ne 'Balanced') { Set-Mode 'Mode.Gaming'; Start-Sleep -Seconds 2 } }
        catch { $reason += '; restore: ' + $_.Exception.Message }
    }
    try { [void]$client.DisposeAsync().AsTask().GetAwaiter().GetResult() } catch { }
    $computer.Close()
    [pscustomobject]@{ reason = $reason; rows = @($rows); finished = (Get-Date -Format o) } |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $OutputPath -Encoding utf8
}
