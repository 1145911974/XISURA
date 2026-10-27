param(
    [ValidateRange(2, 12)][int]$Samples = 6,
    [ValidateRange(1, 60)][int]$IntervalSeconds = 5,
    [ValidateRange(1, 100)][int]$MaxSystemCpuPercent = 20
)

$ErrorActionPreference = 'Stop'
$rows = @()
$servicePid = $null
for ($i = 0; $i -lt $Samples; $i++) {
    if (Get-Process -Name '007FirstLight' -ErrorAction SilentlyContinue) {
        throw 'Idle sample refused: game process is running'
    }
    $process = @(Get-CimInstance Win32_PerfFormattedData_PerfProc_Process |
        Where-Object { $_.Name -eq 'Jiaolong.Service' })
    if ($process.Count -ne 1) { throw 'Idle sample refused: service process unavailable or ambiguous' }
    if ($null -ne $servicePid -and $servicePid -ne $process[0].IDProcess) {
        throw 'Idle sample refused: service restarted during sampling'
    }
    $servicePid = $process[0].IDProcess
    $load = @(Get-CimInstance Win32_Processor | ForEach-Object LoadPercentage)
    if ($load.Count -eq 0 -or $load -contains $null) { throw 'System CPU load unavailable' }
    $systemCpu = [Math]::Round(($load | Measure-Object -Average).Average, 1)
    if ($systemCpu -gt $MaxSystemCpuPercent) {
        throw "Idle sample refused: system CPU $systemCpu% exceeds $MaxSystemCpuPercent%"
    }
    $rows += [pscustomobject]@{
        utc = [DateTimeOffset]::UtcNow.ToString('O')
        serviceCpuCorePercent = [int]$process[0].PercentProcessorTime
        workingMiB = [Math]::Round($process[0].WorkingSet / 1MB, 1)
        privateMiB = [Math]::Round($process[0].PrivateBytes / 1MB, 1)
        handles = [int]$process[0].HandleCount
        threads = [int]$process[0].ThreadCount
        systemCpuPercent = $systemCpu
    }
    if ($i -lt $Samples - 1) { Start-Sleep -Seconds $IntervalSeconds }
}

$report = [ordered]@{
    servicePid = $servicePid
    samples = $Samples
    intervalSeconds = $IntervalSeconds
    maxSystemCpuPercent = $MaxSystemCpuPercent
    serviceCpuCorePercentMax = ($rows.serviceCpuCorePercent | Measure-Object -Maximum).Maximum
    workingMiBMin = ($rows.workingMiB | Measure-Object -Minimum).Minimum
    workingMiBMax = ($rows.workingMiB | Measure-Object -Maximum).Maximum
    privateMiBMin = ($rows.privateMiB | Measure-Object -Minimum).Minimum
    privateMiBMax = ($rows.privateMiB | Measure-Object -Maximum).Maximum
    handlesMin = ($rows.handles | Measure-Object -Minimum).Minimum
    handlesMax = ($rows.handles | Measure-Object -Maximum).Maximum
    threadsMin = ($rows.threads | Measure-Object -Minimum).Minimum
    threadsMax = ($rows.threads | Measure-Object -Maximum).Maximum
    readings = $rows
}
$report | ConvertTo-Json -Depth 4
