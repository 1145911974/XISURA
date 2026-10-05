param(
    [Parameter(Mandatory)][string]$Source,
    [Parameter(Mandatory)][string]$Backup,
    [Parameter(Mandatory)][string]$Result,
    [switch]$ResumeOnly
)
$ErrorActionPreference = 'Stop'
$destination = 'C:\Program Files\Jiaolong Control Center'
$serviceName = 'JiaolongControlService'
$changed = [Collections.Generic.List[object]]::new()
$stopped = $false
try {
    $principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Service update requires elevation.' }
    if ($ResumeOnly) {
        Start-Service -Name $serviceName
        (Get-Service $serviceName).WaitForStatus('Running',[TimeSpan]::FromSeconds(20))
        $resultObject = @{ Success=$true; Service=$serviceName; Action='Resumed without file changes' }
        return
    }
    $Source = (Resolve-Path -LiteralPath $Source).Path
    $Backup = [IO.Path]::GetFullPath($Backup)
    $Result = [IO.Path]::GetFullPath($Result)
    foreach ($path in @($Source,$Backup,$Result)) {
        if (-not $path.StartsWith('C:\Users\Administrator\AppData\Local\Temp\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Only task temporary paths on C: are allowed.' }
    }
    if (Test-Path -LiteralPath $Backup) { throw 'Refusing to overwrite a service backup.' }
    foreach ($requiredFile in @('Jiaolong.Service.exe','Jiaolong.Service.dll','Jiaolong.Service.deps.json','Jiaolong.Service.runtimeconfig.json')) {
        if (-not (Test-Path -LiteralPath (Join-Path $Source $requiredFile) -PathType Leaf)) { throw "Published service missing $requiredFile." }
    }
    $runtimeConfig = Get-Content -LiteralPath (Join-Path $Source 'Jiaolong.Service.runtimeconfig.json') -Raw | ConvertFrom-Json
    $includesCoreRuntime = @($runtimeConfig.runtimeOptions.includedFrameworks | Where-Object { $_.name -eq 'Microsoft.NETCore.App' }).Count -gt 0
    foreach ($runtimeFile in @('hostfxr.dll','hostpolicy.dll','coreclr.dll','System.Private.CoreLib.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $Source $runtimeFile) -PathType Leaf)) { throw "Self-contained service runtime missing $runtimeFile." }
    }
    if (-not $includesCoreRuntime) { throw 'Service package must be self-contained; publish with --runtime win-x64 --self-contained true.' }
    $service = Get-CimInstance Win32_Service -Filter "Name='$serviceName'"
    if ($service.PathName.Trim('"') -ne (Join-Path $destination 'Jiaolong.Service.exe')) { throw 'Installed service target differs from the approved destination.' }
    $serviceProcessId = [int]$service.ProcessId
    $files = Get-ChildItem -LiteralPath $Source -File -Recurse | Where-Object {
        $_.Name -notlike 'appsettings*.json' -and $_.Extension -ne '.pdb'
    }
    $plan = foreach ($file in $files) {
        $relative = $file.FullName.Substring($Source.TrimEnd('\').Length).TrimStart('\')
        $target = [IO.Path]::GetFullPath((Join-Path $destination $relative))
        if (-not $target.StartsWith($destination + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Publish path escapes service directory.' }
        [pscustomobject]@{ Source=$file.FullName; Target=$target; Relative=$relative }
    }

    Stop-Service -Name $serviceName
    (Get-Service $serviceName).WaitForStatus('Stopped',[TimeSpan]::FromSeconds(20))
    $stopped = $true
    if ($serviceProcessId -gt 0) {
        $processExitDeadline = [DateTime]::UtcNow.AddSeconds(10)
        while ([DateTime]::UtcNow -lt $processExitDeadline -and (Get-Process -Id $serviceProcessId -ErrorAction SilentlyContinue)) {
            Start-Sleep -Milliseconds 100
        }
        if (Get-Process -Id $serviceProcessId -ErrorAction SilentlyContinue) { throw 'Service process did not exit after stop.' }
    }
    New-Item -ItemType Directory -Path $Backup | Out-Null
    foreach ($item in $plan) {
        $exists = Test-Path -LiteralPath $item.Target -PathType Leaf
        if ($exists -and (Get-FileHash -LiteralPath $item.Target).Hash -eq (Get-FileHash -LiteralPath $item.Source).Hash) { continue }
        $saved = Join-Path $Backup $item.Relative
        if ($exists) {
            New-Item -ItemType Directory -Force -Path (Split-Path $saved) | Out-Null
            Copy-Item -LiteralPath $item.Target -Destination $saved
        }
        $changed.Add([pscustomobject]@{ Source=$item.Source; Target=$item.Target; Saved=$saved; Existed=$exists; Copied=$false })
    }
    foreach ($item in $changed) {
        New-Item -ItemType Directory -Force -Path (Split-Path $item.Target) | Out-Null
        $item.Copied = $true
        Copy-Item -LiteralPath $item.Source -Destination $item.Target -Force
    }
    Start-Service -Name $serviceName
    (Get-Service $serviceName).WaitForStatus('Running',[TimeSpan]::FromSeconds(20))
    Start-Sleep -Seconds 3
    if ((Get-Service $serviceName).Status -ne 'Running') { throw 'Service stopped after startup.' }
    $resultObject = @{ Success=$true; Service=$serviceName; Backup=$Backup; FileCount=$changed.Count; ServiceHash=(Get-FileHash (Join-Path $destination 'Jiaolong.Service.dll')).Hash }
} catch {
    $failure = $_.Exception.Message
    $rollbackError = $null
    if ($stopped) {
        try {
            Stop-Service -Name $serviceName -ErrorAction SilentlyContinue
            foreach ($item in $changed | Where-Object Copied) {
                try {
                    if ($item.Existed) {
                        if ((Get-FileHash -LiteralPath $item.Saved).Hash -ne (Get-FileHash -LiteralPath $item.Target).Hash) {
                            Copy-Item -LiteralPath $item.Saved -Destination $item.Target -Force
                        }
                    }
                    elseif (Test-Path -LiteralPath $item.Target) { Remove-Item -LiteralPath $item.Target }
                } catch { $rollbackError = $_.Exception.Message }
            }
            Start-Service -Name $serviceName
        } catch { $rollbackError = $_.Exception.Message }
    }
    $resultObject = @{ Success=$false; Error=$failure; RollbackError=$rollbackError; Backup=$Backup }
} finally {
    if ($Result -and $resultObject) { $resultObject | ConvertTo-Json | Set-Content -LiteralPath $Result -Encoding utf8 }
}
if (-not $resultObject.Success) { exit 1 }
