param([switch]$Trial)
$ErrorActionPreference = 'Stop'
$preferencesPath = 'C:\Users\Administrator\AppData\Local\Jiaolong Control Center\preferences.json'

function Invoke-Service([string]$operation, [object]$payload) {
    $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', 'Jiaolong.ControlCenter.v1',
        [IO.Pipes.PipeDirection]::InOut, [IO.Pipes.PipeOptions]::Asynchronous)
    function Send-Frame($value) {
        $bytes = [Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject $value -Depth 20 -Compress))
        $size = [BitConverter]::GetBytes([int]$bytes.Length)
        $pipe.Write($size, 0, 4); $pipe.Write($bytes, 0, $bytes.Length); $pipe.Flush()
    }
    function Read-Bytes([int]$length) {
        $bytes = [byte[]]::new($length); $offset = 0
        while ($offset -lt $length) {
            $read = $pipe.ReadAsync($bytes, $offset, $length - $offset).WaitAsync([TimeSpan]::FromSeconds(20)).GetAwaiter().GetResult()
            if ($read -eq 0) { throw 'Service connection closed' }
            $offset += $read
        }
        return ,$bytes
    }
    function Read-Frame {
        $size = Read-Bytes 4
        $length = [BitConverter]::ToInt32($size, 0)
        if ($length -le 0 -or $length -gt 1048576) { throw 'Invalid IPC frame length' }
        [Text.Encoding]::UTF8.GetString((Read-Bytes $length)) | ConvertFrom-Json
    }
    try {
        $pipe.Connect(3000)
        $version = [ordered]@{ major = 1; minor = 0 }
        Send-Frame ([ordered]@{ kind = 'hello'; protocolVersion = $version; messageId = [Guid]::NewGuid();
            sentAtUtc = [DateTimeOffset]::UtcNow; clientVersion = 'gpu-core-offset-acceptance' })
        if ((Read-Frame).kind -ne 'helloAck') { throw 'Service handshake failed' }
        $operationId = if ($operation -eq 'executeCommand') { $payload.operationId } else { [Guid]::NewGuid() }
        Send-Frame ([ordered]@{ kind = 'request'; protocolVersion = $version; messageId = [Guid]::NewGuid();
            sentAtUtc = [DateTimeOffset]::UtcNow; operationId = $operationId; operation = $operation;
            deadlineUtc = [DateTimeOffset]::UtcNow.AddSeconds(15); payload = $payload })
        $response = Read-Frame
        if ($response.status -ne 'success') { throw "Service request failed: $($response.error.code)" }
        return $response.payload
    }
    finally { $pipe.Dispose() }
}

function Get-DeviceState { Invoke-Service 'getDeviceState' @{} }
function Get-OemPstate { & "$PSScriptRoot\Read-OemGpuPstate.ps1" | ConvertFrom-Json }
function Get-Curve($state) { return ,@($state.controls.gpuVf.nodes | ForEach-Object { [int]$_.offsetKhz }) }
function Get-CurveHash([int[]]$curve) {
    $bytes = [Text.Encoding]::UTF8.GetBytes(($curve -join ','))
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
}
function Set-CoreOffset([int]$expected, [int]$target, [int[]]$curve) {
    $operationId = [Guid]::NewGuid()
    Invoke-Service 'executeCommand' ([ordered]@{ type = 'setGpuCoreOffset'; operationId = $operationId;
        expectedOffsetKhz = $expected; offsetKhz = $target; riskConfirmed = $true;
        expectedVfOffsetsKhz = $curve })
}
function Snapshot($state, $oem) {
    $curve = Get-Curve $state
    [ordered]@{ mode = $state.controls.performanceMode; adaptive = $state.controls.adaptiveAutomation.enabled;
        coreKhz = $state.controls.gpuVf.coreOffsetKhz; memoryKhz = $state.controls.gpuVf.memoryOffsetKhz;
        curveNodes = $curve.Count; curveHash = Get-CurveHash $curve; gpuError = $state.controls.gpuVf.error;
        gpuOwnedFields = @($state.controls.executionState.fields.PSObject.Properties.Name | Where-Object { $_ -match '^gpu\.' });
        gpuTemperatureC = $state.telemetry.gpuTemperatureC; ac = $state.telemetry.acPowerConnected;
        oemCoreRaw = $oem.coreRaw; oemMemoryRaw = $oem.memoryRaw }
}

