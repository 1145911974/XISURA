param([switch]$Trial)
$ErrorActionPreference = 'Stop'
$subgroup = '54533251-82be-4824-96c1-47b60b740d00'
$boost = 'be337238-0d82-4146-a960-4f3749d470c7'
$frequency = '75b0ae3f-bce0-45a7-8c89-c9611c25e100'
$parking = '0cc5b647-c1df-4637-891a-dec35c318583'
$preferencesPath = 'C:\Users\Administrator\AppData\Local\Jiaolong Control Center\preferences.json'

function Get-Index([string]$scheme, [string]$setting) {
    $lines = & powercfg /qh $scheme $subgroup $setting
    if ($LASTEXITCODE -ne 0) { throw "powercfg query failed: $setting" }
    $values = @($lines | Where-Object { $_ -match '当前.*设置索引:\s*0x([0-9a-fA-F]+)' } |
        ForEach-Object { [Convert]::ToInt32($Matches[1], 16) })
    if ($values.Count -ne 2) { throw "AC/DC index unavailable: $setting" }
    return ,$values
}

function Get-Power {
    $active = (& powercfg /getactivescheme) -join ' '
    if ($LASTEXITCODE -ne 0 -or $active -notmatch '[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}')
    { throw 'Active scheme unavailable' }
    $scheme = $Matches[0].ToLowerInvariant()
    $b = Get-Index $scheme $boost; $f = Get-Index $scheme $frequency; $p = Get-Index $scheme $parking
    return [pscustomobject]@{ Scheme=$scheme; AcBoost=$b[0]; DcBoost=$b[1];
        AcMax=$f[0]; DcMax=$f[1]; AcParking=$p[0]; DcParking=$p[1] }
}

function Assert-Unrelated($before, $after) {
    if ($before.Scheme -ne $after.Scheme -or $before.AcMax -ne $after.AcMax -or
        $before.DcMax -ne $after.DcMax -or $before.AcParking -ne $after.AcParking -or
        $before.DcParking -ne $after.DcParking) { throw 'Unrelated Windows power setting changed' }
}

function Request([string]$operation, [object]$payload) {
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
            $read = $pipe.ReadAsync($bytes, $offset, $length - $offset).WaitAsync([TimeSpan]::FromSeconds(10)).GetAwaiter().GetResult()
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
        $version = [ordered]@{ major=1; minor=0 }
        Send-Frame ([ordered]@{ kind='hello'; protocolVersion=$version; messageId=[Guid]::NewGuid();
            sentAtUtc=[DateTimeOffset]::UtcNow; clientVersion='cpu-boost-acceptance' })
        if ((Read-Frame).kind -ne 'helloAck') { throw 'Service handshake failed' }
        $operationId = if ($operation -eq 'executeCommand') { $payload.operationId } else { [Guid]::NewGuid() }
        Send-Frame ([ordered]@{ kind='request'; protocolVersion=$version; messageId=[Guid]::NewGuid();
            sentAtUtc=[DateTimeOffset]::UtcNow; operationId=$operationId; operation=$operation;
            deadlineUtc=[DateTimeOffset]::UtcNow.AddSeconds(9); payload=$payload })
        $response = Read-Frame
        if ($response.status -ne 'success') { throw "Service request failed: $($response.error.code)" }
        return $response.payload
    }
    finally { $pipe.Dispose() }
}

function Set-Boost([bool]$enabled) {
    $result = Request 'executeCommand' ([ordered]@{ type='setCpuTuning'; operationId=[Guid]::NewGuid();
        plan=[ordered]@{ boostEnabled=$enabled }; riskConfirmed=$true })
    return [pscustomobject]@{ state=$result.state; error=$result.error; mutation=$result.mutation }
}

$before = Get-Power
$state = Request 'getDeviceState' @{}
$cpu = $state.controls.cpuTuning
$reasons = @()
if ((Get-CimInstance Win32_BaseBoard).Product -ne 'MRID6-23' -or
    (Get-CimInstance Win32_BIOS).SMBIOSBIOSVersion -ne 'MRID6_23_P_V39') { $reasons += 'deviceIdentity' }
if ($before.Scheme -ne '381b4222-f694-41f0-9685-ff5bb260df2e' -or
    $before.AcBoost -ne 2 -or $before.DcBoost -ne 2) { $reasons += 'windowsBaseline' }
if ($cpu.AcBoostMode -ne $before.AcBoost -or $cpu.DcBoostMode -ne $before.DcBoost -or
    $cpu.BoostEnabled -ne $true) { $reasons += 'serviceReadback' }
