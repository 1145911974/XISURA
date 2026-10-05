$ErrorActionPreference = 'Continue'
$taskName = 'JiaolongCapabilityProbeOnce'
$resultPath = 'C:\Users\Administrator\AppData\Local\Temp\jiaolong-post-reboot-smu.json'
$logPath = 'C:\Users\Administrator\AppData\Local\Temp\jiaolong-post-reboot-probe.log'
$hostExe = 'C:\Users\Administrator\AppData\Local\Temp\JiaolongProbeHost-run\JiaolongProbeHost.exe'

try {
    "[$([DateTimeOffset]::Now)] probe-start" | Set-Content -LiteralPath $logPath -Encoding utf8
    & $hostExe $resultPath smu-final-read *>> $logPath
    "[$([DateTimeOffset]::Now)] probe-exit=$LASTEXITCODE" | Add-Content -LiteralPath $logPath -Encoding utf8
}
catch {
    "[$([DateTimeOffset]::Now)] probe-error=$($_.Exception.Message)" | Add-Content -LiteralPath $logPath -Encoding utf8
}
finally { }
