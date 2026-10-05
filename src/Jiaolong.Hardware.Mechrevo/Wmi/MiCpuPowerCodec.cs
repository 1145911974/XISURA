using Jiaolong.Hardware.Mechrevo.Controls;

namespace Jiaolong.Hardware.Mechrevo.Wmi;

public static class MiCpuPowerCodec
{
    public const byte MethodName = 23;

    public static byte[] CustomMode(bool enabled) => [enabled ? (byte)1 : (byte)0];

    public static byte[] Encode(CpuTuningField field, int value) => field switch
    {
        CpuTuningField.TemperatureLimitC => [4, checked((byte)value)],
        CpuTuningField.SplWatts => [2, checked((byte)value)],
        CpuTuningField.SpptWatts => [3, checked((byte)value)],
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Field is not an MI CPU power field.")
    };

    public static int? Read(ReadOnlySpan<byte> response, CpuTuningField field)
    {
        // FA00/1700 returns only CMEN at byte 4; bytes 5–7 are not CPU limits.
        return null;
    }

    public static bool? ReadCustomMode(ReadOnlySpan<byte> response) =>
        response.Length > 4 && response[4] <= 1 ? response[4] == 1 : null;
}
