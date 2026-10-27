param([switch]$Trial)
$ErrorActionPreference = 'Stop'
$subgroup = '54533251-82be-4824-96c1-47b60b740d00'
$frequency = '75b0ae3f-bce0-45a7-8c89-c9611c25e100'
$boost = 'be337238-0d82-4146-a960-4f3749d470c7'
$parking = '0cc5b647-c1df-4637-891a-dec35c318583'
$preferencesPath = 'C:\Users\Administrator\AppData\Local\Jiaolong Control Center\preferences.json'

function Get-PowerIndex([string]$scheme, [string]$setting) {
    $lines = & powercfg /qh $scheme $subgroup $setting
    if ($LASTEXITCODE -ne 0) { throw "powercfg query failed: $setting" }
    $values = @($lines | Where-Object { $_ -match '当前.*设置索引:\s*0x([0-9a-fA-F]+)' } |
        ForEach-Object { [Convert]::ToInt32($Matches[1], 16) })
    if ($values.Count -ne 2) { throw "AC/DC power index unavailable: $setting" }
    return ,$values
}

function Get-WindowsPower {
    $active = (& powercfg /getactivescheme) -join ' '
    if ($LASTEXITCODE -ne 0 -or $active -notmatch '[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}')
    { throw 'Active power scheme unavailable' }
    $scheme = $Matches[0].ToLowerInvariant()
    $max = Get-PowerIndex $scheme $frequency
    $b = Get-PowerIndex $scheme $boost
    $park = Get-PowerIndex $scheme $parking
    return [pscustomobject]@{ Scheme=$scheme; AcMax=$max[0]; DcMax=$max[1]; AcBoost=$b[0]; DcBoost=$b[1]; AcParking=$park[0]; DcParking=$park[1] }
}

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
        Send-Frame ([ordered]@{ kind='hello'; protocolVersion=$version; messageId=[Guid]::NewGuid(); sentAtUtc=[DateTimeOffset]::UtcNow; clientVersion='cpu-frequency-acceptance' })
        if ((Read-Frame).kind -ne 'helloAck') { throw 'Service handshake failed' }
        $operationId = if ($operation -eq 'executeCommand') { $payload.operationId } else { [Guid]::NewGuid() }
        Send-Frame ([ordered]@{ kind='request'; protocolVersion=$version; messageId=[Guid]::NewGuid(); sentAtUtc=[DateTimeOffset]::UtcNow;
            operationId=$operationId; operation=$operation; deadlineUtc=[DateTimeOffset]::UtcNow.AddSeconds(9); payload=$payload })
        $response = Read-Frame
        if ($response.status -ne 'success') { throw "Service request failed: $($response.error.code)" }
        return $response.payload
    }
    finally { $pipe.Dispose() }
}

function Set-Frequency([int]$ac, [int]$dc) {
    $plan = [ordered]@{ temperatureLimitC=$null; splWatts=$null; spptWatts=$null; maxFrequencyMhz=$null;
        boostEnabled=$null; enabledCoreCount=$null; windowsPowerSchemeId=$null; negativeCurveOptimizer=$null;
        acMaxFrequencyMhz=$ac; dcMaxFrequencyMhz=$dc }
    $result = Invoke-Service 'executeCommand' ([ordered]@{ type='setCpuTuning'; operationId=[Guid]::NewGuid(); plan=$plan; riskConfirmed=$true })
    $errorCode = if ($null -eq $result.error) { $null } else { $result.error.code }
    return [pscustomobject]@{ operationId=$result.operationId; state=$result.state;
        error=$errorCode; mutation=$result.mutation }
}

function Check-Unrelated($before, $after) {
    if ($after.Scheme -ne $before.Scheme -or $after.AcBoost -ne $before.AcBoost -or
        $after.DcBoost -ne $before.DcBoost -or $after.AcParking -ne $before.AcParking -or
        $after.DcParking -ne $before.DcParking) { throw 'Unrelated Windows power setting changed' }
}

$report = [ordered]@{ capturedAtUtc=[DateTimeOffset]::UtcNow; trial=[bool]$Trial; before=$null;
    cpuExecutionBefore=$null; apply=$null; appliedWindows=$null; restore=$null; restoredWindows=$null;
    cpuExecutionAfter=$null; cpuExecutionChanged=$null; serviceRestored=$null;
    preferencesUnchanged=$null; error=$null }
$before = Get-WindowsPower
$state = Invoke-Service 'getDeviceState' @{}
$cpu = $state.controls.cpuTuning
$report.before = $before
$report.cpuExecutionBefore = [ordered]@{
    ac = $state.controls.executionState.fields.'performance.acMaxFrequencyMhz'
    dc = $state.controls.executionState.fields.'performance.dcMaxFrequencyMhz'
}
$reasons = @()
if ($null -ne $report.cpuExecutionBefore.ac -or $null -ne $report.cpuExecutionBefore.dc)
{ $reasons += 'cpuExecutionAlreadyOwned' }
if ((Get-CimInstance Win32_BaseBoard).Product -ne 'MRID6-23' -or
    (Get-CimInstance Win32_BIOS).SMBIOSBIOSVersion -ne 'MRID6_23_P_V39') { $reasons += 'deviceIdentity' }
if ($before.Scheme -ne '381b4222-f694-41f0-9685-ff5bb260df2e' -or
    $before.AcMax -ne 4700 -or $before.DcMax -ne 3200) { $reasons += 'windowsBaseline' }
