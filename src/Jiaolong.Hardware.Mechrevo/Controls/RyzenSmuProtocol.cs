namespace Jiaolong.Hardware.Mechrevo.Controls;

public static class RyzenSmuProtocol
{
    public static readonly int DragonRangeCodeName = 28;
    public static readonly uint Mp1CommandAddress = 0x03B10530;
    public static readonly uint Mp1ResponseAddress = 0x03B1057C;
    public static readonly uint Mp1ArgumentAddress = 0x03B109C4;
    public static readonly uint RaphaelCommandAddress = 0x03B10524;
    public static readonly uint RaphaelResponseAddress = 0x03B10570;
    public static readonly uint RaphaelArgumentAddress = 0x03B10A40;
    public static readonly uint SetCurveOptimizerMp1Command = 0x36;
    public static readonly uint SetCurveOptimizerCommand = 0x07;
    public static readonly uint GetPboScalarCommand = 0x6D;
    public static readonly uint SetPboScalarCommand = 0x5B;

    public static uint EncodeCurve(int value)
    {
        if (value is < -30 or > 0)
            throw new ArgumentOutOfRangeException(nameof(value));

        return (uint)(0x100000 - (uint)(-value));
    }

    public static uint EncodePerCoreCurve(int coreIndex, int value)
    {
        if (coreIndex is < 0 or > 7) throw new ArgumentOutOfRangeException(nameof(coreIndex));
        if (value is < -30 or > 0) throw new ArgumentOutOfRangeException(nameof(value));

        uint encodedValue = unchecked((uint)((value < 0 ? 1_048_576 : 0) + value)) & 0xFFFF;
        return ((uint)(coreIndex / 8) << 28) | ((uint)(coreIndex % 8) << 20) | encodedValue;
    }
}
