param(
    [string]$OutputPath = 'C:\Users\Administrator\AppData\Local\Temp\jiaolong-elevated-safe-probe.json'
)

$ErrorActionPreference = 'Stop'
$results = [ordered]@{
    Timestamp = (Get-Date).ToString('o')
    Elevated = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    Tests = @()
}

function Add-Result([string]$Name, [string]$Status, $Before, $After, [string]$Detail) {
    $results.Tests += [ordered]@{ Name = $Name; Status = $Status; Before = $Before; After = $After; Detail = $Detail }
}

function Run-Capture([string]$File, [string[]]$Arguments) {
    $psi = [Diagnostics.ProcessStartInfo]::new($File)
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    foreach ($argument in $Arguments) { [void]$psi.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($psi)
    $stdout = $process.StandardOutput.ReadToEnd().Trim()
    $stderr = $process.StandardError.ReadToEnd().Trim()
    $process.WaitForExit()
    [pscustomobject]@{ ExitCode = $process.ExitCode; Stdout = $stdout; Stderr = $stderr }
}

if (-not $results.Elevated) { throw 'Probe must run elevated.' }

try {
    $before = Run-Capture 'nvidia-smi' @('-q', '-d', 'POWER')
    if ($before.ExitCode -ne 0) { throw $before.Stderr }
    $powerMatch = [regex]::Match($before.Stdout, 'GPU Ceiling Power Limit\s+Current Power Limit\s*:\s*([0-9.]+) W')
    if (-not $powerMatch.Success) { throw 'Unable to read the current GPU ceiling power limit.' }
    $currentPower = [double]::Parse($powerMatch.Groups[1].Value, [Globalization.CultureInfo]::InvariantCulture)
    $write = Run-Capture 'nvidia-smi' @('-i', '0', '-pl', "$currentPower")
    $after = Run-Capture 'nvidia-smi' @('-q', '-d', 'POWER')
    Add-Result 'GPU power limit same-value write' $(if (($write.Stdout + $write.Stderr) -match 'not supported') { 'UNSUPPORTED' } elseif ($write.ExitCode -eq 0 -and $after.ExitCode -eq 0) { 'PASS' } else { 'FAIL' }) $currentPower $currentPower (($write.Stdout + ' ' + $write.Stderr).Trim())
} catch {
    Add-Result 'GPU power limit same-value write' 'ERROR' $null $null $_.Exception.Message
}

try {
    # 87 C is the driver's current reported target on this machine.
    $target = 87
    $tempWrite = Run-Capture 'nvidia-smi' @('-i', '0', '-gtt', "$target")
    Add-Result 'GPU temperature target same-value write' $(if (($tempWrite.Stdout + $tempWrite.Stderr) -match 'not supported') { 'UNSUPPORTED' } elseif ($tempWrite.ExitCode -eq 0) { 'PASS' } else { 'FAIL' }) $target $target (($tempWrite.Stdout + ' ' + $tempWrite.Stderr).Trim())
} catch {
    Add-Result 'GPU temperature target same-value write' 'ERROR' $null $null $_.Exception.Message
}

try {
    Set-Location -LiteralPath 'F:\JiaoLongControl'
    $assembly = [Reflection.Assembly]::LoadFrom('F:\JiaoLongControl\JiaoLongControl.dll')
    $controllerType = $assembly.GetType('JiaoLongControl.Server.Core.Controllers.NvidiaGpuController', $true)
    $controller = [Activator]::CreateInstance($controllerType)
    $caps = $controllerType.GetMethod('GetGpuCurveCapabilities').Invoke($controller, @(-1))
    $before = $controllerType.GetMethod('GetGpuCurveStatus').Invoke($controller, @(-1))
    if (-not $caps.Success -or -not $caps.Data.Supported -or -not $before.Success) { throw "Curve unavailable: $($caps.Message) $($caps.Data.Reason)" }
    $write = $controllerType.GetMethod('SetGpuOffsets').Invoke($controller, @([int]$before.Data.CoreOffsetMhz, [int]$before.Data.MemoryOffsetMhz, -1))
    $after = $controllerType.GetMethod('GetGpuCurveStatus').Invoke($controller, @(-1))
    $same = $after.Success -and $after.Data.CoreOffsetMhz -eq $before.Data.CoreOffsetMhz -and $after.Data.MemoryOffsetMhz -eq $before.Data.MemoryOffsetMhz
    Add-Result 'GPU V/F and memory offset same-value write' $(if ($write.Success -and $same) { 'PASS' } else { 'FAIL' }) $before.Data $after.Data $write.Message

    $interop = $assembly.GetType('JiaoLongControl.Server.Core.Native.NvGpuCurveInterop', $true)
    $flags = [Reflection.BindingFlags]'Public,NonPublic,Static'
    $handles = $interop.GetMethod('EnumeratePhysicalGpus', $flags).Invoke($null, @())
    $gpu = [IntPtr]$handles[0]
    $getVoltageBoost = $interop.GetMethod('GetCoreVoltageBoostPercent', $flags)
    $setVoltageBoost = $interop.GetMethod('SetCoreVoltageBoostPercent', $flags)
    $voltageBefore = [uint32]$getVoltageBoost.Invoke($null, @($gpu))
    $setVoltageBoost.Invoke($null, @($gpu, $voltageBefore)) | Out-Null
    $voltageAfter = [uint32]$getVoltageBoost.Invoke($null, @($gpu))
    Add-Result 'GPU core-voltage boost same-value write' $(if ($voltageAfter -eq $voltageBefore) { 'PASS' } else { 'FAIL' }) $voltageBefore $voltageAfter 'Private NVAPI get/set with unchanged value.'
    if ($controller -is [IDisposable]) { $controller.Dispose() }
} catch {
    $message = $_.Exception.Message
    if ($_.Exception.InnerException) { $message += ' | ' + $_.Exception.InnerException.Message }
    Add-Result 'NVIDIA private API probes' $(if ($message -match 'not supported|NO_PERMISSION|privilege|权限') { 'UNSUPPORTED_OR_BLOCKED' } else { 'ERROR' }) $null $null $message
}

try {
    $power = Run-Capture 'powercfg' @('/qh', 'SCHEME_CURRENT', 'SUB_PROCESSOR', 'CPMINCORES')
    $hexValues = [regex]::Matches($power.Stdout, '0x([0-9a-fA-F]{8})')
    if ($hexValues.Count -lt 5) { throw 'Unable to parse CPMINCORES AC/DC values.' }
    # /qh emits min, max, increment, then the current AC and DC indexes.
    $acValue = [Convert]::ToInt32($hexValues[$hexValues.Count - 2].Groups[1].Value, 16)
    $dcValue = [Convert]::ToInt32($hexValues[$hexValues.Count - 1].Groups[1].Value, 16)
    $acWrite = Run-Capture 'powercfg' @('/setacvalueindex', 'SCHEME_CURRENT', 'SUB_PROCESSOR', 'CPMINCORES', "$acValue")
    $dcWrite = Run-Capture 'powercfg' @('/setdcvalueindex', 'SCHEME_CURRENT', 'SUB_PROCESSOR', 'CPMINCORES', "$dcValue")
    $after = Run-Capture 'powercfg' @('/qh', 'SCHEME_CURRENT', 'SUB_PROCESSOR', 'CPMINCORES')
    Add-Result 'Windows core-parking same-value write' $(if ($acWrite.ExitCode -eq 0 -and $dcWrite.ExitCode -eq 0 -and $after.ExitCode -eq 0) { 'PASS' } else { 'FAIL' }) "AC=$acValue DC=$dcValue" "AC=$acValue DC=$dcValue" (($acWrite.Stderr + ' ' + $dcWrite.Stderr).Trim())
} catch {
    Add-Result 'Windows core-parking same-value write' 'ERROR' $null $null $_.Exception.Message
}

$results | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $OutputPath -Encoding utf8
