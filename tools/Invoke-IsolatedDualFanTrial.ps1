param(
    [Parameter(Mandatory)][string]$TrialResult,
    [Parameter(Mandatory)][string]$RunResult
)
$ErrorActionPreference = 'Stop'
$taskName = 'JiaoLongPlus'
$configPath = 'F:\JiaoLong7.3\AppSettings.json'
$trialScript = Join-Path $PSScriptRoot 'Read-OemFanEc.ps1'
$tempRoot = 'C:\Users\Administrator\AppData\Local\Temp\'
trap {
    @{ EarlyError = $_.Exception.ToString() } | ConvertTo-Json -Compress |
        Set-Content -LiteralPath $RunResult -Encoding utf8
    exit 1
}
foreach ($path in @($TrialResult, $RunResult)) {
    if (-not [IO.Path]::GetFullPath($path).StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Trial results must stay in C: Temp.'
    }
}
$env:TEMP = $tempRoot.TrimEnd('\')
$env:TMP = $env:TEMP
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Administrator token required.'
}
$task = Get-ScheduledTask -TaskName $taskName
if ([IO.Path]::GetDirectoryName($task.Actions.Execute) -ne 'F:\JiaoLong7.3' -or
    [IO.Path]::GetExtension($task.Actions.Execute) -ne '.exe') {
    throw 'Unexpected third-party task action.'
}
$wasRunning = $task.State -eq 'Running'
$configHash = (Get-FileHash -LiteralPath $configPath -Algorithm SHA256).Hash
$outcome = [ordered]@{ WasRunning = $wasRunning; TrialExitCode = $null; TaskRestored = $false; ConfigUnchanged = $false }
try {
    if (-not $wasRunning) { throw 'Third-party task was not running; refusing to alter its state.' }
    Stop-ScheduledTask -TaskName $taskName
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        Start-Sleep -Milliseconds 200
        $state = (Get-ScheduledTask -TaskName $taskName).State
    } while ($state -eq 'Running' -and [DateTime]::UtcNow -lt $deadline)
    if ($state -eq 'Running') { throw 'Third-party task did not stop.' }
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"' + $trialScript + '"'),
        '-Result', ('"' + $TrialResult + '"'), '-DualTargetTrial', '-OemInitTrial')
    $shell = 'C:\Users\Administrator\.cache\codex-runtimes\codex-primary-runtime\dependencies\native\powershell\pwsh.exe'
    $process = Start-Process -FilePath $shell -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
    $outcome.TrialExitCode = $process.ExitCode
} catch {
    $outcome.Error = $_.Exception.Message
} finally {
    if ($wasRunning) {
        try {
            if ((Get-ScheduledTask -TaskName $taskName).State -ne 'Running') {
                Start-ScheduledTask -TaskName $taskName
            }
            $deadline = [DateTime]::UtcNow.AddSeconds(15)
            do {
                Start-Sleep -Milliseconds 250
                $outcome.TaskRestored = (Get-ScheduledTask -TaskName $taskName).State -eq 'Running'
            } while (-not $outcome.TaskRestored -and [DateTime]::UtcNow -lt $deadline)
        } catch {
            $outcome.RestoreError = $_.Exception.Message
        }
    }
    $outcome.ConfigUnchanged = (Get-FileHash -LiteralPath $configPath -Algorithm SHA256).Hash -eq $configHash
    $outcome.ServiceRunning = (Get-Service JiaolongControlService).Status -eq 'Running'
    $outcome | ConvertTo-Json -Compress | Set-Content -LiteralPath $RunResult -Encoding utf8
}
if (-not $outcome.TaskRestored -or -not $outcome.ConfigUnchanged -or
    -not $outcome.ServiceRunning -or $outcome.TrialExitCode -ne 0) { exit 1 }
