param([string]$Result, [switch]$PreflightOnly, [switch]$StrongCooling, [switch]$IndependentCurves, [switch]$SharedCurve, [switch]$AutomaticCeiling, [switch]$DisconnectRelease,
    [ValidateRange(3500,5800)][int]$TargetRpm=3500, [ValidateRange(1,20)][int]$SampleSeconds=8,
    [ValidateRange(0,100)][int]$CpuCurvePercent=100, [ValidateRange(0,100)][int]$GpuCurvePercent=95,
    [ValidateRange(0,100)][int]$SharedCurvePercent=95,
    [ValidateRange(1800,5800)][int]$CeilingRpm=1800)
$ErrorActionPreference = 'Stop'
if (([int][bool]$StrongCooling+[int][bool]$IndependentCurves+[int][bool]$SharedCurve+[int][bool]$AutomaticCeiling) -gt 1) {
    throw 'Choose only one fan strategy for a trial'
}
$curveTrial=$IndependentCurves -or $SharedCurve
if (-not $IndependentCurves -and ($PSBoundParameters.ContainsKey('CpuCurvePercent') -or
    $PSBoundParameters.ContainsKey('GpuCurvePercent'))) { throw 'Curve percentages require -IndependentCurves' }
if (-not $SharedCurve -and $PSBoundParameters.ContainsKey('SharedCurvePercent')) { throw 'Shared curve percent requires -SharedCurve' }
if (-not $AutomaticCeiling -and $PSBoundParameters.ContainsKey('CeilingRpm')) { throw 'Ceiling RPM requires -AutomaticCeiling' }
if (Get-Process -Name '007FirstLight' -ErrorAction SilentlyContinue) { throw 'Fan trial refused: game test is paused' }
$taskName = 'JiaoLongPlus'
$configPath = 'F:\JiaoLong7.3\AppSettings.json'
$tempRoot = 'C:\Users\Administrator\AppData\Local\Temp\'
if (-not $PreflightOnly -and [string]::IsNullOrWhiteSpace($Result)) { throw 'Result path is required for a fan trial' }
if ($Result -and -not [IO.Path]::GetFullPath($Result).StartsWith($tempRoot,[StringComparison]::OrdinalIgnoreCase)) { throw 'Result must stay in C: Temp' }
if ((Get-CimInstance Win32_BaseBoard).Product -ne 'MRID6-23' -or
    (Get-CimInstance Win32_BIOS).SMBIOSBIOSVersion -ne 'MRID6_23_P_V39') { throw 'Unsupported hardware identity' }