$beforeState = Get-DeviceState
$beforeOem = Get-OemPstate
$beforeCurve = Get-Curve $beforeState
$preferences = Get-Content -LiteralPath $preferencesPath -Raw | ConvertFrom-Json
$preferencesHash = (Get-FileHash -LiteralPath $preferencesPath -Algorithm SHA256).Hash
$service = Get-CimInstance Win32_Service -Filter "Name='JiaolongControlService'"
$report = [ordered]@{ capturedAtUtc = [DateTimeOffset]::UtcNow; trialRequested = [bool]$Trial;
    servicePid = $service.ProcessId; gpuFollowPreset = $preferences.gpuFollowPreset;
    before = Snapshot $beforeState $beforeOem; preflightRefusal = @(); apply = $null; during = $null;
    restore = $null; after = $null; error = $null; restorationError = $null }
$reasons = @()
if ($service.State -ne 'Running' -or $service.ProcessId -le 0) { $reasons += 'serviceUnavailable' }
$gpuNames = & nvidia-smi --query-gpu=name --format=csv,noheader
$gpuQueryExit = $LASTEXITCODE
$gpuName = ($gpuNames | Select-Object -First 1).Trim()
if ((Get-CimInstance Win32_BaseBoard).Product -ne 'MRID6-23' -or
    (Get-CimInstance Win32_BIOS).SMBIOSBIOSVersion -ne 'MRID6_23_P_V39' -or
    $gpuQueryExit -ne 0 -or $gpuName -ne 'NVIDIA GeForce RTX 4070 Laptop GPU') { $reasons += 'deviceIdentity' }
if ($beforeState.controls.performanceMode -ne 'balanced' -or $beforeState.controls.adaptiveAutomation.enabled -eq $true) {
    $reasons += 'modeOrAutomation'
}
if (@($beforeState.controls.executionState.pages | Where-Object { $_.page -eq 'gpu' }).Count -ne 0 -or
    @($beforeState.controls.executionState.fields.PSObject.Properties.Name | Where-Object { $_ -match '^gpu\.' }).Count -ne 0) {
    $reasons += 'gpuExecutionAlreadyOwned'
}
if (Get-Process -Name 'Jiaolong.ControlCenter','007FirstLight' -ErrorAction SilentlyContinue) { $reasons += 'clientOrGameRunning' }
$age = ([DateTimeOffset]::UtcNow - [DateTimeOffset]$beforeState.telemetry.capturedAtUtc).TotalSeconds
if ($beforeState.telemetry.acPowerConnected -ne $true -or $age -lt 0 -or $age -gt 3 -or
    $null -eq $beforeState.telemetry.gpuTemperatureC -or
    $beforeState.telemetry.gpuTemperatureC -ge 65) { $reasons += 'powerThermalOrStaleTelemetry' }
if ($beforeState.controls.gpuVf.error -or $beforeCurve.Count -ne 127 -or
    $null -eq $beforeState.controls.gpuVf.coreOffsetKhz -or $null -eq $beforeState.controls.gpuVf.memoryOffsetKhz -or
    $beforeOem.coreRaw -ne 0 -or $beforeOem.memoryRaw -ne 0 -or
    $beforeState.controls.gpuVf.coreOffsetKhz -ne 0 -or $beforeState.controls.gpuVf.memoryOffsetKhz -ne 0) {
    $reasons += 'gpuBaselineUnavailable'
}
$target = 10000
if ($null -eq $beforeState.controls.gpuVf.coreMinimumOffsetKhz -or
    $null -eq $beforeState.controls.gpuVf.coreMaximumOffsetKhz -or
    $beforeState.controls.gpuVf.coreMinimumOffsetKhz -gt $target -or
    $beforeState.controls.gpuVf.coreMaximumOffsetKhz -lt $target -or
    @($beforeCurve | Select-Object -Skip 1 | Where-Object { ($_ + $target) -gt 200000 }).Count -ne 0) {
    $reasons += 'targetOutsideVerifiedRange'
}
$report.preflightRefusal = $reasons
if ($reasons.Count -gt 0 -or -not $Trial) {
    $report | ConvertTo-Json -Depth 8
    if ($Trial -and $reasons.Count -gt 0) { throw "GPU trial preflight refused: $($reasons -join ',')" }
    return
}

