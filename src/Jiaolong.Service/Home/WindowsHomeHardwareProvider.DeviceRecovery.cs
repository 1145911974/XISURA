using System.Security;
using Microsoft.Win32;
using Jiaolong.Contracts.Models;

namespace Jiaolong.Service.Home;

public sealed partial class WindowsHomeHardwareProvider
{
    public Task<string?> ReadDeviceRecoveryKeyAsync(HardwareIdentity identity, CancellationToken token)
    {
        return Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();

            string? machineGuid;
            try
            {
                using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var cryptography = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography", writable: false);
                machineGuid = cryptography?.GetValue("MachineGuid") as string;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException or PlatformNotSupportedException)
            {
                token.ThrowIfCancellationRequested();
                return null;
            }

            token.ThrowIfCancellationRequested();
            string? firmwareUuid = ReadManagementValue("Win32_ComputerSystemProduct", "UUID");
            token.ThrowIfCancellationRequested();
            return DeviceRecoveryKey.Create(machineGuid, firmwareUuid, identity);
        }, token);
    }
}
