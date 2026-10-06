param([Parameter(Mandatory)][string]$Destination)
$ErrorActionPreference = 'Stop'

# MonoPosixHelper imports this CRT even though the application itself is managed.
# Redistribute the release CRT from Visual Studio's documented Redist directory,
# never an arbitrary system DLL or the non-redistributable debug runtime.
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (!(Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio C++ redistributable tools are required to build the offline installer.' }
$installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$installation) { throw 'Visual Studio C++ redistributable tools were not found.' }
$redistRoot = Join-Path $installation 'VC\Redist\MSVC'
$runtime = Get-ChildItem -LiteralPath $redistRoot -Directory |
    Where-Object Name -Match '^\d+\.\d+\.\d+(\.\d+)?$' |
    Sort-Object { [version]$_.Name } -Descending |
    ForEach-Object { Get-ChildItem -Path (Join-Path $_.FullName 'x64\Microsoft.VC*.CRT\vcruntime140.dll') -File } |
    Select-Object -First 1
if (!$runtime) { throw 'The redistributable x64 vcruntime140.dll was not found.' }
# MSBuild can inherit PowerShell 7's module path while launching Windows PowerShell.
Import-Module (Join-Path $PSHOME 'Modules\Microsoft.PowerShell.Security\Microsoft.PowerShell.Security.psd1')
$signature = Get-AuthenticodeSignature -LiteralPath $runtime.FullName
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') {
    throw 'The Visual C++ runtime must have a valid Microsoft signature.'
}
Copy-Item -LiteralPath $runtime.FullName -Destination (Join-Path $Destination 'vcruntime140.dll') -Force
Write-Output "Bundled Microsoft x64 Visual C++ runtime $($runtime.VersionInfo.FileVersion)"
