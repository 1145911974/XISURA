using Jiaolong.Contracts.Commands;

namespace Jiaolong.Hardware.Mechrevo.Controls;

public sealed record RyzenSmuLimitWrite(bool Mp1, uint Command, uint Argument, uint? FallbackRsmu = null);
public sealed record CpuSmuLimitSnapshot(AdvancedCpuTuningPlan Limits, double? OemSplWatts, uint PmTableVersion = 0);

public interface ISmuCpuLimitTransport
{
    CpuSmuLimitSnapshot ReadLimitSnapshot(CancellationToken cancellationToken);
    void WriteAdvancedLimit(RyzenSmuLimitWrite write, CancellationToken cancellationToken);
}

public static class RyzenSmuAdvancedLimits
{
    public static CpuSmuLimitSnapshot DecodeSnapshot(uint version, ReadOnlySpan<float> table) =>
        new(DecodePmTable(version, table), version == 0x540108 ? Quantize(table[6], 1, 250) : null, version);
    // This verified read layout does not establish independent setter or recovery support.
    public static AdvancedCpuTuningPlan DecodePmTable(uint version, ReadOnlySpan<float> table)
    {
        if (version is not (0x540105 or 0x540108) || table.Length < 64)
            throw new InvalidOperationException("advancedCpuPmTableUnsupported");
        // CPUDoc 60eb3c9, SystemInfo.cs:683-699 explicitly maps 540105. No OEM SPL/FPPT aliases are established.
        if (version == 0x540105)
            return new AdvancedCpuTuningPlan(
                PptWatts: RestorablePower(table[2]),
                TdcCurrentMilliamps: RestorableCurrent(table[8]),
                EdcCurrentMilliamps: RestorableCurrent(table[61]),
                Mp1TemperatureC: Integral(table[10], 40, 100));
        var limits = new AdvancedCpuTuningPlan(
            StapmWatts: Quantize(table[0], 1, 250),
            FastPptWatts: Quantize(table[2], 1, 250),
            SlowPptWatts: Quantize(table[4], 1, 250),
            PptWatts: Quantize(table[2], 1, 250),
            VrmCurrentMilliamps: checked((int)Math.Round(Quantize(table[8], 1, 200) * 1000)),
            TdcCurrentMilliamps: checked((int)Math.Round(Quantize(table[8], 1, 200) * 1000)),
            EdcCurrentMilliamps: checked((int)Math.Round(Quantize(table[62], 1, 200) * 1000)),
            Mp1TemperatureC: Integral(table[10], 40, 100),
            RsmuTemperatureC: Integral(table[10], 40, 100));
        return limits;
    }

    public static object? ReadField(AdvancedCpuTuningPlan limits, CpuTuningField field) => field switch
    {
        CpuTuningField.StapmWatts => (object?)limits.StapmWatts,
        CpuTuningField.FastPptWatts => limits.FastPptWatts,
        CpuTuningField.SlowPptWatts => limits.SlowPptWatts,
        CpuTuningField.PptWatts => limits.PptWatts,
        CpuTuningField.VrmCurrentMilliamps => limits.VrmCurrentMilliamps,
        CpuTuningField.TdcCurrentMilliamps => limits.TdcCurrentMilliamps,
        CpuTuningField.EdcCurrentMilliamps => limits.EdcCurrentMilliamps,
        CpuTuningField.Mp1TemperatureC => limits.Mp1TemperatureC,
        CpuTuningField.RsmuTemperatureC => limits.RsmuTemperatureC,
        _ => throw new ArgumentOutOfRangeException(nameof(field))
    };

    internal static RyzenSmuLimitWrite BuildField(CpuTuningField field, object? value, bool restoring, uint pmTableVersion = 0)
    {
        bool directPpt = field == CpuTuningField.PptWatts && pmTableVersion == 0x540105;
        if (directPpt && value is double power && (!double.IsFinite(power) || Math.Abs(power * 1000 - Math.Round(power * 1000)) > 0.000001))
            throw new ArgumentException("advancedCpuLimitPrecisionInvalid", nameof(value));
        // 120W is the observed V35 native Turbo PPT, not a hardware safety rating.
        if (directPpt && !restoring)
            return value is double target && target is >= 45 and <= 120
                ? new(true, 0x3E, checked((uint)Math.Round(target * 1000)))
                : throw new ArgumentOutOfRangeException(nameof(value));
        RyzenSmuLimitWrite Route(RyzenSmuLimitWrite write) => directPpt ? write with { Mp1 = true, Command = 0x3E, FallbackRsmu = null }
            : field == CpuTuningField.TdcCurrentMilliamps && pmTableVersion == 0x540105
                ? write with { Mp1 = true, Command = 0x3C, FallbackRsmu = null }
            : field == CpuTuningField.EdcCurrentMilliamps && pmTableVersion == 0x540105
                ? write with { Mp1 = true, Command = 0x3D, FallbackRsmu = null } : write;
        var plan = field switch
        {
            CpuTuningField.StapmWatts when value is double watts => new AdvancedCpuTuningPlan(StapmWatts: watts),
            CpuTuningField.FastPptWatts when value is double watts => new(FastPptWatts: watts),
            CpuTuningField.SlowPptWatts when value is double watts => new(SlowPptWatts: watts),
            CpuTuningField.PptWatts when value is double watts => new(PptWatts: watts),
            CpuTuningField.VrmCurrentMilliamps when value is int current => new(VrmCurrentMilliamps: current),
            CpuTuningField.TdcCurrentMilliamps when value is int current => new(TdcCurrentMilliamps: current),
            CpuTuningField.EdcCurrentMilliamps when value is int current => new(EdcCurrentMilliamps: current),
            CpuTuningField.Mp1TemperatureC when value is int temperature => new(Mp1TemperatureC: temperature),
            CpuTuningField.RsmuTemperatureC when value is int temperature => new(RsmuTemperatureC: temperature),
            _ => throw new ArgumentException("advancedCpuLimitValueInvalid", nameof(value))
        };
        if (restoring && value is double original && double.IsFinite(original) && original is >= 1 and <= 250)
        {
            // Firmware defaults can exceed the UI input range. Restore only a captured snapshot.
            var template = BuildSingle(field switch
            {
                CpuTuningField.StapmWatts => new(StapmWatts: 45),
                CpuTuningField.FastPptWatts => new(FastPptWatts: 45),
                CpuTuningField.SlowPptWatts => new(SlowPptWatts: 45),
                CpuTuningField.PptWatts => new(PptWatts: 45),
                _ => throw new ArgumentOutOfRangeException(nameof(field))
            })!;
            return Route(template with { Argument = checked((uint)Math.Round(original * 1000)) });
        }
        return Route(BuildSingle(plan) ?? throw new ArgumentOutOfRangeException(nameof(value)));
    }