if ($state.controls.adaptiveAutomation.enabled -eq $true) { $reasons += 'automationEnabled' }
if ($state.controls.executionState.fields.'performance.isBoostEnabled') { $reasons += 'boostAlreadyOwned' }
if (Get-Process -Name 'Jiaolong.ControlCenter' -ErrorAction SilentlyContinue) { $reasons += 'clientRunning' }
$age = ([DateTimeOffset]::UtcNow - [DateTimeOffset]$state.telemetry.capturedAtUtc).TotalSeconds
if ($state.telemetry.acPowerConnected -ne $true -or $null -eq $state.telemetry.cpuTemperatureC -or
    $state.telemetry.cpuTemperatureC -ge 75 -or $age -gt 5)
{ $reasons += 'powerThermalOrStaleTelemetry' }
$report = [ordered]@{ capturedAtUtc=[DateTimeOffset]::UtcNow; trial=[bool]$Trial; before=$before;
    preflightReasons=$reasons; boostExecutionBefore=$state.controls.executionState.fields.'performance.isBoostEnabled';
    frequencyExecutionBefore=[ordered]@{
        ac=$state.controls.executionState.fields.'performance.acMaxFrequencyMhz';
        dc=$state.controls.executionState.fields.'performance.dcMaxFrequencyMhz' };
    performanceMode=$state.controls.performanceMode;
    apply=$null; applied=$null; restore=$null; restored=$null; serviceRestored=$null;
    boostExecutionAfter=$null; preferencesUnchanged=$null; error=$null }
if (-not $Trial) { $report | ConvertTo-Json -Depth 6; exit 0 }
if ($reasons.Count) { throw "Preflight refused without write: $($reasons -join ',')" }

$originalHash = (Get-FileHash -LiteralPath $preferencesPath -Algorithm SHA256).Hash
$attempted = $false
try {
    $attempted = $true
    $report.apply = Set-Boost $false
    if ($report.apply.state -ne 'applied' -or $null -ne $report.apply.error) { throw 'Boost disable not confirmed' }
    $report.applied = Get-Power
    Assert-Unrelated $before $report.applied
    if ($report.applied.AcBoost -ne 0 -or $report.applied.DcBoost -ne 0)
    { throw 'Independent boost disable readback mismatch' }
}
catch { $report.error = $_.Exception.Message }
finally {
    if ($attempted) {
        try {
            $current = Get-Power
            Assert-Unrelated $before $current
            if (($current.AcBoost -notin @(0, $before.AcBoost)) -or
                ($current.DcBoost -notin @(0, $before.DcBoost)))
            { throw 'External boost change detected; refusing to overwrite it' }
            if ($current.AcBoost -eq 0 -or $current.DcBoost -eq 0) {
                $report.restore = Set-Boost $true
                if ($report.restore.state -ne 'applied' -or $null -ne $report.restore.error)
                { throw 'Service boost restore not confirmed' }
            }
            $report.restored = Get-Power
            Assert-Unrelated $before $report.restored
            if ($report.restored.AcBoost -ne $before.AcBoost -or $report.restored.DcBoost -ne $before.DcBoost)
            { throw 'Original boost modes not restored' }
            $afterState = Request 'getDeviceState' @{}
            $report.serviceRestored = $afterState.controls.cpuTuning.AcBoostMode -eq $before.AcBoost -and
                $afterState.controls.cpuTuning.DcBoostMode -eq $before.DcBoost
            $report.boostExecutionAfter = $afterState.controls.executionState.fields.'performance.isBoostEnabled'
            if (-not $report.serviceRestored) { throw 'Service boost readback not restored' }
        }
        catch {
            $report.error = (@($report.error, "Recovery: $($_.Exception.Message)") | Where-Object { $_ }) -join '; '
            try {
                $current = Get-Power
                Assert-Unrelated $before $current
                if (($current.AcBoost -in @(0, $before.AcBoost)) -and
                    ($current.DcBoost -in @(0, $before.DcBoost))) {
                    & powercfg /setacvalueindex $before.Scheme $subgroup $boost $before.AcBoost
                    if ($LASTEXITCODE -ne 0) { throw 'Direct AC boost recovery failed' }
                    & powercfg /setdcvalueindex $before.Scheme $subgroup $boost $before.DcBoost
                    if ($LASTEXITCODE -ne 0) { throw 'Direct DC boost recovery failed' }
                    & powercfg /setactive $before.Scheme
                    if ($LASTEXITCODE -ne 0) { throw 'Direct Windows recovery activation failed' }
                    $report.restored = Get-Power
                }
            }
            catch { $report.error += "; Direct recovery: $($_.Exception.Message)" }
        }
    }
    $report.preferencesUnchanged = (Get-FileHash -LiteralPath $preferencesPath -Algorithm SHA256).Hash -eq $originalHash
}
$report | ConvertTo-Json -Depth 6
if ($report.error -or -not $report.preferencesUnchanged) { exit 1 }