$writeAttempted = $false
try {
    $writeAttempted = $true
    $report.apply = Set-CoreOffset 0 $target $beforeCurve
    if ($report.apply.state -ne 'applied' -or $report.apply.error) { throw "GPU apply not confirmed: $($report.apply.state)" }
    $duringState = Get-DeviceState
    $duringOem = Get-OemPstate
    $report.during = Snapshot $duringState $duringOem
    $duringCurve = Get-Curve $duringState
    if ($duringState.controls.gpuVf.coreOffsetKhz -ne $target -or
        $duringState.controls.gpuVf.memoryOffsetKhz -ne 0 -or
        $duringState.controls.gpuVf.error -or $duringOem.coreRaw -ne $target -or $duringOem.memoryRaw -ne 0 -or
        $duringCurve.Count -ne 127 -or
        $duringCurve[0] -ne $beforeCurve[0] -or
        @((1..126) | Where-Object { $duringCurve[$_] -ne $beforeCurve[$_] + $target }).Count -ne 0) {
        throw 'GPU service or independent OEM nonzero readback mismatch'
    }
}
catch { $report.error = $_.Exception.Message }
finally {
    if ($writeAttempted) {
        try {
            $current = Get-DeviceState
            $currentCurve = Get-Curve $current
            $currentCore = $current.controls.gpuVf.coreOffsetKhz
            if ($currentCore -eq $target -and $current.controls.gpuVf.memoryOffsetKhz -eq 0) {
                $report.restore = Set-CoreOffset $target 0 $currentCurve
                if ($report.restore.state -ne 'applied' -or $report.restore.error) {
                    throw "GPU restore not confirmed: $($report.restore.state)"
                }
            }
            elseif ($currentCore -ne 0 -or $current.controls.gpuVf.memoryOffsetKhz -ne 0) {
                throw 'GPU state changed outside trial; automatic restore refused'
            }
            $afterState = Get-DeviceState
            $afterOem = Get-OemPstate
            $afterCurve = Get-Curve $afterState
            $report.after = Snapshot $afterState $afterOem
            if ($afterState.controls.gpuVf.coreOffsetKhz -ne 0 -or $afterState.controls.gpuVf.memoryOffsetKhz -ne 0 -or
                $afterState.controls.gpuVf.error -or $afterOem.coreRaw -ne 0 -or $afterOem.memoryRaw -ne 0 -or
                ($afterCurve -join ',') -ne ($beforeCurve -join ',') -or
                @($report.after.gpuOwnedFields).Count -ne 0 -or
                $afterState.controls.performanceMode -ne $beforeState.controls.performanceMode -or
                $afterState.controls.adaptiveAutomation.enabled -ne $beforeState.controls.adaptiveAutomation.enabled -or
                (Get-FileHash -LiteralPath $preferencesPath -Algorithm SHA256).Hash -ne $preferencesHash -or
                (Get-CimInstance Win32_Service -Filter "Name='JiaolongControlService'").ProcessId -ne $service.ProcessId) {
                throw 'GPU final state, preferences or service process mismatch'
            }
        }
        catch { $report.restorationError = $_.Exception.Message }
    }
}
$report | ConvertTo-Json -Depth 8
if ($report.error -or $report.restorationError) { throw 'GPU core offset trial did not fully verify' }