    private static int RestorableCurrent(float value)
    {
        double rounded = Quantize(value, 1, 200);
        double tolerance = 2d * Math.Abs((double)MathF.BitIncrement(value) - value);
        if (Math.Abs(value - rounded) > tolerance) throw new InvalidOperationException("advancedCpuLimitPrecisionInvalid");
        return checked((int)Math.Round(rounded * 1000));
    }

    private static double RestorablePower(float value)
    {
        double rounded = Quantize(value, 1, 250);
        // PM floats can differ by two ULPs from the integer-mW command. Do not erase real sub-mW values.
        double tolerance = 2d * Math.Abs((double)MathF.BitIncrement(value) - value);
        return Math.Abs(value - rounded) <= tolerance ? rounded : throw new InvalidOperationException("advancedCpuLimitPrecisionInvalid");
    }

    private static double Quantize(float value, double minimum, double maximum)
    {
        double rounded = Math.Round(value, 3);
        return float.IsFinite(value) && rounded >= minimum && rounded <= maximum
            ? rounded : throw new InvalidOperationException("advancedCpuLimitReadInvalid");
    }

    private static int Integral(float value, int minimum, int maximum)
    {
        double rounded = Quantize(value, minimum, maximum);
        return rounded == Math.Round(rounded) ? (int)rounded :
            throw new InvalidOperationException("advancedCpuIntegralLimitReadInvalid");
    }

    // AM5_V1 mapping used by the installed 10.16.27 console for 7745HX.
    // Bounds are software policy; they are not firmware maximum readings.
    public static RyzenSmuLimitWrite? BuildSingle(AdvancedCpuTuningPlan plan)
    {
        var empty = new AdvancedCpuTuningPlan();
        if (plan.StapmWatts is double stapm && (plan with { StapmWatts = null }) == empty)
            return Power(true, 79, stapm);
        if (plan.FastPptWatts is double fast && (plan with { FastPptWatts = null }) == empty)
            return Power(true, 62, fast);
        if (plan.SlowPptWatts is double slow && (plan with { SlowPptWatts = null }) == empty)
            return Power(true, 95, slow, 203);
        if (plan.PptWatts is double ppt && (plan with { PptWatts = null }) == empty)
            return Power(false, 86, ppt);
        if (plan.VrmCurrentMilliamps is int vrm && (plan with { VrmCurrentMilliamps = null }) == empty)
            return vrm is >= 1_000 and <= 200_000 ? new(true, 60, (uint)vrm, 87) : null;
        if (plan.TdcCurrentMilliamps is int tdc && (plan with { TdcCurrentMilliamps = null }) == empty)
            return tdc is >= 1_000 and <= 200_000 ? new(false, 0x57, (uint)tdc) : null;
        if (plan.EdcCurrentMilliamps is int edc && (plan with { EdcCurrentMilliamps = null }) == empty)
            return edc is >= 1_000 and <= 200_000 ? new(true, 61, (uint)edc, 88) : null;
        if (plan.Mp1TemperatureC is int mp1 && (plan with { Mp1TemperatureC = null }) == empty)
            return mp1 is >= 40 and <= 100 ? new(true, 63, (uint)mp1) : null;
        if (plan.RsmuTemperatureC is int rsmu && (plan with { RsmuTemperatureC = null }) == empty)
            return rsmu is >= 40 and <= 100 ? new(false, 89, (uint)rsmu) : null;
        return null;
    }

    private static RyzenSmuLimitWrite? Power(bool mp1, uint command, double watts, uint? fallback = null) =>
        double.IsFinite(watts) && watts is >= 45 and <= 75
            ? new(mp1, command, checked((uint)Math.Round(watts * 1000)), fallback) : null;
}
