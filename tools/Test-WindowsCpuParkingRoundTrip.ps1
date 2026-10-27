param([switch]$Trial)
$ErrorActionPreference = 'Stop'
$subgroup = '54533251-82be-4824-96c1-47b60b740d00'
$parking = '0cc5b647-c1df-4637-891a-dec35c318583'
$frequency = '75b0ae3f-bce0-45a7-8c89-c9611c25e100'
$boost = 'be337238-0d82-4146-a960-4f3749d470c7'
$preferencesPath = 'C:\Users\Administrator\AppData\Local\Jiaolong Control Center\preferences.json'

function Get-PowerIndex([string]$scheme, [string]$setting) {
    $lines = & powercfg /qh $scheme $subgroup $setting
    if ($LASTEXITCODE -ne 0) { throw "Power setting query failed: $setting" }
    $values = @($lines | Where-Object { $_ -match '当前.*设置索引:\s*0x([0-9a-fA-F]+)' } |
        ForEach-Object { [Convert]::ToInt32($Matches[1], 16) })
    if ($values.Count -ne 2) { throw "AC/DC power setting unavailable: $setting" }
    return ,$values
}

function Get-Power {
    $active = (& powercfg /getactivescheme) -join ' '
    if ($LASTEXITCODE -ne 0 -or $active -notmatch '[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}')
    { throw 'Active power scheme unavailable' }
    $scheme = $Matches[0].ToLowerInvariant()
    $park = Get-PowerIndex $scheme $parking
    $freq = Get-PowerIndex $scheme $frequency
    $boostMode = Get-PowerIndex $scheme $boost
    return [pscustomobject]@{ Scheme=$scheme; Ac=$park[0]; Dc=$park[1];
        AcMax=$freq[0]; DcMax=$freq[1]; AcBoost=$boostMode[0]; DcBoost=$boostMode[1] }
}

function Assert-Unrelated($before, $after) {
    if ($before.Scheme -ne $after.Scheme -or $before.Dc -ne $after.Dc -or
        $before.AcMax -ne $after.AcMax -or $before.DcMax -ne $after.DcMax -or
        $before.AcBoost -ne $after.AcBoost -or $before.DcBoost -ne $after.DcBoost)
    { throw 'Unrelated Windows power setting changed' }
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
            sentAtUtc=[DateTimeOffset]::UtcNow; clientVersion='cpu-parking-acceptance' })
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

function Set-Parking([int]$ac, [int]$dc) {
    $plan = [ordered]@{ acMinActiveCoresPercent=$ac; dcMinActiveCoresPercent=$dc }
    $result = Request 'executeCommand' ([ordered]@{ type='setCpuTuning'; operationId=[Guid]::NewGuid();
        plan=$plan; riskConfirmed=$true })
    return [pscustomobject]@{ state=$result.state; error=$result.error; mutation=$result.mutation }
}

$before = Get-Power
$state = Request 'getDeviceState' @{}
$cpu = $state.controls.cpuTuning
$reasons = @()
if ((Get-CimInstance Win32_BaseBoard).Product -ne 'MRID6-23' -or
    (Get-CimInstance Win32_BIOS).SMBIOSBIOSVersion -ne 'MRID6_23_P_V39') { $reasons += 'deviceIdentity' }
if ($before.Scheme -ne '381b4222-f694-41f0-9685-ff5bb260df2e' -or
    $before.Ac -ne 100 -or $before.Dc -ne 17) { $reasons += 'windowsBaseline' }
if ($cpu.AcMinActiveCoresPercent -ne $before.Ac -or $cpu.DcMinActiveCoresPercent -ne $before.Dc)
{ $reasons += 'serviceReadback' }
if ($state.controls.adaptiveAutomation.enabled -eq $true) { $reasons += 'automationEnabled' }
if ($state.controls.executionState.fields.'performance.acMinActiveCoresPercent' -or
    $state.controls.executionState.fields.'performance.dcMinActiveCoresPercent') { $reasons += 'parkingAlreadyOwned' }
if (Get-Process -Name 'Jiaolong.ControlCenter' -ErrorAction SilentlyContinue) { $reasons += 'clientRunning' }
$age = ([DateTimeOffset]::UtcNow - [DateTimeOffset]$state.telemetry.capturedAtUtc).TotalSeconds
if ($state.telemetry.acPowerConnected -ne $true -or $null -eq $state.telemetry.cpuTemperatureC -or
    $state.telemetry.cpuTemperatureC -ge 75 -or $age -gt 5)
{ $reasons += 'powerThermalOrStaleTelemetry' }
$report = [ordered]@{ capturedAtUtc=[DateTimeOffset]::UtcNow; trial=[bool]$Trial; before=$before;
    preflightReasons=$reasons; parkingExecutionBefore=[ordered]@{
        ac=$state.controls.executionState.fields.'performance.acMinActiveCoresPercent';
        dc=$state.controls.executionState.fields.'performance.dcMinActiveCoresPercent' };
    apply=$null; applied=$null; restore=$null; restored=$null; serviceRestored=$null;
    preferencesUnchanged=$null; error=$null }
if (-not $Trial) { $report | ConvertTo-Json -Depth 5; exit 0 }
if ($reasons.Count) { throw "Preflight refused without write: $($reasons -join ',')" }

$originalHash = (Get-FileHash -LiteralPath $preferencesPath -Algorithm SHA256).Hash
$target = 99
$attempted = $false
try {
    $attempted = $true
    $report.apply = Set-Parking $target $before.Dc
    if ($report.apply.state -ne 'applied' -or $null -ne $report.apply.error) { throw 'Parking apply not confirmed' }
    $report.applied = Get-Power
    Assert-Unrelated $before $report.applied
    if ($report.applied.Ac -ne $target) { throw 'Independent parking readback mismatch' }
}
catch { $report.error = $_.Exception.Message }
finally {
    if ($attempted) {
        try {
            $current = Get-Power
            if ($current.Scheme -ne $before.Scheme -or $current.Dc -ne $before.Dc)
            { throw 'External power scheme/DC change detected; refusing to overwrite it' }
            if ($current.Ac -eq $target) {
                $report.restore = Set-Parking $before.Ac $before.Dc
                if ($report.restore.state -ne 'applied' -or $null -ne $report.restore.error)
                { throw 'Service restore not confirmed' }
            }
            elseif ($current.Ac -ne $before.Ac) { throw 'External AC parking change detected; refusing to overwrite it' }
            $report.restored = Get-Power
            Assert-Unrelated $before $report.restored
            if ($report.restored.Ac -ne $before.Ac) { throw 'Original parking not restored' }
            $after = (Request 'getDeviceState' @{}).controls.cpuTuning
            $report.serviceRestored = $after.AcMinActiveCoresPercent -eq $before.Ac -and
                $after.DcMinActiveCoresPercent -eq $before.Dc
            if (-not $report.serviceRestored) { throw 'Service parking readback not restored' }
        }
        catch {
            $report.error = (@($report.error, "Recovery: $($_.Exception.Message)") | Where-Object { $_ }) -join '; '
            try {
                $current = Get-Power
                if ($current.Scheme -eq $before.Scheme -and $current.Ac -eq $target -and $current.Dc -eq $before.Dc) {
                    & powercfg /setacvalueindex $before.Scheme $subgroup $parking $before.Ac
                    if ($LASTEXITCODE -ne 0) { throw 'Direct Windows recovery write failed' }
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
$report | ConvertTo-Json -Depth 5
if ($report.error -or -not $report.preferencesUnchanged) { exit 1 }
