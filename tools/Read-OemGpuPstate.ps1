$ErrorActionPreference = 'Stop'
$dll = 'C:\Program Files (x86)\蛟龙游戏控制中心\KaronOC.dll'
$expectedHash = '6EDFBF9B35FAAA3187F30085C8DAA20A4CB3D682E273C5190F709ACCC058DEE6'
if (-not [Environment]::Is64BitProcess -or -not (Test-Path -LiteralPath $dll)) {
    throw 'Verified x64 OEM GPU reader unavailable'
}
if ((Get-AuthenticodeSignature -LiteralPath $dll).Status -ne 'Valid' -or
    (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash -ne $expectedHash) {
    throw 'OEM GPU reader signature or hash mismatch'
}
$env:TEMP = 'C:\Users\Administrator\AppData\Local\Temp'
$env:TMP = $env:TEMP
if (-not ('OemGpuPstateReader' -as [type])) { Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class OemGpuPstateReader
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ReadOffsets(ref int core, ref int memory);
    public static int[] Read(string path)
    {
        IntPtr library = NativeLibrary.Load(path);
        try
        {
            var read = Marshal.GetDelegateForFunctionPointer<ReadOffsets>(
                NativeLibrary.GetExport(library, "GetPstatesLevel0Settings"));
            int core = 0, memory = 0;
            int status = read(ref core, ref memory);
            if (status != 0) throw new InvalidOperationException($"OEM GPU read returned {status}");
            return new[] { core, memory };
        }
        finally { NativeLibrary.Free(library); }
    }
}
'@ }
$offsets = [OemGpuPstateReader]::Read($dll)
[ordered]@{
    capturedAtUtc = [DateTimeOffset]::UtcNow
    source = 'signed-oem-KaronOC-read-only'
    coreRaw = $offsets[0]
    memoryRaw = $offsets[1]
    unit = 'raw-driver-offset; matched NVAPI kHz at 0 and +10000 on this machine'
} | ConvertTo-Json -Compress
