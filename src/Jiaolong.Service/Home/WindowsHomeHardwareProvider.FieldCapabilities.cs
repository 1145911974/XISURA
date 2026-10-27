using Jiaolong.Contracts.Models;

namespace Jiaolong.Service.Home;

public sealed partial class WindowsHomeHardwareProvider
{
    private FieldCapability[] CreateFieldCapabilities(CapabilityDescriptor[] items, CpuTuningState? cpu)
    {
        bool Available(string key) => items.Any(item => item.Key == key && item.State == CapabilityState.Available);
        // Capability snapshots outlive individual curve edits; advertise the driver envelope.
        var coreRange = gpuVfCache?.PstateOffsetRange(memory: false, preserveCurve: false);
        var memoryRange = gpuVfCache?.PstateOffsetRange(memory: true);
        bool vfReadable = gpuVfCache is { Error: null, Nodes.Length: 127 };
        bool coreReadable = coreRange.HasValue;
        bool memoryReadable = memoryRange.HasValue;
        var result = new List<FieldCapability>();
        void Add(string field, string capability, bool readable, double? minimum, double? maximum, string unit, string source, bool writable = true,
            string modeSideEffect = "PreservesSelectedMode", double? step = null)
        {
            bool available = Available(capability) && writable;
            result.Add(new(field, available ? CapabilityState.Available : readable ? CapabilityState.ReadOnly : CapabilityState.Unavailable,
                readable, available, minimum, maximum, step ?? (minimum.HasValue ? (field is "coreOffsetKhz" or "memoryOffsetKhz" ? 1000 : 1) : null), unit, source,
                modeSideEffect, available ? DateTimeOffset.UtcNow : null, 0));
        }
        Add("acMaxFrequencyMhz", "cpuTuning", cpu?.AcFrequency() is not null, 0, 6000, "MHz", "windowsPowerApi", cpu is not null);
        Add("dcMaxFrequencyMhz", "cpuTuning", cpu?.DcFrequency() is not null, 0, 6000, "MHz", "windowsPowerApi", cpu is not null);
        Add("isBoostEnabled", "cpuTuning", cpu?.BoostEnabled is not null, null, null, "", "windowsPowerApi", cpu?.BoostEnabled is not null);
        Add("windowsPowerSchemeId", "cpuTuning", cpu?.WindowsPowerSchemeId is not null, null, null, "", "windowsPowerApi", cpu is not null);
        Add("acMinActiveCoresPercent", "cpuTuning", cpu?.AcMinActiveCoresPercent is not null, 0, 100, "%", "windowsPowerApi", cpu?.AcMinActiveCoresPercent is not null);
        Add("dcMinActiveCoresPercent", "cpuTuning", cpu?.DcMinActiveCoresPercent is not null, 0, 100, "%", "windowsPowerApi", cpu?.DcMinActiveCoresPercent is not null);
        // OEM setters can select custom/Turbo internally; no verified preserve-mode mapping exists yet.
        Add("temperatureLimitC", "cpuTuning", cpu?.TemperatureLimitC is not null, null, null, "°C", "oemModeSideEffectUnknown", false, "Unknown");
        Add("splWatts", "cpuTuning", cpu?.SplWatts is not null || cpu?.ObservedSplWatts is not null, null, null, "W", "oemModeSideEffectUnknown", false, "Unknown");
        Add("spptWatts", "cpuTuning", cpu?.SpptWatts is not null || cpu?.AdvancedLimits?.FastPptWatts is not null, null, null, "W", "oemModeSideEffectUnknown", false, "Unknown");
        Add("negativeCurveOptimizer", "cpuTuning:curveOptimizer", cpu?.HasCompleteCurveValues() == true, -50, 0, "", "hardwareReadback", cpu?.HasCompleteCurveValues() == true);
        Add("pboScalar", "cpuTuning:pbo", cpu?.PboScalar is >= 1 and <= 10, 1, 10, "", "hardwareReadback", cpu?.PboScalar is >= 1 and <= 10);
        Add("advancedCpuTuning", "cpuTuning:smu", cpu?.AdvancedLimits is not null, null, null, "", "advancedProtocolUnsupported", false);
        Add("pptWatts", "cpuTuning:smu", cpu?.AdvancedLimits?.PptWatts is not null, 45, 120, "W", "hardwareReadback",
            CanWriteDirectPptLimit(smuLimitCache) && cpu?.AdvancedLimits?.PptWatts == smuLimitCache?.Limits.PptWatts, step: 0.001);
        Add("mp1TemperatureC", "cpuTuning:smu", cpu?.AdvancedLimits?.Mp1TemperatureC is not null, 40, 100, "°C", "hardwareReadback",
            CanWriteDirectMp1Limit(smuLimitCache) && cpu?.AdvancedLimits?.Mp1TemperatureC == smuLimitCache?.Limits.Mp1TemperatureC);
        Add("tdcCurrentMilliamps", "cpuTuning:smu", cpu?.AdvancedLimits?.TdcCurrentMilliamps is not null, 1000, 200000, "mA", "hardwareReadback",
            CanWriteDirectTdcLimit(smuLimitCache) && cpu?.AdvancedLimits?.TdcCurrentMilliamps == smuLimitCache?.Limits.TdcCurrentMilliamps);
        Add("edcCurrentMilliamps", "cpuTuning:smu", cpu?.AdvancedLimits?.EdcCurrentMilliamps is not null, 1000, 200000, "mA", "hardwareReadback",
            CanWriteDirectEdcLimit(smuLimitCache) && cpu?.AdvancedLimits?.EdcCurrentMilliamps == smuLimitCache?.Limits.EdcCurrentMilliamps);
        Add("coreFrequencyLimitMhz", "gpuFrequencyLimit", false, gpuClockState?.MinimumMhz, gpuClockState?.MaximumMhz, "MHz", "nvidiaSmiSubmission",
            gpuClockState is { Error: null, MinimumMhz: > 0 } clock && clock.MaximumMhz >= clock.MinimumMhz);
        Add("coreOffsetKhz", "gpuCoreOffset", coreReadable, coreRange?.MinimumKhz, coreRange?.MaximumKhz, "kHz",
            coreReadable ? "nvapiReadback" : gpuVfCache?.Error ?? "gpuCoreOffsetReadFailed", coreReadable);
        Add("memoryOffsetKhz", "gpuMemoryOffset", memoryReadable, memoryRange?.MinimumKhz, memoryRange?.MaximumKhz, "kHz",
            memoryReadable ? "nvapiReadback" : gpuVfCache?.Error ?? "gpuMemoryOffsetReadFailed", memoryReadable);
        Add("vfOffsetsKhz", "gpuVfCurve", vfReadable, null, null, "kHz",
            vfReadable ? "nvapiReadback" : gpuVfCache?.Error ?? "gpuVfReadFailed", vfReadable);
        Add("strategy", "fanControl", false, null, null, "", "ecFanControl");
        foreach (string field in new[] { "maximumRpm", "fixedRpm", "cpu", "gpu", "shared", "isShared" })
            Add(field, "fanControl", false, field is "maximumRpm" or "fixedRpm" ? 1800 : null,
                field is "maximumRpm" or "fixedRpm" ? 5800 : null,
                field is "maximumRpm" or "fixedRpm" ? "RPM" : "", "ecFanControl",
                step: field is "maximumRpm" or "fixedRpm" ? 100 : null);
        Add("strongCooling", "strongCooling", false, null, null, "", "oemAndEcFanControl");
        foreach (string field in new[] { "effect", "brightness", "hex" })
            Add(field, "keyboardLighting", Available("keyboardLighting"), field == "brightness" ? 0 : null,
                field == "brightness" ? 3 : null, "", field == "effect" ? "oemModeOrSoftwareEffectReadback" : "oemLightingReadback");
        // Static has no speed register; its observable canonical value is always one.
        Add("speed", "keyboardLighting", Available("keyboardLighting"),
            null, null, "", "staticCanonicalOrSoftwareEffectReadback");
        Add("logo", "keyboardLighting", Available("quickSetting:lidLogo"), null, null, "", "oemLogoReadback");
        return result.ToArray();
    }
}
