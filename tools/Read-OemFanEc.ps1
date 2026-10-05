param([Parameter(Mandatory)][string]$Result,[switch]$Trial,[switch]$OemInitTrial,[switch]$DualTargetTrial)
$ErrorActionPreference = 'Stop'
$driverDir = 'F:\JiaoLongControl\Drivers\Blding'
$driver = Join-Path $driverDir 'JiaoLongDriver64.sys'
$library = Join-Path $driverDir 'JiaoLongDriver64.dll'
$serviceName = 'JiaoLongDriver64'
$created = $false
$started = $false
$initialized = $false
$initTouched = $false
$initBefore = [byte]0
function Get-Telemetry {
    $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', 'Jiaolong.ControlCenter.v1',[IO.Pipes.PipeDirection]::InOut)
    try {
        $pipe.Connect(3000)
        function Send-Frame($value) {
            $bytes = [Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject $value -Depth 16 -Compress))
            $size = [BitConverter]::GetBytes([int]$bytes.Length)
            $pipe.Write($size,0,4); $pipe.Write($bytes,0,$bytes.Length); $pipe.Flush()
        }
        function Read-Frame {
            $size = New-Object byte[] 4; $pipe.ReadExactly($size)
            $length = [BitConverter]::ToInt32($size,0)
            if ($length -le 0 -or $length -gt 1048576) { throw 'Invalid service frame length' }
            $bytes = New-Object byte[] $length; $pipe.ReadExactly($bytes)
            [Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json
        }
        $version = [ordered]@{major=1;minor=0}
        Send-Frame ([ordered]@{kind='hello';protocolVersion=$version;messageId=[Guid]::NewGuid();sentAtUtc=[DateTimeOffset]::UtcNow;clientVersion='fan-controlled-trial'})
        if ((Read-Frame).kind -ne 'helloAck') { throw 'Service handshake failed' }
        Send-Frame ([ordered]@{kind='request';protocolVersion=$version;messageId=[Guid]::NewGuid();sentAtUtc=[DateTimeOffset]::UtcNow;operationId=[Guid]::NewGuid();operation='getDeviceState';deadlineUtc=[DateTimeOffset]::UtcNow.AddSeconds(5);payload=@{}})
        $response = Read-Frame
        if ($response.status -ne 'success') { throw 'Service telemetry request failed' }
        $t = $response.payload.telemetry
        if ($null -eq $t -or ([DateTimeOffset]::UtcNow - [DateTimeOffset]$t.capturedAtUtc).TotalSeconds -gt 3) { throw 'Service telemetry stale' }
        foreach ($name in @('cpuTemperatureC','gpuTemperatureC','cpuFanRpm','gpuFanRpm')) {
            if ($null -eq $t.$name -or -not [double]::IsFinite([double]$t.$name)) { throw "Telemetry missing: $name" }
        }
        return $t
    } finally { $pipe.Dispose() }
}
function Invoke-Ec([scriptblock]$action) {
    $mutex = [Threading.Mutex]::new($false,'Global\Access_EC')
    try {
        try { $locked = $mutex.WaitOne(200) } catch [Threading.AbandonedMutexException] { $locked = $true }
        if (-not $locked) { throw 'EC mutex unavailable' }
        try { & $action } finally { $mutex.ReleaseMutex() }
    } finally { $mutex.Dispose() }
}
try {
    if ($Trial -and $DualTargetTrial) { throw 'Choose one guarded fan trial' }
    if ($DualTargetTrial -and -not $OemInitTrial) { throw 'Dual-target trial requires the OEM initialization sequence' }
    if ($OemInitTrial -and -not ($Trial -or $DualTargetTrial)) { throw 'OEM initialization is only allowed during a guarded fan trial' }
    $principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Administrator token required' }
    if (-not ([IO.Path]::GetFullPath($Result)).StartsWith('C:\Users\Administrator\AppData\Local\Temp\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Result path must be in C: Temp' }
    foreach ($path in @($driver,$library)) {
        if ((Get-AuthenticodeSignature -LiteralPath $path).Status -ne 'Valid') { throw "Invalid OEM signature: $path" }
    }
    $board = (Get-CimInstance Win32_BaseBoard).Product
    $bios = (Get-CimInstance Win32_BIOS).SMBIOSBIOSVersion
    if ($board -ne 'MRID6-23' -or $bios -ne 'MRID6_23_P_V39') { throw "Unsupported board/BIOS: $board/$bios" }
    if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) { throw 'OEM driver service already exists; refusing to change it' }
    & sc.exe create $serviceName 'type=' kernel 'start=' demand 'binPath=' $driver | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Driver service create failed: $LASTEXITCODE" }
    $created = $true
    & sc.exe start $serviceName | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Driver start failed: $LASTEXITCODE" }
    $started = $true
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class OemEc
{
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] public static extern bool SetDllDirectory(string path);
    [DllImport("JiaoLongDriver64.dll", CallingConvention=CallingConvention.StdCall)] public static extern bool InitializeBldring();
    [DllImport("JiaoLongDriver64.dll", CallingConvention=CallingConvention.StdCall)] public static extern void ShutdownBldring();
    [DllImport("JiaoLongDriver64.dll", CallingConvention=CallingConvention.StdCall)] public static extern bool GetBLDPortVal(ushort port, ref byte value, byte size);
    [DllImport("JiaoLongDriver64.dll", CallingConvention=CallingConvention.StdCall)] public static extern bool SetBLDPortVal(ushort port, byte value, byte size);
    private static void Write(ushort port, byte value)
    {
        if (!SetBLDPortVal(port, value, 1)) throw new InvalidOperationException($"OEM port write failed: {port:X4}");
    }
    public static byte Read(ushort address)
    {
        Write(0x4E, 0x2E); Write(0x4F, 0x11); Write(0x4E, 0x2F); Write(0x4F, (byte)(address >> 8));
        Write(0x4E, 0x2E); Write(0x4F, 0x10); Write(0x4E, 0x2F); Write(0x4F, (byte)address);
        Write(0x4E, 0x2E); Write(0x4F, 0x12); Write(0x4E, 0x2F);
        byte value = 0;
        if (!GetBLDPortVal(0x4F, ref value, 1)) throw new InvalidOperationException($"OEM port read failed: {address:X4}");
        return value;
    }
    public static void WriteRegister(ushort address, byte data)
    {
        Write(0x4E, 0x2E); Write(0x4F, 0x11); Write(0x4E, 0x2F); Write(0x4F, (byte)(address >> 8));
        Write(0x4E, 0x2E); Write(0x4F, 0x10); Write(0x4E, 0x2F); Write(0x4F, (byte)address);
        Write(0x4E, 0x2E); Write(0x4F, 0x12); Write(0x4E, 0x2F); Write(0x4F, data);
    }
}
'@
    if (-not [OemEc]::SetDllDirectory($driverDir)) { throw 'SetDllDirectory failed' }
    if (-not [OemEc]::InitializeBldring()) { throw 'InitializeBldring failed' }
    $initialized = $true
    $baseline = Invoke-Ec { @{controlSamples=@([OemEc]::Read(0x0B20),[OemEc]::Read(0x0B20),[OemEc]::Read(0x0B20));cpuTarget=[OemEc]::Read(0xC83C);gpuTarget=[OemEc]::Read(0xC83D);ecSignature=[OemEc]::Read(0x2000);ecInitWord=[OemEc]::Read(0x1060)} }
    $report = @{ board=$board; bios=$bios; controlSamples=$baseline.controlSamples; cpuTarget=$baseline.cpuTarget; gpuTarget=$baseline.gpuTarget; ecSignature=$baseline.ecSignature; ecInitWord=$baseline.ecInitWord; capturedAtUtc=[DateTimeOffset]::UtcNow; trials=@() }
    $report.readSequence = Invoke-Ec { @(
        [OemEc]::Read(0x2000),[OemEc]::Read(0x0B20),[OemEc]::Read(0x0B20),
        [OemEc]::Read(0x2000),[OemEc]::Read(0xC83C),[OemEc]::Read(0x0B20)
    ) }
    $report | ConvertTo-Json -Depth 8 -Compress | Set-Content -LiteralPath $Result -Encoding utf8
    if ($DualTargetTrial) {
        $dualPreflight = Get-Telemetry
        $report.dualPreflight = @{ ac=$dualPreflight.acPowerConnected;cpuTemp=$dualPreflight.cpuTemperatureC;
            gpuTemp=$dualPreflight.gpuTemperatureC;cpuRpm=$dualPreflight.cpuFanRpm;gpuRpm=$dualPreflight.gpuFanRpm }
        $report | ConvertTo-Json -Depth 8 -Compress | Set-Content -LiteralPath $Result -Encoding utf8
        if ($dualPreflight.acPowerConnected -ne $true -or $dualPreflight.cpuTemperatureC -ge 85 -or
            $dualPreflight.gpuTemperatureC -ge 80 -or $dualPreflight.cpuFanRpm -lt 500 -or
            $dualPreflight.gpuFanRpm -lt 500 -or $dualPreflight.cpuFanRpm -ge 3000 -or
            $dualPreflight.gpuFanRpm -ge 3000) { throw 'AC, thermal, or RPM preflight refused dual trial' }
    }
    if ($OemInitTrial) {
        $preflight = Get-Telemetry
        if ($preflight.acPowerConnected -ne $true -or $preflight.cpuTemperatureC -ge 85 -or $preflight.gpuTemperatureC -ge 80) {
            throw 'OEM initialization refused: AC or thermal preflight failed'
        }
        if ($baseline.ecSignature -ne 85 -or @($baseline.controlSamples | Select-Object -Unique).Count -ne 1 -or
            ($baseline.controlSamples[0] -band 0x0A) -ne 0 -or $baseline.cpuTarget -ne 0 -or $baseline.gpuTarget -ne 0) {
            throw 'OEM initialization refused: unstable or owned EC baseline'
        }
        $initBefore = [byte]$baseline.ecInitWord
        if (($initBefore -band 0x80) -eq 0) {
            $initTouched = $true
            Invoke-Ec {
                [OemEc]::WriteRegister(0x1060,[byte]($initBefore -bor 0x80))
                $report.initReadBack = [OemEc]::Read(0x1060)
            }
            if (($report.initReadBack -band 0x80) -eq 0 -and -not $DualTargetTrial) { throw 'OEM EC initialization bit did not persist' }
        }
        $baseline = Invoke-Ec { @{controlSamples=@([OemEc]::Read(0x0B20),[OemEc]::Read(0x0B20),[OemEc]::Read(0x0B20));cpuTarget=[OemEc]::Read(0xC83C);gpuTarget=[OemEc]::Read(0xC83D);ecSignature=[OemEc]::Read(0x2000);ecInitWord=[OemEc]::Read(0x1060)} }
        $report.afterOemInit = $baseline
        $report | ConvertTo-Json -Depth 8 -Compress | Set-Content -LiteralPath $Result -Encoding utf8
    }
    if ($Trial) {
        if (@($baseline.controlSamples | Select-Object -Unique).Count -ne 1 -or ($baseline.controlSamples[0] -band 0x0A) -ne 0 -or $baseline.cpuTarget -ne 0 -or $baseline.gpuTarget -ne 0) { throw 'Firmware baseline has fan ownership or unstable targets; trial refused' }
        foreach ($fan in @(@{name='cpu';address=[ushort]0xC83C;bit=[byte]2;rpm='cpuFanRpm'},@{name='gpu';address=[ushort]0xC83D;bit=[byte]8;rpm='gpuFanRpm'})) {
            $before = Get-Telemetry
            if ($before.acPowerConnected -ne $true) { throw 'AC power required for fan trial' }
            if ($before.cpuTemperatureC -ge 85 -or $before.gpuTemperatureC -ge 80) { throw 'Thermal guard refused trial' }
            if ($before.($fan.rpm) -le 500 -or $before.($fan.rpm) -ge 6500) { throw 'RPM baseline outside guarded range' }
            $target = [Math]::Min(68,[Math]::Ceiling($before.($fan.rpm) / 100) + 5)
            $samples = @()
            $script:fanTouched = $false
            try {
                Invoke-Ec {
                    $controlNow = [OemEc]::Read(0x0B20)
                    if (($controlNow -band 0x0A) -ne 0 -or [OemEc]::Read($fan.address) -ne 0) { throw 'EC ownership changed before trial' }
                    $script:fanTouched = $true
                    [OemEc]::WriteRegister($fan.address,[byte]$target)
                    [OemEc]::WriteRegister(0x0B20,[byte]($controlNow -bor $fan.bit))
                    $actualTarget = [OemEc]::Read($fan.address)
                    $actualControl = [OemEc]::Read(0x0B20)
                    $expectedControl = [byte]($controlNow -bor $fan.bit)
                    if ($actualTarget -ne $target -or $actualControl -ne $expectedControl) { throw "EC readback mismatch: target=$actualTarget expected=$target control=$actualControl expectedControl=$expectedControl" }
                }
                for ($second=0; $second -lt 10; $second++) {
                    Start-Sleep -Seconds 1
                    $sample = Get-Telemetry
                    $samples += @{second=$second+1;cpuTemp=$sample.cpuTemperatureC;gpuTemp=$sample.gpuTemperatureC;cpuRpm=$sample.cpuFanRpm;gpuRpm=$sample.gpuFanRpm}
                    if ($sample.acPowerConnected -ne $true) { throw 'AC power lost during fan trial' }
                    if ($sample.cpuTemperatureC -ge 90 -or $sample.gpuTemperatureC -ge 84) { throw 'Thermal guard stopped trial' }
                }
            } finally {
                $restored = -not $script:fanTouched
                if ($script:fanTouched) {
                    for ($attempt=0; $attempt -lt 3 -and -not $restored; $attempt++) {
                        try {
                            Invoke-Ec {
                                [OemEc]::WriteRegister($fan.address,0)
                                $current = [OemEc]::Read(0x0B20)
                                [OemEc]::WriteRegister(0x0B20,[byte]($current -band (-bnot $fan.bit)))
                                if ([OemEc]::Read($fan.address) -ne 0 -or ([OemEc]::Read(0x0B20) -band $fan.bit) -ne 0) { throw 'EC restore readback mismatch' }
                            }
                            $restored = $true
                        } catch { Start-Sleep -Milliseconds 150 }
                    }
                }
                if (-not $restored) { throw "CRITICAL: $($fan.name) EC restore failed" }
            }
            $after = Get-Telemetry
            $report.trials += @{fan=$fan.name;targetByte=$target;beforeRpm=$before.($fan.rpm);afterRpm=$after.($fan.rpm);samples=$samples;restored=$restored}
            $report | ConvertTo-Json -Depth 8 -Compress | Set-Content -LiteralPath $Result -Encoding utf8
        }
    }
    if ($DualTargetTrial) {
        if (@($baseline.controlSamples | Select-Object -Unique).Count -ne 1 -or
            ($baseline.controlSamples[0] -band 0x0A) -ne 0 -or $baseline.cpuTarget -ne 0 -or $baseline.gpuTarget -ne 0) {
            throw 'Firmware baseline has fan ownership or unstable targets; dual trial refused'
        }
        $before = Get-Telemetry
        if ($before.acPowerConnected -ne $true -or $before.cpuTemperatureC -ge 85 -or $before.gpuTemperatureC -ge 80 -or
            $before.cpuFanRpm -lt 500 -or $before.gpuFanRpm -lt 500 -or
            $before.cpuFanRpm -ge 3000 -or $before.gpuFanRpm -ge 3000) {
            throw 'AC, thermal, or RPM preflight refused dual trial'
        }
        $target = [byte]35
        $samples = @()
        $script:dualTouched = $false
        $controlBefore = [byte]$baseline.controlSamples[0]
        try {
            Invoke-Ec {
                $controlNow = [OemEc]::Read(0x0B20)
                if ($controlNow -ne $controlBefore -or [OemEc]::Read(0xC83C) -ne 0 -or [OemEc]::Read(0xC83D) -ne 0) {
                    throw 'EC ownership changed before dual trial'
                }
                $script:dualTouched = $true
                [OemEc]::WriteRegister(0xC83C,$target)
                [OemEc]::WriteRegister(0xC83D,$target)
                $expectedControl = [byte]($controlBefore -bor 2)
                [OemEc]::WriteRegister(0x0B20,$expectedControl)
                $cpuReadBack = [OemEc]::Read(0xC83C)
                $gpuReadBack = [OemEc]::Read(0xC83D)
                $controlReadBack = @([OemEc]::Read(0x0B20),[OemEc]::Read(0x0B20),[OemEc]::Read(0x0B20))
                $report.dualReadBack = @{ cpuTarget=$cpuReadBack;gpuTarget=$gpuReadBack;control=$controlReadBack;expectedControl=$expectedControl }
                if ($cpuReadBack -ne $target -or $gpuReadBack -ne $target -or
                    @($controlReadBack | Where-Object { $_ -ne $expectedControl }).Count -ne 0) {
                    throw 'Dual EC readback mismatch'
                }
            }
            for ($second=0; $second -lt 10; $second++) {
                Start-Sleep -Seconds 1
                $sample = Get-Telemetry
                $samples += @{second=$second+1;cpuTemp=$sample.cpuTemperatureC;gpuTemp=$sample.gpuTemperatureC;cpuRpm=$sample.cpuFanRpm;gpuRpm=$sample.gpuFanRpm}
                if ($sample.acPowerConnected -ne $true -or $sample.cpuTemperatureC -ge 90 -or $sample.gpuTemperatureC -ge 84) {
                    throw 'AC or thermal guard stopped dual trial'
                }
            }
        } finally {
            $restored = -not $script:dualTouched
            if ($script:dualTouched) {
                for ($attempt=0; $attempt -lt 3 -and -not $restored; $attempt++) {
                    try {
                        Invoke-Ec {
                            [OemEc]::WriteRegister(0xC83C,0)
                            [OemEc]::WriteRegister(0xC83D,0)
                            [OemEc]::WriteRegister(0x0B20,$controlBefore)
                            if ([OemEc]::Read(0xC83C) -ne 0 -or [OemEc]::Read(0xC83D) -ne 0 -or
                                [OemEc]::Read(0x0B20) -ne $controlBefore) { throw 'Dual EC restore readback mismatch' }
                        }
                        $restored = $true
                    } catch { Start-Sleep -Milliseconds 150 }
                }
            }
            $report.dualTrial = @{targetByte=$target;beforeCpuRpm=$before.cpuFanRpm;beforeGpuRpm=$before.gpuFanRpm;samples=$samples;restored=$restored}
            $report | ConvertTo-Json -Depth 8 -Compress | Set-Content -LiteralPath $Result -Encoding utf8
            if (-not $restored) { throw 'CRITICAL: dual EC restore failed' }
        }
    }
}
catch {
    @{ error=$_.Exception.ToString(); report=$report } | ConvertTo-Json -Depth 8 -Compress | Set-Content -LiteralPath $Result -Encoding utf8
    exit 1
}
finally {
    try {
        if ($initTouched) {
            Invoke-Ec {
                [OemEc]::WriteRegister(0x1060,$initBefore)
                if ([OemEc]::Read(0x1060) -ne $initBefore) { throw 'OEM initialization restore mismatch' }
            }
        }
    } finally {
        if ($initialized) { [OemEc]::ShutdownBldring() }
        if ($started) { & sc.exe stop $serviceName | Out-Null }
        if ($created) { & sc.exe delete $serviceName | Out-Null }
    }
}