$task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
if ($null -ne $task -and ($task.State -ne 'Running' -or
    $task.Actions.Execute -ne 'F:\JiaoLong7.3\第三方蛟龙游戏控制中心.exe')) {
    throw 'Unexpected third-party task baseline'
}
$taskWasRunning = $null -ne $task
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
$thirdPartyNames = @('第三方蛟龙游戏控制中心','JiaoLongControl','JiaoLongControl.Server')
if (-not $taskWasRunning -and @($thirdPartyNames | ForEach-Object { Get-Process -Name $_ -ErrorAction SilentlyContinue }).Count -gt 0) {
    throw 'Third-party fan writer is running without the expected task'
}
$configHash = (Get-FileHash -LiteralPath $configPath -Algorithm SHA256).Hash
$report = [ordered]@{ StartedAtUtc=[DateTimeOffset]::UtcNow; Before=$null; Apply=$null; Samples=@(); Release=$null; TaskRestored=$false; ConfigUnchanged=$false; ServiceRunning=$false }
function CurrentMode { (Request 'getDeviceState' @{}).controls.performanceMode }
function Request([string]$operation, [object]$payload) {
    $newConnection = $null -eq $script:trialPipe
    if ($newConnection) { $script:trialPipe=[IO.Pipes.NamedPipeClientStream]::new('.','Jiaolong.ControlCenter.v1',[IO.Pipes.PipeDirection]::InOut,[IO.Pipes.PipeOptions]::Asynchronous) }
    $pipe=$script:trialPipe
    try {
        if ($newConnection) { $pipe.Connect(3000) }
        function Send($message) {
            $bytes=[Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject $message -Depth 20 -Compress))
            $size=[BitConverter]::GetBytes([int]$bytes.Length)
            $pipe.Write($size,0,4); $pipe.Write($bytes,0,$bytes.Length); $pipe.Flush()
        }
        function Read-Bytes([int]$length) {
            $bytes=[byte[]]::new($length); $offset=0
            while ($offset -lt $length) {
                $read=$pipe.ReadAsync($bytes,$offset,$length-$offset).WaitAsync([TimeSpan]::FromSeconds(10)).GetAwaiter().GetResult()
                if ($read -eq 0) { throw 'Service connection closed' }
                $offset+=$read
            }
            return ,$bytes
        }
        function Receive {
            $size=Read-Bytes 4
            $length=[BitConverter]::ToInt32($size,0)
            if ($length -le 0 -or $length -gt 1048576) { throw 'Invalid IPC length' }
            $bytes=Read-Bytes $length
            [Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json
        }
        $version=[ordered]@{major=1;minor=0}
        if ($newConnection) {
            Send ([ordered]@{kind='hello';protocolVersion=$version;messageId=[Guid]::NewGuid();sentAtUtc=[DateTimeOffset]::UtcNow;clientVersion='winring-fan-verification'})
            if ((Receive).kind -ne 'helloAck') { throw 'IPC handshake failed' }
        }
        $operationId=if($operation -eq 'executeCommand'){$payload.operationId}else{[Guid]::NewGuid()}
        Send ([ordered]@{kind='request';protocolVersion=$version;messageId=[Guid]::NewGuid();sentAtUtc=[DateTimeOffset]::UtcNow;operationId=$operationId;operation=$operation;deadlineUtc=[DateTimeOffset]::UtcNow.AddSeconds(9);payload=$payload})
        $response=Receive
        if ($response.status -ne 'success') { throw "IPC request failed: $($response.error.code)" }
        return $response.payload
    } catch { $pipe.Dispose(); $script:trialPipe=$null; throw }
}
function Snapshot {
    $state=Request 'getDeviceState' @{}
    $t=$state.telemetry
    if ($null -eq $t -or ([DateTimeOffset]::UtcNow-[DateTimeOffset]$t.capturedAtUtc).TotalSeconds -gt 3) { throw 'Telemetry stale' }
    foreach ($name in @('cpuTemperatureC','gpuTemperatureC','cpuFanRpm','gpuFanRpm')) {
        if ($null -eq $t.$name -or -not [double]::IsFinite([double]$t.$name)) { throw "Telemetry missing: $name" }
    }
    return $t
}
function Release {
    Request 'executeCommand' ([ordered]@{type='releaseFanControl';operationId=[Guid]::NewGuid();reason='userRequested'})
}
if ($PreflightOnly) {
    try {
        $state=Request 'getDeviceState' @{}
        [pscustomobject]@{ StrongCooling=$state.controls.strongCooling;
            ActiveFanStrategy=$state.controls.activeFanControlPlan.strategy;
            ActiveFixedRpm=$state.controls.activeFanControlPlan.fixedRpm;
            CpuTemperatureC=$state.telemetry.cpuTemperatureC; GpuTemperatureC=$state.telemetry.gpuTemperatureC;
            CpuFanRpm=$state.telemetry.cpuFanRpm; GpuFanRpm=$state.telemetry.gpuFanRpm;
            AcPowerConnected=$state.telemetry.acPowerConnected; ThirdPartyTaskPresent=$taskWasRunning }
    }
    finally { if ($null -ne $script:trialPipe) { $script:trialPipe.Dispose(); $script:trialPipe=$null } }
    return
}
if ($taskWasRunning -and -not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Administrator token required to stop the third-party task'
}
$touched=$false
try {
    $report.Before=Snapshot
    $beforeControls=(Request 'getDeviceState' @{}).controls
    $report.ModeBefore=$beforeControls.performanceMode
    $report.StrongCoolingBefore=$beforeControls.strongCooling
    $report.ActiveFanPlanBefore=$beforeControls.activeFanControlPlan
    if ($null -ne $beforeControls.activeFanControlPlan -or $beforeControls.strongCooling -ne $false) {
        throw 'Existing fan or strong cooling control requires an explicit restore plan'
    }
    $cpuCurveEc=[Math]::Min(58,[Math]::Max(18,[int][Math]::Round((1800+$CpuCurvePercent*40)/100)))
    $gpuCurveEc=[Math]::Min(58,[Math]::Max(18,[int][Math]::Round((1800+$GpuCurvePercent*40)/100)))
    if ($SharedCurve) { $cpuCurveEc=$gpuCurveEc=[Math]::Min(58,[Math]::Max(18,[int][Math]::Round((1800+$SharedCurvePercent*40)/100))) }
    $lowestTarget=if($curveTrial){100*[Math]::Min($cpuCurveEc,$gpuCurveEc)}else{$TargetRpm}
    if ($report.Before.acPowerConnected -ne $true -or $report.Before.cpuTemperatureC -ge 85 -or
        $report.Before.gpuTemperatureC -ge 80 -or $report.Before.cpuFanRpm -lt 500 -or
        $report.Before.gpuFanRpm -lt 500 -or (-not $AutomaticCeiling -and
        ($report.Before.cpuFanRpm -ge ($lowestTarget-200) -or $report.Before.gpuFanRpm -ge ($lowestTarget-200)))) {
        throw 'AC, thermal, or RPM preflight refused'
    }
    if ($AutomaticCeiling) {
        if ($report.Before.cpuTemperatureC -ge 70 -or $report.Before.gpuTemperatureC -ge 65) {
            throw 'Automatic ceiling thermal preflight refused'
        }
        $report.CeilingExpectedActive=$report.Before.cpuFanRpm -gt $CeilingRpm+100 -or
            $report.Before.gpuFanRpm -gt $CeilingRpm+100
    }
    if ($taskWasRunning) { Stop-ScheduledTask -TaskName $taskName }
    $deadline=[DateTime]::UtcNow.AddSeconds(15)
    do {
        Start-Sleep -Milliseconds 200
        $running=$taskWasRunning -and (Get-ScheduledTask -TaskName $taskName).State -eq 'Running'
        $processes=@($thirdPartyNames | ForEach-Object { Get-Process -Name $_ -ErrorAction SilentlyContinue })
    } while (($running -or $processes.Count -gt 0) -and [DateTime]::UtcNow -lt $deadline)
    if ($running -or $processes.Count -gt 0) { throw 'Third-party writer did not stop' }
    $now=Snapshot
    if ($now.cpuTemperatureC -ge 85 -or $now.gpuTemperatureC -ge 80) { throw 'Thermal preflight changed' }
    $payload=[ordered]@{type='setFanControl';operationId=[Guid]::NewGuid();plan=[ordered]@{
        points=@(@{temperatureC=30;percent=43},@{temperatureC=100;percent=43});
        gpuPoints=$null;strategy='Fixed';fixedRpm=$TargetRpm};riskConfirmed=$true}
    if ($IndependentCurves) {
        $payload.plan=[ordered]@{points=@(@{temperatureC=30;percent=$CpuCurvePercent},@{temperatureC=100;percent=$CpuCurvePercent});
            gpuPoints=@(@{temperatureC=30;percent=$GpuCurvePercent},@{temperatureC=100;percent=$GpuCurvePercent});strategy='Curve';fixedRpm=$null}
        $report.TargetCpuRpm=100*$cpuCurveEc; $report.TargetGpuRpm=100*$gpuCurveEc
    }
    if ($SharedCurve) {
        $payload.plan=[ordered]@{points=@(@{temperatureC=30;percent=$SharedCurvePercent},@{temperatureC=100;percent=$SharedCurvePercent});
            gpuPoints=$null;strategy='Curve';fixedRpm=$null}
        $report.TargetCpuRpm=100*$cpuCurveEc; $report.TargetGpuRpm=100*$gpuCurveEc
    }
    if ($AutomaticCeiling) {
        $payload.plan=[ordered]@{points=@(@{temperatureC=30;percent=43},@{temperatureC=100;percent=43});
            gpuPoints=$null;strategy='Auto';fixedRpm=$null;maximumRpm=$CeilingRpm}
        $report.TargetCpuRpm=$CeilingRpm; $report.TargetGpuRpm=$CeilingRpm
    }
    if ($StrongCooling) {
        $payload=[ordered]@{type='setStrongCooling';operationId=[Guid]::NewGuid();enabled=$true}
        $report.StrongCoolingBefore=(Request 'getDeviceState' @{}).controls.strongCooling
    }
    $touched=$true
    $report.Apply=Request 'executeCommand' $payload
    if ($report.Apply.state -ne 'applied') { throw "Fan command rejected: $($report.Apply.error.code)" }
    $report.RegistersApplied=(Request 'getDeviceState' @{}).controls.fanEcControl
    if ($null -eq $report.RegistersApplied) { throw 'Applied fan command has no independent EC register readback' }
    if ($curveTrial -and ($report.RegistersApplied.CpuTarget -ne $cpuCurveEc -or
        $report.RegistersApplied.GpuTarget -ne $gpuCurveEc -or
        ([DateTimeOffset]::UtcNow-[DateTimeOffset]$report.RegistersApplied.CapturedAtUtc).TotalSeconds -ge 5)) {
        throw 'CPU/GPU curve targets not confirmed by fresh EC readback'
    }
    if ($SharedCurve) {
        $report.ActiveFanPlanApplied=(Request 'getDeviceState' @{}).controls.activeFanControlPlan
        if ($report.ActiveFanPlanApplied.strategy -ne 'Curve' -or $null -ne $report.ActiveFanPlanApplied.gpuPoints) {
            throw 'Shared curve plan was not retained by the service'
        }
    }
    if ($StrongCooling -and ($report.RegistersApplied.CpuTarget -ne 68 -or
        $report.RegistersApplied.GpuTarget -ne 68 -or
        ([DateTimeOffset]::UtcNow-[DateTimeOffset]$report.RegistersApplied.CapturedAtUtc).TotalSeconds -ge 5 -or
        (Request 'getDeviceState' @{}).controls.strongCooling -ne $true)) {
        throw 'Strong cooling lacked fresh EC targets or firmware state readback'
    }
    if ($AutomaticCeiling) {
        $report.ActiveFanPlanApplied=(Request 'getDeviceState' @{}).controls.activeFanControlPlan
        if ($report.ActiveFanPlanApplied.strategy -ne 'Auto' -or
            $report.ActiveFanPlanApplied.maximumRpm -ne $CeilingRpm) {
            throw 'Automatic ceiling plan was not retained by the service'
        }
        $expectedEc=if($report.CeilingExpectedActive){[int][Math]::Floor($CeilingRpm/100)}else{0}
        if ($report.RegistersApplied.CpuTarget -ne $expectedEc -or
            $report.RegistersApplied.GpuTarget -ne $expectedEc -or
            ([DateTimeOffset]::UtcNow-[DateTimeOffset]$report.RegistersApplied.CapturedAtUtc).TotalSeconds -ge 5) {
            throw 'Automatic ceiling EC target did not match the intervention condition'
        }
    }
    for ($second=1; $second -le $SampleSeconds; $second++) {
        Start-Sleep -Seconds 1
        $sample=Snapshot
        $report.Samples+=@{second=$second;cpuTemperatureC=$sample.cpuTemperatureC;gpuTemperatureC=$sample.gpuTemperatureC;cpuFanRpm=$sample.cpuFanRpm;gpuFanRpm=$sample.gpuFanRpm}
        if ($sample.cpuTemperatureC -ge 90 -or $sample.gpuTemperatureC -ge 84 -or $sample.acPowerConnected -ne $true) { throw 'Thermal or AC guard stopped trial' }
        if ($AutomaticCeiling -and ($sample.cpuTemperatureC -ge 75 -or $sample.gpuTemperatureC -ge 70 -or
            $sample.cpuTemperatureC-$report.Before.cpuTemperatureC -ge 5 -or
            $sample.gpuTemperatureC-$report.Before.gpuTemperatureC -ge 5)) {
            throw 'Automatic ceiling temperature guard stopped trial'
        }
    }
    if (-not ($StrongCooling -or $curveTrial -or $AutomaticCeiling)) {
        $last=$report.Samples[-1]
        $report.RpmRiseConfirmed=$last.cpuFanRpm -ge ($report.Before.cpuFanRpm+400) -and
            $last.gpuFanRpm -ge ($report.Before.gpuFanRpm+400)
        if (-not $report.RpmRiseConfirmed) { throw 'Independent tachometer did not confirm fan speed increase' }
    }
    if ($curveTrial) {
        $report.PeakCpuRpm=($report.Samples.cpuFanRpm | Measure-Object -Maximum).Maximum
        $report.PeakGpuRpm=($report.Samples.gpuFanRpm | Measure-Object -Maximum).Maximum
        $report.RpmRiseConfirmed=$report.PeakCpuRpm -ge [Math]::Max($report.TargetCpuRpm*0.8,$report.Before.cpuFanRpm+400) -and
            $report.PeakGpuRpm -ge [Math]::Max($report.TargetGpuRpm*0.8,$report.Before.gpuFanRpm+400)
        if (-not $report.RpmRiseConfirmed) { throw 'Independent curve targets lacked dual-fan tachometer response' }
        if ([Math]::Abs($report.TargetCpuRpm-$report.TargetGpuRpm) -ge 1000) {
            $report.ChannelSeparationConfirmed=if($report.TargetCpuRpm -gt $report.TargetGpuRpm){
                $report.PeakCpuRpm-$report.PeakGpuRpm -ge 500
            }else{$report.PeakGpuRpm-$report.PeakCpuRpm -ge 500}
            if (-not $report.ChannelSeparationConfirmed) { throw 'Independent curve targets lacked distinct channel response' }
        }
    }
    if ($StrongCooling) {
        $report.PeakCpuRpm=($report.Samples.cpuFanRpm | Measure-Object -Maximum).Maximum
        $report.PeakGpuRpm=($report.Samples.gpuFanRpm | Measure-Object -Maximum).Maximum
        $report.RpmRiseConfirmed=$report.PeakCpuRpm -ge [Math]::Max(4500,$report.Before.cpuFanRpm+1200) -and
            $report.PeakGpuRpm -ge [Math]::Max(4500,$report.Before.gpuFanRpm+1200)
        if (-not $report.RpmRiseConfirmed) { throw 'Strong cooling lacked dual-fan tachometer response' }
    }
    if ($AutomaticCeiling -and $report.CeilingExpectedActive) {
        $report.LowestCpuRpm=($report.Samples.cpuFanRpm | Measure-Object -Minimum).Minimum
        $report.LowestGpuRpm=($report.Samples.gpuFanRpm | Measure-Object -Minimum).Minimum
        $report.CeilingResponseConfirmed=$report.LowestCpuRpm -le [Math]::Min($CeilingRpm+200,$report.Before.cpuFanRpm-200) -and
            $report.LowestGpuRpm -le [Math]::Min($CeilingRpm+200,$report.Before.gpuFanRpm-200)
        if (-not $report.CeilingResponseConfirmed) { throw 'Automatic ceiling lacked dual-fan slowdown' }
    }
    if ($DisconnectRelease) {
        $script:trialPipe.Dispose(); $script:trialPipe=$null
        Start-Sleep -Seconds 2
        $report.DisconnectedOwner=$true
    } else {
        $report.Release=Release
        if ($report.Release.state -ne 'applied') { throw 'Fan release command failed' }
    }
    $report.After=Snapshot
    $report.RegistersAfter=(Request 'getDeviceState' @{}).controls.fanEcControl
    $report.RegistersReleased=$null -ne $report.RegistersAfter -and
        ($report.RegistersAfter.Control -band 0x0A) -eq 0 -and
        $report.RegistersAfter.CpuTarget -eq 0 -and $report.RegistersAfter.GpuTarget -eq 0 -and
        (($report.RegistersAfter.Initialization -band 0x80) -eq ($report.RegistersApplied.ExpectedInitialization -band 0x80)) -and
        ([DateTimeOffset]::UtcNow-[DateTimeOffset]$report.RegistersAfter.CapturedAtUtc).TotalSeconds -lt 5
    if (-not $report.RegistersReleased) { throw 'EC target/control release or initialization restore not confirmed' }
    $touched=$false
    if (-not ($StrongCooling -or $curveTrial -or $AutomaticCeiling)) {
        Start-Sleep -Seconds 8
        $report.SettledAfter=Snapshot
        $report.RpmRestored=[Math]::Abs($report.SettledAfter.cpuFanRpm-$report.Before.cpuFanRpm) -le 500 -and
            [Math]::Abs($report.SettledAfter.gpuFanRpm-$report.Before.gpuFanRpm) -le 500
        if (-not $report.RpmRestored) { throw 'Fan RPM did not return near the baseline' }
    }
    if ($curveTrial -or $StrongCooling -or $AutomaticCeiling) {
        $report.SettledSamples=@()
        for ($attempt=1; $attempt -le 10; $attempt++) {
            Start-Sleep -Seconds 2
            $report.SettledAfter=Snapshot
            $report.SettledSamples+=@{second=$attempt*2;cpuFanRpm=$report.SettledAfter.cpuFanRpm;
                gpuFanRpm=$report.SettledAfter.gpuFanRpm}
            $restoreTolerance=if($AutomaticCeiling -and $report.CeilingExpectedActive){250}else{500}
            $report.RpmRestored=[Math]::Abs($report.SettledAfter.cpuFanRpm-$report.Before.cpuFanRpm) -le $restoreTolerance -and
                [Math]::Abs($report.SettledAfter.gpuFanRpm-$report.Before.gpuFanRpm) -le $restoreTolerance
            if ($report.RpmRestored) { break }
        }
        if (-not $report.RpmRestored) { throw 'Fan release did not restore both fan speeds within 20 seconds' }
    }
    if ($StrongCooling) {
        $report.StrongCoolingAfter=(Request 'getDeviceState' @{}).controls.strongCooling
        if ($report.StrongCoolingAfter -ne $false) { throw 'Strong cooling firmware state did not restore to off' }
    }
    if ($AutomaticCeiling) {
        $report.ActiveFanPlanAfter=(Request 'getDeviceState' @{}).controls.activeFanControlPlan
        if ($null -ne $report.ActiveFanPlanAfter) { throw 'Automatic ceiling plan remained active after release' }
    }
    $report.ModeAfter=CurrentMode
    if ($report.ModeAfter -ne $report.ModeBefore) { throw 'Performance mode changed during fan trial' }
} catch {
    $report.Error=$_.Exception.ToString()
} finally {
    if ($touched) {
        try {
            $report.FallbackRelease=Release
            if ($report.Apply.state -eq 'applied') {
                $report.FallbackRegisters=(Request 'getDeviceState' @{}).controls.fanEcControl
                $report.FallbackRegistersReleased=$null -ne $report.FallbackRegisters -and
                    ($report.FallbackRegisters.Control -band 0x0A) -eq 0 -and
                    $report.FallbackRegisters.CpuTarget -eq 0 -and $report.FallbackRegisters.GpuTarget -eq 0
                if (-not $report.FallbackRegistersReleased) { throw 'Fallback EC release not independently confirmed' }
                if ($curveTrial -or $StrongCooling -or $AutomaticCeiling) {
                    for ($attempt=1; $attempt -le 10; $attempt++) {
                        Start-Sleep -Seconds 2
                        $report.FallbackSettledAfter=Snapshot
                        $restoreTolerance=if($AutomaticCeiling -and $report.CeilingExpectedActive){250}else{500}
                        $report.FallbackRpmRestored=[Math]::Abs($report.FallbackSettledAfter.cpuFanRpm-$report.Before.cpuFanRpm) -le $restoreTolerance -and
                            [Math]::Abs($report.FallbackSettledAfter.gpuFanRpm-$report.Before.gpuFanRpm) -le $restoreTolerance
                        if ($report.FallbackRpmRestored) { break }
                    }
                    if (-not $report.FallbackRpmRestored) { throw 'Fallback release did not restore both fan speeds within 20 seconds' }
                }
                if ($StrongCooling) {
                    $report.FallbackStrongCoolingAfter=(Request 'getDeviceState' @{}).controls.strongCooling
                    if ($report.FallbackStrongCoolingAfter -ne $false) { throw 'Fallback strong cooling firmware state did not restore to off' }
                }
            }
        } catch { $report.FallbackReleaseError=$_.Exception.ToString() }
    }
    if ($null -ne $script:trialPipe) { $script:trialPipe.Dispose(); $script:trialPipe=$null }
    try {
        if ($taskWasRunning) {
            if ((Get-ScheduledTask -TaskName $taskName).State -ne 'Running') { Start-ScheduledTask -TaskName $taskName }
            $deadline=[DateTime]::UtcNow.AddSeconds(15)
            do {
                Start-Sleep -Milliseconds 250
                $report.TaskRestored=(Get-ScheduledTask -TaskName $taskName).State -eq 'Running'
            } while (-not $report.TaskRestored -and [DateTime]::UtcNow -lt $deadline)
        } else {
            $report.TaskRestored=$null -eq (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue)
        }
    } catch { $report.TaskRestoreError=$_.Exception.ToString() }
    $report.ConfigUnchanged=(Get-FileHash -LiteralPath $configPath -Algorithm SHA256).Hash -eq $configHash
    $report.ServiceRunning=(Get-Service JiaolongControlService).Status -eq 'Running'
    $report.FinishedAtUtc=[DateTimeOffset]::UtcNow
    $report | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $Result -Encoding utf8
}
if ($report.Error -or $report.FallbackReleaseError -or -not $report.TaskRestored -or -not $report.ConfigUnchanged -or -not $report.ServiceRunning) { exit 1 }
