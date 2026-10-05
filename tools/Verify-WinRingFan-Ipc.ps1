param([Parameter(Mandatory)][string]$Result, [switch]$StrongCooling, [switch]$IndependentCurves, [switch]$DisconnectRelease,
    [ValidateRange(3500,5800)][int]$TargetRpm=3500, [ValidateRange(1,10)][int]$SampleSeconds=8)
$ErrorActionPreference = 'Stop'
$taskName = 'JiaoLongPlus'
$configPath = 'F:\JiaoLong7.3\AppSettings.json'
$tempRoot = 'C:\Users\Administrator\AppData\Local\Temp\'
if (-not [IO.Path]::GetFullPath($Result).StartsWith($tempRoot,[StringComparison]::OrdinalIgnoreCase)) { throw 'Result must stay in C: Temp' }
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Administrator token required' }
if ((Get-CimInstance Win32_BaseBoard).Product -ne 'MRID6-23' -or
    (Get-CimInstance Win32_BIOS).SMBIOSBIOSVersion -ne 'MRID6_23_P_V39') { throw 'Unsupported hardware identity' }
$task = Get-ScheduledTask -TaskName $taskName
if ($task.State -ne 'Running' -or $task.Actions.Execute -ne 'F:\JiaoLong7.3\第三方蛟龙游戏控制中心.exe') { throw 'Unexpected third-party task baseline' }
$configHash = (Get-FileHash -LiteralPath $configPath -Algorithm SHA256).Hash
$report = [ordered]@{ StartedAtUtc=[DateTimeOffset]::UtcNow; Before=$null; Apply=$null; Samples=@(); Release=$null; TaskRestored=$false; ConfigUnchanged=$false; ServiceRunning=$false }
function CurrentMode { (Request 'getDeviceState' @{}).controls.performanceMode }
function Request([string]$operation, [object]$payload) {
    $newConnection = $null -eq $script:trialPipe
    if ($newConnection) { $script:trialPipe=[IO.Pipes.NamedPipeClientStream]::new('.','Jiaolong.ControlCenter.v1',[IO.Pipes.PipeDirection]::InOut) }
    $pipe=$script:trialPipe
    try {
        if ($newConnection) { $pipe.Connect(3000) }
        function Send($message) {
            $bytes=[Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject $message -Depth 20 -Compress))
            $size=[BitConverter]::GetBytes([int]$bytes.Length)
            $pipe.Write($size,0,4); $pipe.Write($bytes,0,$bytes.Length); $pipe.Flush()
        }
        function Receive {
            $size=New-Object byte[] 4; $pipe.ReadExactly($size)
            $length=[BitConverter]::ToInt32($size,0)
            if ($length -le 0 -or $length -gt 1048576) { throw 'Invalid IPC length' }
            $bytes=New-Object byte[] $length; $pipe.ReadExactly($bytes)
            [Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json
        }
        $version=[ordered]@{major=1;minor=0}
        if ($newConnection) {
            Send ([ordered]@{kind='hello';protocolVersion=$version;messageId=[Guid]::NewGuid();sentAtUtc=[DateTimeOffset]::UtcNow;clientVersion='winring-fan-verification'})
            if ((Receive).kind -ne 'helloAck') { throw 'IPC handshake failed' }
        }
        Send ([ordered]@{kind='request';protocolVersion=$version;messageId=[Guid]::NewGuid();sentAtUtc=[DateTimeOffset]::UtcNow;operationId=[Guid]::NewGuid();operation=$operation;deadlineUtc=[DateTimeOffset]::UtcNow.AddSeconds(9);payload=$payload})
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
$touched=$false
try {
    $report.Before=Snapshot
    $report.ModeBefore=CurrentMode
    $lowestTarget=if($IndependentCurves){5600}else{$TargetRpm}
    if ($report.Before.acPowerConnected -ne $true -or $report.Before.cpuTemperatureC -ge 85 -or
        $report.Before.gpuTemperatureC -ge 80 -or $report.Before.cpuFanRpm -lt 500 -or
        $report.Before.gpuFanRpm -lt 500 -or $report.Before.cpuFanRpm -ge ($lowestTarget-200) -or $report.Before.gpuFanRpm -ge ($lowestTarget-200)) {
        throw 'AC, thermal, or RPM preflight refused'
    }
    Stop-ScheduledTask -TaskName $taskName
    $deadline=[DateTime]::UtcNow.AddSeconds(15)
    do {
        Start-Sleep -Milliseconds 200
        $running=(Get-ScheduledTask -TaskName $taskName).State -eq 'Running'
        $processes=@(Get-Process -Name '第三方蛟龙游戏控制中心' -ErrorAction SilentlyContinue)
    } while (($running -or $processes.Count -gt 0) -and [DateTime]::UtcNow -lt $deadline)
    if ($running -or $processes.Count -gt 0) { throw 'Third-party writer did not stop' }
    $now=Snapshot
    if ($now.cpuTemperatureC -ge 85 -or $now.gpuTemperatureC -ge 80) { throw 'Thermal preflight changed' }
    $payload=[ordered]@{type='setFanControl';operationId=[Guid]::NewGuid();plan=[ordered]@{
        points=@(@{temperatureC=30;percent=43},@{temperatureC=100;percent=43});
        gpuPoints=$null;strategy='Fixed';fixedRpm=$TargetRpm};riskConfirmed=$true}
    if ($IndependentCurves) {
        $payload.plan=[ordered]@{points=@(@{temperatureC=30;percent=100},@{temperatureC=100;percent=100});
            gpuPoints=@(@{temperatureC=30;percent=95},@{temperatureC=100;percent=95});strategy='Curve';fixedRpm=$null}
        $report.TargetCpuRpm=5800; $report.TargetGpuRpm=5600
    }
    if ($StrongCooling) {
        $payload=[ordered]@{type='setStrongCooling';operationId=[Guid]::NewGuid();enabled=$true}
        $report.StrongCoolingBefore=(Request 'getDeviceState' @{}).controls.strongCooling
    }
    $touched=$true
    $report.Apply=Request 'executeCommand' $payload
    if ($report.Apply.state -ne 'applied') { throw "Fan command rejected: $($report.Apply.error.code)" }
    $report.RegistersApplied=(Request 'getDeviceState' @{}).controls.fanEcControl
    for ($second=1; $second -le $SampleSeconds; $second++) {
        Start-Sleep -Seconds 1
        $sample=Snapshot
        $report.Samples+=@{second=$second;cpuTemperatureC=$sample.cpuTemperatureC;gpuTemperatureC=$sample.gpuTemperatureC;cpuFanRpm=$sample.cpuFanRpm;gpuFanRpm=$sample.gpuFanRpm}
        if ($sample.cpuTemperatureC -ge 90 -or $sample.gpuTemperatureC -ge 84 -or $sample.acPowerConnected -ne $true) { throw 'Thermal or AC guard stopped trial' }
    }
    if ($DisconnectRelease) {
        $script:trialPipe.Dispose(); $script:trialPipe=$null
        Start-Sleep -Seconds 2
        $report.DisconnectedOwner=$true
    } else {
        $report.Release=Release
        if ($report.Release.state -ne 'applied') { throw 'Fan release command failed' }
        $touched=$false
    }
    $report.After=Snapshot
    $report.RegistersAfter=(Request 'getDeviceState' @{}).controls.fanEcControl
    $report.RegistersReleased=($report.RegistersAfter.Control -band 0x0A) -eq 0 -and
        $report.RegistersAfter.CpuTarget -eq 0 -and $report.RegistersAfter.GpuTarget -eq 0 -and
        (($report.RegistersAfter.Initialization -band 0x80) -eq ($report.RegistersApplied.ExpectedInitialization -band 0x80)) -and
        $null -ne $report.RegistersAfter -and
        ([DateTimeOffset]::UtcNow-[DateTimeOffset]$report.RegistersAfter.CapturedAtUtc).TotalSeconds -lt 5
    if (-not $report.RegistersReleased) { throw 'EC target/control release or initialization restore not confirmed' }
    if ($StrongCooling) { $report.StrongCoolingAfter=(Request 'getDeviceState' @{}).controls.strongCooling }
    $report.ModeAfter=CurrentMode
} catch {
    $report.Error=$_.Exception.ToString()
} finally {
    if ($touched) {
        try { $report.FallbackRelease=Release } catch { $report.FallbackReleaseError=$_.Exception.ToString() }
    }
    if ($null -ne $script:trialPipe) { $script:trialPipe.Dispose(); $script:trialPipe=$null }
    try {
        if ((Get-ScheduledTask -TaskName $taskName).State -ne 'Running') { Start-ScheduledTask -TaskName $taskName }
        $deadline=[DateTime]::UtcNow.AddSeconds(15)
        do {
            Start-Sleep -Milliseconds 250
            $report.TaskRestored=(Get-ScheduledTask -TaskName $taskName).State -eq 'Running'
        } while (-not $report.TaskRestored -and [DateTime]::UtcNow -lt $deadline)
    } catch { $report.TaskRestoreError=$_.Exception.ToString() }
    $report.ConfigUnchanged=(Get-FileHash -LiteralPath $configPath -Algorithm SHA256).Hash -eq $configHash
    $report.ServiceRunning=(Get-Service JiaolongControlService).Status -eq 'Running'
    $report.FinishedAtUtc=[DateTimeOffset]::UtcNow
    $report | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $Result -Encoding utf8
}
if ($report.Error -or $report.FallbackReleaseError -or -not $report.TaskRestored -or -not $report.ConfigUnchanged -or -not $report.ServiceRunning) { exit 1 }
