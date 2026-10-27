param([Parameter(Mandatory)][string]$ExecutablePath)

$ErrorActionPreference = 'Stop'
$executable = (Resolve-Path -LiteralPath $ExecutablePath).Path

function Invoke-ReadOnlyStage([bool]$Confirm) {
    $start = [Diagnostics.ProcessStartInfo]::new($executable, '--stage read-only')
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        [void]$process.Start()
        $errorTask = $process.StandardError.ReadToEndAsync()
        $placeholder = $false
        while ($true) {
            $lineTask = $process.StandardOutput.ReadLineAsync()
            if (-not $lineTask.Wait(5000)) { throw 'Acceptance CLI output timed out.' }
            $line = $lineTask.GetAwaiter().GetResult()
            if ($null -eq $line) { break }
            if ($line -eq 'No WMI/EC call or hardware write is performed by this software build.') {
                $placeholder = $true
            }
            if ($line -match 'Type the one-time confirmation phrase exactly: (JIAOLONG-\d{6})$') {
                if (-not $placeholder) { throw 'Refusing to confirm a CLI that does not identify itself as a no-device-action placeholder.' }
                $entered = if ($Confirm) { $Matches[1] } else { 'incorrect' }
                $process.StandardInput.WriteLine($entered)
                $process.StandardInput.Close()
            }
        }
        if (-not $process.WaitForExit(5000)) { throw 'Acceptance CLI did not exit.' }
        [pscustomobject]@{ ExitCode = $process.ExitCode; Error = $errorTask.GetAwaiter().GetResult() }
    }
    finally {
        if ($process.Id -and -not $process.HasExited) { $process.Kill() }
        $process.Dispose()
    }
}

$confirmed = Invoke-ReadOnlyStage $true
if ($confirmed.ExitCode -ne 4 -or $confirmed.Error -notmatch 'NOT_IMPLEMENTED') {
    throw "Unimplemented acceptance must return 4 and NOT_IMPLEMENTED; actual exit: $($confirmed.ExitCode)."
}
$rejected = Invoke-ReadOnlyStage $false
if ($rejected.ExitCode -ne 3 -or $rejected.Error -notmatch 'Confirmation failed') {
    throw "Incorrect confirmation must remain rejected with exit 3; actual exit: $($rejected.ExitCode)."
}
Write-Output 'PASS: unimplemented acceptance is not success; incorrect confirmation stays rejected. No device actions ran.'
