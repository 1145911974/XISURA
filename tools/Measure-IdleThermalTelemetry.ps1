param(
    [ValidateRange(2, 12)][int]$Samples = 6,
    [ValidateRange(1, 60)][int]$IntervalSeconds = 5,
    [ValidateRange(1, 100)][int]$MaxSystemCpuPercent = 20
)

$ErrorActionPreference = 'Stop'
function Read-Frame($pipe) {
    function Read-Bytes([int]$length) {
        $buffer = [byte[]]::new($length)
        $offset = 0
        while ($offset -lt $length) {
            $read = $pipe.ReadAsync($buffer, $offset, $length - $offset).WaitAsync([TimeSpan]::FromSeconds(10)).GetAwaiter().GetResult()
            if ($read -eq 0) { throw 'Service connection closed' }
            $offset += $read
        }
        return ,$buffer
    }
    $length = [BitConverter]::ToInt32((Read-Bytes 4), 0)
    if ($length -le 0 -or $length -gt 1048576) { throw 'Invalid service frame length' }
    [Text.Encoding]::UTF8.GetString((Read-Bytes $length)) | ConvertFrom-Json
}
function Send-Frame($pipe, $value) {
    $bytes = [Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject $value -Depth 12 -Compress))
    $size = [BitConverter]::GetBytes([int]$bytes.Length)
    $pipe.Write($size, 0, 4)
    $pipe.Write($bytes, 0, $bytes.Length)
    $pipe.Flush()
}
function Get-ServiceState {
    $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', 'Jiaolong.ControlCenter.v1',
        [IO.Pipes.PipeDirection]::InOut, [IO.Pipes.PipeOptions]::Asynchronous)
    try {
        $pipe.Connect(3000)
        $version = [ordered]@{ major = 1; minor = 0 }
        Send-Frame $pipe ([ordered]@{ kind = 'hello'; protocolVersion = $version;
            messageId = [Guid]::NewGuid(); sentAtUtc = [DateTimeOffset]::UtcNow; clientVersion = 'idle-thermal-readonly' })
        if ((Read-Frame $pipe).kind -ne 'helloAck') { throw 'Service handshake failed' }
        Send-Frame $pipe ([ordered]@{ kind = 'request'; protocolVersion = $version;
            messageId = [Guid]::NewGuid(); sentAtUtc = [DateTimeOffset]::UtcNow;
            operationId = [Guid]::NewGuid(); operation = 'getDeviceState';
            deadlineUtc = [DateTimeOffset]::UtcNow.AddSeconds(9); payload = @{} })
        $response = Read-Frame $pipe
        if ($response.status -ne 'success') { throw "Service read failed: $($response.error.code)" }
        return $response.payload
    }
    finally { $pipe.Dispose() }
}

$rows = @()
$servicePid = $null
$mode = $null
$adaptive = $null
$strongCooling = $null
for ($i = 0; $i -lt $Samples; $i++) {
    if (Get-Process -Name '007FirstLight' -ErrorAction SilentlyContinue) { throw 'Idle sample refused: game process is running' }
    $service = @(Get-CimInstance Win32_PerfFormattedData_PerfProc_Process |
        Where-Object { $_.Name -eq 'Jiaolong.Service' })
    if ($service.Count -ne 1 -or $null -ne $servicePid -and $servicePid -ne $service[0].IDProcess) {
        throw 'Idle sample refused: service unavailable or restarted'
    }
    $servicePid = $service[0].IDProcess
    $load = @(Get-CimInstance Win32_Processor | ForEach-Object LoadPercentage)
    if ($load.Count -eq 0 -or $load -contains $null) { throw 'System CPU load unavailable' }
    $systemCpu = [Math]::Round(($load | Measure-Object -Average).Average, 1)
    if ($systemCpu -gt $MaxSystemCpuPercent) { throw "Idle sample refused: system CPU $systemCpu% exceeds $MaxSystemCpuPercent%" }
    $state = Get-ServiceState
    $telemetry = $state.telemetry
    $ageSeconds = ([DateTimeOffset]::UtcNow - [DateTimeOffset]$telemetry.capturedAtUtc).TotalSeconds
    if ($ageSeconds -lt 0 -or $ageSeconds -gt 3 -or $telemetry.acPowerConnected -ne $true) {
        throw 'Idle sample refused: telemetry stale or AC disconnected'
    }
    foreach ($field in @('cpuTemperatureC','gpuTemperatureC','cpuFanRpm','gpuFanRpm')) {
        if ($null -eq $telemetry.$field -or -not [double]::IsFinite([double]$telemetry.$field)) {
            throw "Telemetry unavailable: $field"
        }
    }
    if ($null -ne $mode -and $mode -ne $state.controls.performanceMode -or
        $null -ne $adaptive -and $adaptive -ne $state.controls.adaptiveAutomation.enabled -or
        $null -ne $strongCooling -and $strongCooling -ne $state.controls.strongCooling) {
        throw 'Idle sample refused: mode, automation or strong cooling changed'
    }
    $mode = $state.controls.performanceMode
    $adaptive = $state.controls.adaptiveAutomation.enabled
    $strongCooling = $state.controls.strongCooling
    $rows += [pscustomobject]@{
        capturedAtUtc = $telemetry.capturedAtUtc
        systemCpuPercent = $systemCpu
        serviceCpuPercent = [double]$service[0].PercentProcessorTime
        servicePrivateBytes = [long]$service[0].PrivateBytes
        cpuC = [double]$telemetry.cpuTemperatureC
        gpuC = [double]$telemetry.gpuTemperatureC
        cpuFanRpm = [int]$telemetry.cpuFanRpm
        gpuFanRpm = [int]$telemetry.gpuFanRpm
    }
    if ($i -lt $Samples - 1) { Start-Sleep -Seconds $IntervalSeconds }
}
[ordered]@{ servicePid = $servicePid; mode = $mode; adaptiveEnabled = $adaptive; strongCooling = $strongCooling;
    samples = $Samples; intervalSeconds = $IntervalSeconds; maxSystemCpuPercent = $MaxSystemCpuPercent;
    readings = $rows } | ConvertTo-Json -Depth 4
