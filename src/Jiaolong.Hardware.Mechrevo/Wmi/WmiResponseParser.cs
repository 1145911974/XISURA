namespace Jiaolong.Hardware.Mechrevo.Wmi;

public enum DataQuality
{
    Good,
    Stale,
    Unknown
}

public readonly record struct ParsedValue<T>(T? Value, DataQuality Quality)
    where T : struct
{
    public static ParsedValue<T> Unknown => new(default, DataQuality.Unknown);
}

public static class WmiResponseParser
{
    public static (double? CpuRpm, double? GpuRpm) ReadFanRpmPair(ReadOnlySpan<byte> buffer)
    {
        var cpu = ReadUInt16(buffer, 4).Value;
        var gpu = ReadUInt16(buffer, 6).Value;
        return (PlausibleRpm(cpu), PlausibleRpm(gpu));
    }

    public static double? ReadCpuTemperature(ReadOnlySpan<byte> buffer)
    {
        var temperature = ReadByte(buffer, 4).Value;
        return temperature is > 0 and <= 150 ? temperature : null;
    }

    public static ParsedValue<byte> ReadByte(ReadOnlySpan<byte> buffer, int responseIndex)
    {
        if (responseIndex < 0 || responseIndex >= buffer.Length) return ParsedValue<byte>.Unknown;
        return new ParsedValue<byte>(buffer[responseIndex], DataQuality.Good);
    }

    public static ParsedValue<ushort> ReadUInt16(ReadOnlySpan<byte> buffer, int responseIndex)
    {
        if (responseIndex < 0 || responseIndex > buffer.Length - sizeof(ushort)) return ParsedValue<ushort>.Unknown;
        return new ParsedValue<ushort>(BitConverter.ToUInt16(buffer.Slice(responseIndex, sizeof(ushort))), DataQuality.Good);
    }

    public static ParsedValue<uint> ReadUInt32(ReadOnlySpan<byte> buffer, int responseIndex)
    {
        if (responseIndex < 0 || responseIndex > buffer.Length - sizeof(uint)) return ParsedValue<uint>.Unknown;
        return new ParsedValue<uint>(BitConverter.ToUInt32(buffer.Slice(responseIndex, sizeof(uint))), DataQuality.Good);
    }

    private static double? PlausibleRpm(ushort? value) => value is ushort rpm ? rpm : null;
}
