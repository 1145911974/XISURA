using Jiaolong.Contracts.Models;

namespace Jiaolong.Service.Home;

public sealed partial class WindowsHomeHardwareProvider
{
    private FieldCapability[] CreateFieldCapabilities(CapabilityDescriptor[] items, CpuTuningState? cpu)
    {
        bool Available(string key) => items.Any(item => item.Key == key && item.State == CapabilityState.Available);
        var result = new List<FieldCapability>();
        void Add(string field, string capability, bool readable, double? minimum, double? maximum, string unit, string source, bool writable = true)
        {
            bool available = Available(capability) && writable;
            result.Add(new(field, available ? CapabilityState.Available : readable ? CapabilityState.ReadOnly : CapabilityState.Unavailable,
                readable, available, minimum, maximum, minimum.HasValue ? 1 : null, unit, source,
                "PreservesSelectedMode", available ? DateTimeOffset.UtcNow : null, 0));
        }
        Add("acMaxFrequencyMhz", "cpuTuning", cpu?.AcFrequency() is not null, 0, 6000, "MHz", "windowsPowerApi", cpu is not null);
        Add("dcMaxFrequencyMhz", "cpuTuning", cpu?.DcFrequency() is not null, 0, 6000, "MHz", "windowsPowerApi", cpu is not null);
        Add("isBoostEnabled", "cpuTuning", cpu?.BoostEnabled is not null, null, null, "", "windowsPowerApi", cpu?.BoostEnabled is not null);
        Add("windowsPowerSchemeId", "cpuTuning", cpu?.WindowsPowerSchemeId is not null, null, null, "", "windowsPowerApi", cpu is not null);
        Add("acMinActiveCoresPercent", "cpuTuning", cpu?.AcMinActiveCoresPercent is not null, 0, 100, "%", "windowsPowerApi", cpu?.AcMinActiveCoresPercent is not null);
        Add("dcMinActiveCoresPercent", "cpuTuning", cpu?.DcMinActiveCoresPercent is not null, 0, 100, "%", "windowsPowerApi", cpu?.DcMinActiveCoresPercent is not null);
        // OEM setters can select custom/Turbo internally; no verified preserve-mode mapping exists yet.
        Add("temperatureLimitC", "cpuTuning", cpu?.TemperatureLimitC is not null, null, null, "°C", "oemModeSideEffectUnknown", false);
        Add("splWatts", "cpuTuning", cpu?.SplWatts is not null, null, null, "W", "oemModeSideEffectUnknown", false);
        Add("spptWatts", "cpuTuning", cpu?.SpptWatts is not null, null, null, "W", "oemModeSideEffectUnknown", false);
        Add("negativeCurveOptimizer", "cpuTuning:curveOptimizer", cpu?.HasCompleteCurveValues() == true, -30, 0, "", "hardwareReadback", cpu?.HasCompleteCurveValues() == true);
        Add("pboScalar", "cpuTuning:pbo", cpu?.PboScalar is >= 1 and <= 10, 1, 10, "", "hardwareReadback", cpu?.PboScalar is >= 1 and <= 10);
        Add("advancedCpuTuning", "cpuTuning:smu", cpu?.AdvancedLimits is not null, null, null, "", "advancedProtocolUnsupported", false);
        Add("coreFrequencyLimitMhz", "gpuFrequencyLimit", false, gpuClockState?.MinimumMhz, gpuClockState?.MaximumMhz, "MHz", "nvidiaSmiRangeOnly", false);
        Add("coreOffsetKhz", "gpuCoreOffset", true, -200000, 200000, "kHz", "nvapiReadback");
        Add("memoryOffsetKhz", "gpuMemoryOffset", true, -200000, 200000, "kHz", "nvapiReadback");
        Add("vfOffsetsKhz", "gpuVfCurve", true, null, null, "kHz", "nvapiReadback");
        foreach (string field in new[] { "strategy", "maximumRpm", "fixedRpm", "cpu", "gpu", "shared", "isShared" })
            Add(field, "fanControl", false, field is "maximumRpm" or "fixedRpm" ? 1500 : null,
                field is "maximumRpm" or "fixedRpm" ? 6000 : null, "", "ecSubmission");
        Add("strongCooling", "strongCooling", false, null, null, "", "ecSubmission");
        foreach (string field in new[] { "effect", "brightness", "hex", "speed", "logo" })
            Add(field, "keyboardLighting", false, field == "brightness" ? 0 : null, field == "brightness" ? 3 : null, "", "oemLightingSubmission");
        return result.ToArray();
    }
}
