using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;

namespace Jiaolong.Service.Home;

public static class HomeCapabilityCatalog
{
    public static IReadOnlyList<string> ControlKeys { get; } =
    [
        "performanceMode",
        "cpuTuning",
        "cpuTuning:curveOptimizer",
        "cpuTuning:smu",
        "cpuTuning:pbo",
        "strongCooling",
        "fanControl",
        "keyboardLighting",
        "gpuVfCurve",
        "gpuMemoryOffset",
        "gpuCoreOffset",
        "gpuFrequencyLimit",
        "gpuPowerLimit",
        "quickSetting:wifi",
        "quickSetting:bluetooth",
        "quickSetting:touchpad",
        "quickSetting:fnLock",
        "quickSetting:lidLogo"
    ];

    public static string RequiredCapability(HardwareCommand command) => command switch
    {
        SetPerformanceModeCommand => "performanceMode",
        SetMuxModeCommand => "muxMode",
        SetCpuTuningCommand or SetCpuTuningBatchCommand => "cpuTuning",
        SetGpuVfCurveCommand => "gpuVfCurve",
        SetGpuMemoryOffsetCommand => "gpuMemoryOffset",
        SetGpuCoreOffsetCommand => "gpuCoreOffset",
        SetGpuFrequencyLimitCommand => "gpuFrequencyLimit",
        SetGpuVoltageBoostCommand => "gpuVoltageBoost",
        SetGpuPowerLimitCommand => "gpuPowerLimit",
        SetGpuPowerPolicyCommand => "gpuPowerLimit",
        SetLidLogoCommand => "quickSetting:lidLogo",
        SetStrongCoolingCommand => "strongCooling",
        SetFanControlCommand or ReleaseFanControlCommand => "fanControl",
        SetKeyboardLightingCommand or RestoreKeyboardLightingPreviewCommand => "keyboardLighting",
        SetQuickSettingCommand quick => QuickSettingKey(quick.Setting),
        _ => command.GetType().Name
    };

    public static string QuickSettingKey(QuickSettingKind setting) =>
        setting == QuickSettingKind.StrongCooling
            ? "strongCooling"
            : $"quickSetting:{ToLowerCamel(setting.ToString())}";

    public static CapabilityDescriptor[] ReadOnlyControls(string reason) =>
        ControlKeys.Select(key => new CapabilityDescriptor(key, CapabilityState.ReadOnly, reason)).ToArray();

    private static string ToLowerCamel(string value) =>
        value.Length == 0 ? value : char.ToLowerInvariant(value[0]) + value[1..];
}
