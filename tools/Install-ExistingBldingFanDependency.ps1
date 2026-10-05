param([Parameter(Mandatory)][string]$Result)
$ErrorActionPreference='Stop'
if (-not [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Service dependency installation requires administrator rights.' }
$Result=[IO.Path]::GetFullPath($Result)
if (-not $Result.StartsWith('C:\Users\Administrator\AppData\Local\Temp\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Result must be in C: task temporary directory.' }
$source='F:\JiaoLongControl\Drivers\Blding'
$destination='C:\Program Files\Jiaolong Control Center\Drivers\Blding'
$hashes=@{
    'JiaoLongDriver64.dll'='A6F6B0F5C5B5523D2E6EBCE3DF54149B65E5D07C0B5E49AB91816E766DE6B65E'
    'JiaoLongDriver64.sys'='A3D7FD8F8713726A54FAD81052864373E07D64822E7525505577F27D8165ABF9'
}
$output=@{Success=$false}
try {
    foreach ($name in $hashes.Keys) {
        if ((Get-FileHash -LiteralPath (Join-Path $source $name) -Algorithm SHA256).Hash -ne $hashes[$name]) { throw "Existing dependency hash mismatch: $name" }
    }
    $signature=Get-AuthenticodeSignature -LiteralPath (Join-Path $source 'JiaoLongDriver64.sys')
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Shenzhen Bitland Information Technology') { throw 'Kernel driver signature is not the existing verified Bitland signature.' }
    foreach ($directory in @('C:\Program Files\Jiaolong Control Center','C:\Program Files\Jiaolong Control Center\Drivers',$destination)) {
        if (-not (Test-Path -LiteralPath $directory)) { New-Item -ItemType Directory -Path $directory | Out-Null }
        if ((Get-Item -LiteralPath $directory).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Refuse reparse directory: $directory" }
    }
    $driverPath=Join-Path $destination 'JiaoLongDriver64.sys'
    $existing=Get-CimInstance Win32_SystemDriver -Filter "Name='JiaoLongDriver64'"
    if ($existing -and $existing.PathName.Trim('"').Replace('\??\','') -ne $driverPath) { throw 'Existing Blding service uses a different path; refusing to replace it.' }
    foreach ($name in $hashes.Keys) {
        $target=Join-Path $destination $name
        if (Test-Path -LiteralPath $target) {
            if ((Get-Item -LiteralPath $target).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Refuse reparse dependency.' }
            if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $hashes[$name]) { throw 'Refuse to overwrite another dependency version.' }
        } else { Copy-Item -LiteralPath (Join-Path $source $name) -Destination $target }
    }
    if (-not $existing) {
        & sc.exe create JiaoLongDriver64 type= kernel start= demand binPath= $driverPath | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Could not register the existing signed driver.' }
    }
    $output=@{Success=$true;Source=$source;Destination=$destination;DriverService='JiaoLongDriver64';Signer=$signature.SignerCertificate.Subject;CreatedService=($null -eq $existing)}
} catch { $output.Error=$_.Exception.Message; throw }
finally { $output | ConvertTo-Json | Set-Content -LiteralPath $Result -Encoding utf8 }