if ($cpu.AcMaxFrequencyMhz -ne $before.AcMax -or $cpu.DcMaxFrequencyMhz -ne $before.DcMax -or
    $cpu.AcBoostMode -ne $before.AcBoost -or $cpu.DcBoostMode -ne $before.DcBoost -or
    $cpu.AcMinActiveCoresPercent -ne $before.AcParking -or $cpu.DcMinActiveCoresPercent -ne $before.DcParking)
{ $reasons += 'serviceReadback' }
if ($state.controls.performanceMode -ne 'balanced' -or $state.controls.adaptiveAutomation.enabled -eq $true)
{ $reasons += 'modeOrAutomation' }
$telemetryAgeSeconds = ([DateTimeOffset]::UtcNow - [DateTimeOffset]$state.telemetry.capturedAtUtc).TotalSeconds
if ($state.telemetry.acPowerConnected -ne $true -or $null -eq $state.telemetry.cpuTemperatureC -or
    $state.telemetry.cpuTemperatureC -ge 75 -or $telemetryAgeSeconds -gt 5)
{ $reasons += "powerThermalOrStaleTelemetry(ac=$($state.telemetry.acPowerConnected),cpuC=$($state.telemetry.cpuTemperatureC),ageS=$([Math]::Round($telemetryAgeSeconds,1)))" }
if (Get-Process -Name 'Jiaolong.ControlCenter' -ErrorAction SilentlyContinue) { $reasons += 'clientRunning' }
if ($reasons.Count -gt 0) { throw "Preflight refused without write: $($reasons -join ',')" }

if (-not $Trial) { $report | ConvertTo-Json -Depth 8; exit 0 }

$originalHash = (Get-FileHash -LiteralPath $preferencesPath -Algorithm SHA256).Hash
$target = $before.AcMax - 100
$attempted = $false
try {
    $attempted = $true
    $report.apply = Set-Frequency $target $before.DcMax
    if ($report.apply.state -ne 'applied' -or $null -ne $report.apply.error) { throw 'CPU frequency apply not confirmed' }
    $report.appliedWindows = Get-WindowsPower
    Check-Unrelated $before $report.appliedWindows
    if ($report.appliedWindows.AcMax -ne $target -or $report.appliedWindows.DcMax -ne $before.DcMax)
    { throw 'Independent frequency readback mismatch' }
}
catch { $report.error = $_.Exception.Message }
finally {
    if ($attempted) {
        try {
            $current = Get-WindowsPower
            if ($current.AcMax -eq $target -and $current.DcMax -eq $before.DcMax) {
                $report.restore = Set-Frequency $before.AcMax $before.DcMax
                if ($report.restore.state -ne 'applied' -or $null -ne $report.restore.error)
                { throw 'Service restore not confirmed' }
            }
            elseif ($current.AcMax -ne $before.AcMax -or $current.DcMax -ne $before.DcMax)
            { throw 'External frequency change detected; refusing to overwrite it' }
            $report.restoredWindows = Get-WindowsPower
            Check-Unrelated $before $report.restoredWindows
            if ($report.restoredWindows.AcMax -ne $before.AcMax -or $report.restoredWindows.DcMax -ne $before.DcMax)
            { throw 'Original frequency not restored' }
            $afterState = Invoke-Service 'getDeviceState' @{}
            $report.cpuExecutionAfter = [ordered]@{
                ac = $afterState.controls.executionState.fields.'performance.acMaxFrequencyMhz'
                dc = $afterState.controls.executionState.fields.'performance.dcMaxFrequencyMhz'
            }
            $report.cpuExecutionChanged = (ConvertTo-Json -InputObject $report.cpuExecutionBefore -Depth 8 -Compress) -ne
                (ConvertTo-Json -InputObject $report.cpuExecutionAfter -Depth 8 -Compress)
            $afterCpu = $afterState.controls.cpuTuning
            $report.serviceRestored = $afterCpu.AcMaxFrequencyMhz -eq $before.AcMax -and
                $afterCpu.DcMaxFrequencyMhz -eq $before.DcMax -and
                $afterState.controls.performanceMode -eq $state.controls.performanceMode -and
                $afterState.controls.adaptiveAutomation.enabled -eq $state.controls.adaptiveAutomation.enabled
            if (-not $report.serviceRestored) { throw 'Service state did not return to original values' }
        }
        catch {
            $report.error = (@($report.error, "Recovery: $($_.Exception.Message)") |
                Where-Object { $_ }) -join '; '
            try {
                $current = Get-WindowsPower
                if ($current.AcMax -eq $target -and $current.DcMax -eq $before.DcMax) {
                    & powercfg /setacvalueindex $before.Scheme $subgroup $frequency $before.AcMax
                    if ($LASTEXITCODE -ne 0) { throw 'Direct Windows recovery write failed' }
                    & powercfg /setactive $before.Scheme
                    if ($LASTEXITCODE -ne 0) { throw 'Direct Windows recovery activation failed' }
                    $report.restoredWindows = Get-WindowsPower
                }
            }
            catch { $report.error += "; Direct recovery: $($_.Exception.Message)" }
        }
    }
    $report.preferencesUnchanged = (Get-FileHash -LiteralPath $preferencesPath -Algorithm SHA256).Hash -eq $originalHash
}
$report | ConvertTo-Json -Depth 8
if ($report.error -or -not $report.preferencesUnchanged) { exit 1 }
