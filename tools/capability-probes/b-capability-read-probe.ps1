param(
    [Parameter(Mandatory)] [string] $OutputPath
)

$ErrorActionPreference = 'Stop'
$isElevated = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isElevated) { throw 'Probe must run elevated.' }

$snapshot = [ordered]@{
    Timestamp = [DateTimeOffset]::Now
    Elevated = $true
    Assembly = [ordered]@{}
    CpuSmu = [ordered]@{}
    Gpu = [ordered]@{}
    Mux = [ordered]@{}
    Fan = [ordered]@{}
    Errors = [System.Collections.Generic.List[object]]::new()
}

function Capture([string] $Area, [scriptblock] $Action) {
    try { & $Action }
    catch {
        $message = $_.Exception.Message
        if ($_.Exception.InnerException) { $message += ' | ' + $_.Exception.InnerException.Message }
        $snapshot.Errors.Add([ordered]@{ Area = $Area; Message = $message })
        $null
    }
}

Set-Location -LiteralPath 'F:\JiaoLongControl'
$dll = 'F:\JiaoLongControl\JiaoLongControl.dll'
$assembly = [Reflection.Assembly]::LoadFrom($dll)
$snapshot.Assembly.Version = $assembly.GetName().Version.ToString()
$snapshot.Assembly.Sha256 = (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash

$smu = Capture 'CpuSmu.Create' { [Activator]::CreateInstance($assembly.GetType('JiaoLongControl.Server.Core.Controllers.RyzenSmuController', $true)) }
if ($smu) {
    $snapshot.CpuSmu.IsInitialized = $smu.IsInitialized
    $snapshot.CpuSmu.Family = $smu.CurrentFamily.ToString()
    $snapshot.CpuSmu.Telemetry = Capture 'CpuSmu.Telemetry' { $smu.GetSmuTelemetry() }
    $snapshot.CpuSmu.Mp1MailboxArgs = Capture 'CpuSmu.Mp1MailboxArgs' { @($smu.ReadMailboxArgs($false)) }
    $snapshot.CpuSmu.RsmuMailboxArgs = Capture 'CpuSmu.RsmuMailboxArgs' { @($smu.ReadMailboxArgs($true)) }
    $snapshot.CpuSmu.CoreVoltage = Capture 'CpuSmu.CoreVoltage' { $smu.GetCoreVoltage() }
}

$nvidia = Capture 'Gpu.NvidiaCreate' { [Activator]::CreateInstance($assembly.GetType('JiaoLongControl.Server.Core.Controllers.NvidiaGpuController', $true)) }
if ($nvidia) {
    $snapshot.Gpu.Stats = Capture 'Gpu.Stats' { $nvidia.GetGpuAllStats(0) }
    $snapshot.Gpu.PowerRange = Capture 'Gpu.PowerRange' { $nvidia.GetGpuPowerLimitRange(0) }
    $snapshot.Gpu.CurveCapabilities = Capture 'Gpu.CurveCapabilities' { $nvidia.GetGpuCurveCapabilities(0) }
    $snapshot.Gpu.CurveStatus = Capture 'Gpu.CurveStatus' { $nvidia.GetGpuCurveStatus(0) }
}

$mux = Capture 'Mux.Create' { [Activator]::CreateInstance($assembly.GetType('JiaoLongControl.Server.Core.Controllers.GpuController', $true)) }
if ($mux) { $snapshot.Mux.Current = Capture 'Mux.Get' { $mux.Get() } }

$fan = Capture 'Fan.Create' { [Activator]::CreateInstance($assembly.GetType('JiaoLongControl.Server.Core.Controllers.FanController', $true)) }
if ($fan) {
    $snapshot.Fan.IsInitialized = $fan.IsInitialized
    $snapshot.Fan.Speed = Capture 'Fan.Speed' { $fan.GetFanSpeed() }
    $snapshot.Fan.MaxSwitch = Capture 'Fan.MaxSwitch' { $fan.GetMaxFanSpeedSwitch() }
}

$snapshot | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $OutputPath -Encoding utf8
