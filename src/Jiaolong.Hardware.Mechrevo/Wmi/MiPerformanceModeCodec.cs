using Jiaolong.Contracts.Models;

namespace Jiaolong.Hardware.Mechrevo.Wmi;

/// <summary>
/// Translates the contract's semantic mode names to the verified MRID6-23
/// MICommonInterface values. The device order is not the enum declaration order.
/// </summary>
public static class MiPerformanceModeCodec
{
    public static bool TryEncode(PerformanceMode mode, out byte wireValue)
    {
        switch (mode)
        {
            case PerformanceMode.Balanced:
                wireValue = 0;
                return true;
            case PerformanceMode.Turbo:
                wireValue = 1;
                return true;
            case PerformanceMode.Quiet:
                wireValue = 2;
                return true;
            default:
                wireValue = 0;
                return false;
        }
    }

    public static PerformanceMode? Decode(byte wireValue) => wireValue switch
    {
        0 => PerformanceMode.Balanced,
        1 => PerformanceMode.Turbo,
        2 => PerformanceMode.Quiet,
        _ => null
    };
}
