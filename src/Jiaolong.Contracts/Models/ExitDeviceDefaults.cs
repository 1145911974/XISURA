using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Jiaolong.Contracts.Models;

public sealed record ExitDeviceDefaults(int SchemaVersion, string DeviceKey, int? CurveOptimizerAll);

public static class DeviceRecoveryKey
{
    public static string? Create(string? machineGuid, string? firmwareUuid, HardwareIdentity? identity)
    {
        if (!UsableGuid(machineGuid, out var machine) || !UsableGuid(firmwareUuid, out var firmware) ||
            identity is null || new[] { identity.BoardProduct, identity.BiosVersion, identity.CpuModel, identity.GpuName }
                .Any(string.IsNullOrWhiteSpace)) return null;
        // A model/BIOS match alone cannot authorize another computer's override.
        var hardware = new HardwareIdentity(Normalize(identity.BoardProduct), Normalize(identity.BiosVersion),
            Normalize(identity.CpuModel), Normalize(identity.GpuName));
        string input = JsonSerializer.Serialize(new { version = 1, machine, firmware, hardware });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    }

    public static bool IsValid(string? key) => key is { Length: 64 } && key.All(char.IsAsciiHexDigit);

    private static string Normalize(string value) => value.Trim().ToUpperInvariant();
    private static bool UsableGuid(string? value, out Guid guid) => Guid.TryParse(value, out guid) &&
        guid != Guid.Empty && guid != new Guid("ffffffff-ffff-ffff-ffff-ffffffffffff");
}
