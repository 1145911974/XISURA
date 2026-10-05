$ErrorActionPreference = 'Stop'
$source = 'F:\JiaoLong7.3\WinRing0x64.sys'
$destination = 'C:\Program Files\Jiaolong Control Center\WinRing0x64.sys'
$expected = '11BD2C9F9E2397C9A16E0990E4ED2CF0679498FE0FD418A3DFDAC60B5C160EE5'
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Administrator token required' }
if ((Get-CimInstance Win32_Service -Filter "Name='JiaolongControlService'").PathName.Trim('"') -ne
    'C:\Program Files\Jiaolong Control Center\Jiaolong.Service.exe') { throw 'Unexpected service installation path' }
if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $expected -or
    (Get-AuthenticodeSignature -LiteralPath $source).Status -ne 'Valid') { throw 'Installed WinRing0 driver identity mismatch' }
if (Test-Path -LiteralPath $destination) {
    if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $expected) { throw 'Existing destination differs' }
    'Verified existing WinRing0 dependency'
    return
}
Copy-Item -LiteralPath $source -Destination $destination
if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $expected) { throw 'Copied WinRing0 dependency differs' }
'Installed verified WinRing0 dependency beside service executable'
