using System.Text.Json;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;

namespace Jiaolong.Service.Home;

public sealed record CompiledControlPage(PageSelection Selection, Dictionary<string, SettingRequest> Settings, string[] Skipped);
public sealed record CompiledControlPlan(CompiledControlPage[] Pages, long CatalogRevision);

// Compilation consumes Settings only. Editor payloads are never hardware instructions.
public static class ControlPlanCompiler
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Dictionary<ControlPageId, string[]> Known = new()
    {
        [ControlPageId.Performance] = ["temperatureLimitC", "splWatts", "spptWatts", "pptWatts", "mp1TemperatureC", "tdcCurrentMilliamps", "edcCurrentMilliamps", "acMaxFrequencyMhz", "dcMaxFrequencyMhz", "isBoostEnabled", "windowsPowerSchemeId", "acMinActiveCoresPercent", "dcMinActiveCoresPercent", "negativeCurveOptimizer", "advancedCpuTuning", "pboScalar"],
        [ControlPageId.Gpu] = ["coreFrequencyLimitMhz", "coreOffsetKhz", "memoryOffsetKhz", "vfOffsetsKhz", "voltageBoost", "powerLimit", "muxMode"],
        [ControlPageId.Fan] = ["profile", "strategy", "maximumRpm", "fixedRpm", "cpu", "gpu", "shared", "isShared", "strongCooling"],
        [ControlPageId.Lighting] = ["effect", "brightness", "hex", "speed", "logo"]
    };

    public static CompiledControlPlan Compile(ControlRequest request, ControlPresetCatalog catalog, string owner,
        CapabilitySnapshot capabilities, long epoch)
    {
        if (request.OperationId == Guid.Empty || !Enum.IsDefined(request.Origin) || !Enum.IsDefined(request.LogicalMode) ||
            request.NativeMode is not (PerformanceMode.Quiet or PerformanceMode.Balanced or PerformanceMode.Turbo) ||
            request.Pages is null || request.Pages.Length > 4 || request.Pages.Any(page => page is null || !Enum.IsDefined(page.Page) ||
                page.Key.Mode != request.LogicalMode || page.Key.Slot is < 1 or > 3 || page.Revision <= 0) ||
            request.Pages.Select(page => page.Page).Distinct().Count() != request.Pages.Length)
            throw new InvalidDataException("invalidControlRequest");
        long revision = catalog.Revision(owner);
        var selected = request.Pages.Where(page => page.Follow).OrderBy(page => page.Page).ToArray();
        var envelopes = catalog.Resolve(owner, selected);
        var pages = new List<CompiledControlPage>();
        for (int i = 0; i < selected.Length; i++)
        {
            var envelope = envelopes[i];
            if (!PresetValidation.Validate(envelope).IsValid) throw new InvalidDataException("invalidPreset");
            pages.Add(CompilePage(selected[i], envelope.Settings, capabilities, epoch));
        }
        if (catalog.Revision(owner) != revision) throw new InvalidDataException("catalogChangedDuringCompilation");
        return new(pages.ToArray(), revision);
    }

    internal static CompiledControlPlan CompileTransient(IEnumerable<CompiledControlPage> pages, long revision,
        CapabilitySnapshot capabilities, long epoch) => new(pages.Select(page =>
    {
        var compiled = CompilePage(page.Selection, page.Settings, capabilities, epoch);
        return compiled with { Skipped = compiled.Skipped.Concat(page.Skipped).Distinct().ToArray() };
    }).ToArray(), revision);

    private static CompiledControlPage CompilePage(PageSelection selection, Dictionary<string, SettingRequest> source,
        CapabilitySnapshot capabilities, long epoch)
    {
        var settings = new Dictionary<string, SettingRequest>(StringComparer.OrdinalIgnoreCase);
        var skipped = new List<string>();
        foreach (var (inputField, intent) in ExpandCpuSettings(selection.Page, source))
        {
            string field = Known[selection.Page].FirstOrDefault(candidate => candidate.Equals(inputField, StringComparison.OrdinalIgnoreCase)) ?? inputField;
            if (field.Equals("profile", StringComparison.OrdinalIgnoreCase) && selection.Page == ControlPageId.Fan) continue;
            if (!Known[selection.Page].Contains(field, StringComparer.OrdinalIgnoreCase))
            { if (intent.Intent != SettingIntent.Keep) throw new InvalidDataException("unknownField:" + field); continue; }
            if (intent.Intent == SettingIntent.Keep) continue;
            if (intent.Intent == SettingIntent.Set)
            {
                var capability = capabilities.Fields.FirstOrDefault(item => item.Key.Equals(field, StringComparison.OrdinalIgnoreCase));
                bool optional = PresetValidation.IsOptionalField(field);
                if (!PresetValidation.IsVerifiedWritable(capability, epoch))
                { if (optional) { skipped.Add(field); continue; } throw new InvalidDataException("fieldNotWritable:" + field); }
                if (intent.Value!.Value.ValueKind == JsonValueKind.Number &&
                    (!intent.Value.Value.TryGetDouble(out double value) || !PresetValidation.WithinRange(value, capability)))
                    throw new InvalidDataException("fieldOutOfRange:" + field);
                if (field is "temperatureLimitC" or "splWatts" or "spptWatts" &&
                    (capability.NativeModeSideEffect != "PreservesSelectedMode" || capabilities.IsOnAcPower != true ||
                     capability.Minimum is null || capability.Maximum is null))
                    throw new InvalidDataException("nativeModeSideEffectUnverified:" + field);
            }
            settings.Add(field, intent);
        }
        return new(selection, settings, skipped.ToArray());
    }

    internal static AdvancedCpuTuningPlan? ResidualSmu(AdvancedCpuTuningPlan? plan)
    {
        if (plan is null) return null;
        var smu = plan with { PptWatts = null, Mp1TemperatureC = null, TdcCurrentMilliamps = null, EdcCurrentMilliamps = null, PboScalar = null, CurveOptimizerAll = null, PerCoreCurveOptimizer = null };
        return smu == new AdvancedCpuTuningPlan() ? null : smu;
    }

    private static Dictionary<string, SettingRequest> ExpandCpuSettings(ControlPageId page, Dictionary<string, SettingRequest> source)
    {
        var values = new Dictionary<string, SettingRequest>(source, StringComparer.OrdinalIgnoreCase);
        if (page != ControlPageId.Performance || !values.Remove("advancedCpuTuning", out var advanced)) return values;
        if (advanced.Intent == SettingIntent.Official)
        {
            foreach (var field in new[] { "advancedCpuTuning", "pptWatts", "mp1TemperatureC", "tdcCurrentMilliamps", "edcCurrentMilliamps", "pboScalar", "negativeCurveOptimizer" })
                values.TryAdd(field, SettingRequest.Official);
        }
        if (advanced.Intent != SettingIntent.Set) return values;
        var plan = advanced.Value!.Value.Deserialize<AdvancedCpuTuningPlan>(Json) ?? throw new InvalidDataException("advancedPlanMissing");
        if (ResidualSmu(plan) is { } smu) values.Add("advancedCpuTuning", SettingRequest.Set(smu));
        void Add(string field, object? value)
        {
            if (value is null) return;
            var setting = SettingRequest.Set(value);
            if (values.TryGetValue(field, out var prior) && prior.Intent == SettingIntent.Set && !Same(prior, setting))
                throw new InvalidDataException("conflictingAdvancedField:" + field);
            values[field] = setting;
        }
        Add("pptWatts", plan.PptWatts);
        Add("mp1TemperatureC", plan.Mp1TemperatureC);
        Add("tdcCurrentMilliamps", plan.TdcCurrentMilliamps);
        Add("edcCurrentMilliamps", plan.EdcCurrentMilliamps);
        Add("pboScalar", plan.PboScalar);
        Add("negativeCurveOptimizer", plan.CurveOptimizerAll);
        // Older readback snapshots retain both representations of the same all-core target.
        if (plan.CurveOptimizerAll is not { } all || plan.PerCoreCurveOptimizer is { Count: > 0 } cores &&
            cores.Values.Any(value => value != all))
            Add("negativeCurveOptimizer", plan.PerCoreCurveOptimizer);
        return values;
    }

    internal static Dictionary<string, SettingRequest> Read(ControlPageId page, HomeControlState state)
    {
        var result = new Dictionary<string, SettingRequest>(StringComparer.OrdinalIgnoreCase);
        void Add(string name, object? value) { if (value is not null) result[name] = new(SettingIntent.Set, JsonSerializer.SerializeToElement(value, Json)); }
        if (page == ControlPageId.Performance && state.CpuTuning is { } cpu)
        {
            // The provider exposes MP1 temperature through both UI representations; own it once.
            if (cpu.AdvancedLimits?.Mp1TemperatureC is null) Add("temperatureLimitC", cpu.TemperatureLimitC);
            Add("splWatts", cpu.SplWatts); Add("spptWatts", cpu.SpptWatts);
            Add("acMaxFrequencyMhz", cpu.AcFrequency()); Add("dcMaxFrequencyMhz", cpu.DcFrequency()); Add("isBoostEnabled", cpu.BoostEnabled);
            Add("windowsPowerSchemeId", cpu.WindowsPowerSchemeId); Add("acMinActiveCoresPercent", cpu.AcMinActiveCoresPercent);
            Add("dcMinActiveCoresPercent", cpu.DcMinActiveCoresPercent); Add("negativeCurveOptimizer", cpu.NegativeCurveOptimizer);
            Add("pptWatts", cpu.AdvancedLimits?.PptWatts); Add("mp1TemperatureC", cpu.AdvancedLimits?.Mp1TemperatureC); Add("tdcCurrentMilliamps", cpu.AdvancedLimits?.TdcCurrentMilliamps); Add("edcCurrentMilliamps", cpu.AdvancedLimits?.EdcCurrentMilliamps);
            Add("advancedCpuTuning", ResidualSmu(cpu.AdvancedLimits));
            Add("pboScalar", cpu.PboScalar);
            if (cpu.NegativeCurveOptimizer is null && cpu.HasCompleteCurveValues()) Add("negativeCurveOptimizer", cpu.PerCoreCurveOptimizer);
        }
        if (page == ControlPageId.Gpu)
        {
            // Driver-submitted ceilings are not independent readback evidence.
            Add("coreOffsetKhz", state.GpuVf?.CoreOffsetKhz); Add("memoryOffsetKhz", state.GpuVf?.MemoryOffsetKhz);
            if (state.GpuVf is { Nodes.Length: 127 } vf) Add("vfOffsetsKhz", vf.Nodes.Select(node => node.OffsetKhz).ToArray());
        }
        if (page == ControlPageId.Fan)
        {
            Add("strongCooling", state.StrongCooling);
            if (state.ActiveFanControlPlan is { } fan)
            { Add("strategy", fan.Strategy); Add("maximumRpm", fan.MaximumRpm); Add("fixedRpm", fan.FixedRpm);
              Add("isShared", fan.GpuPoints is null);
              Add("cpu", fan.Points.Select(point => new { temperature = point.TemperatureC, targetPercent = point.Percent }).ToArray());
              if (fan.GpuPoints is null) Add("shared", fan.Points.Select(point => new { temperature = point.TemperatureC, targetPercent = point.Percent }).ToArray());
              if (fan.GpuPoints is not null) Add("gpu", fan.GpuPoints.Select(point => new { temperature = point.TemperatureC, targetPercent = point.Percent }).ToArray()); }
            else Add("strategy", "Auto");
        }
        if (page == ControlPageId.Lighting && state.KeyboardLighting is { } light)
        { var canonicalLight = CanonicalLighting(light);
          Add("effect", canonicalLight.Effect); Add("brightness", light.BrightnessLevel); Add("speed", canonicalLight.Speed);
          if (light.Red is not null) Add("hex", $"#{light.Red:X2}{light.Green:X2}{light.Blue:X2}"); Add("logo", light.LogoEnabled); }
        return result;
    }

    internal static HardwareCommand[] Commands(ControlPageId page, Dictionary<string, SettingRequest> values, HomeControlState current)
    {
        T? Value<T>(string field) => values.TryGetValue(field, out var value) ? value.Value!.Value.Deserialize<T>(Json) : default;
        int? Number(string field) => values.TryGetValue(field, out var value) ? value.Value!.Value.GetInt32() : null;
        bool? Boolean(string field) => values.TryGetValue(field, out var value) ? value.Value!.Value.GetBoolean() : null;
        var commands = new List<HardwareCommand>();
        if (values.Count == 0) return [];
        if (page == ControlPageId.Performance)
        {
            var advanced = Value<AdvancedCpuTuningPlan>("advancedCpuTuning") ?? new();
            bool perCore = values.GetValueOrDefault("negativeCurveOptimizer")?.Value?.ValueKind == JsonValueKind.Object;
            Dictionary<int, int>? cores = null;
            if (perCore)
            {
                var baseline = current.CpuTuning?.HasCompleteCurveValues() == true ? current.CpuTuning.PerCoreCurveOptimizer!
                    : throw new InvalidDataException("curveOptimizerBaselineUnreadable");
                cores = new();
                foreach (var pair in Value<Dictionary<int, int>>("negativeCurveOptimizer")!)
                {
                    if (!baseline.ContainsKey(pair.Key) || pair.Value is < -50 or > 0) throw new InvalidDataException("perCoreTopologyOrValueInvalid");
                    cores[pair.Key] = pair.Value;
                }
            }
            double? pptWatts = values.TryGetValue("pptWatts", out var ppt) ? ppt.Value!.Value.GetDouble() : advanced.PptWatts;
            int? mp1Temperature = Number("mp1TemperatureC") ?? advanced.Mp1TemperatureC;
            int? tdcCurrent = Number("tdcCurrentMilliamps") ?? advanced.TdcCurrentMilliamps;
            int? edcCurrent = Number("edcCurrentMilliamps") ?? advanced.EdcCurrentMilliamps;
            advanced = advanced with { PptWatts = null, Mp1TemperatureC = null, TdcCurrentMilliamps = null, EdcCurrentMilliamps = null,
                PboScalar = Number("pboScalar"), PerCoreCurveOptimizer = cores };
            CpuBoostPolicy? boostPolicy = null;
            if (values.GetValueOrDefault("isBoostEnabled")?.Value is { ValueKind: JsonValueKind.Object } rawBoost &&
                !CpuBoostPolicy.TryRead(rawBoost, out boostPolicy)) throw new InvalidDataException("boostPolicyInvalid");
            var plan = new CpuTuningPlan(Number("temperatureLimitC"), Number("splWatts"), Number("spptWatts"), null,
                boostPolicy is null ? Boolean("isBoostEnabled") : null, null, values.ContainsKey("windowsPowerSchemeId") ? Value<Guid>("windowsPowerSchemeId") : null,
                perCore ? null : Number("negativeCurveOptimizer"))
            { AcMaxFrequencyMhz = Number("acMaxFrequencyMhz") ?? (values.ContainsKey("dcMaxFrequencyMhz") ? current.CpuTuning?.AcFrequency() ?? throw new InvalidDataException("acFrequencyUnreadable") : null),
              DcMaxFrequencyMhz = Number("dcMaxFrequencyMhz") ?? (values.ContainsKey("acMaxFrequencyMhz") ? current.CpuTuning?.DcFrequency() ?? throw new InvalidDataException("dcFrequencyUnreadable") : null),
              AcMinActiveCoresPercent = Number("acMinActiveCoresPercent") ?? (values.ContainsKey("dcMinActiveCoresPercent") ? current.CpuTuning?.AcMinActiveCoresPercent ?? throw new InvalidDataException("acParkingUnreadable") : null),
              DcMinActiveCoresPercent = Number("dcMinActiveCoresPercent") ?? (values.ContainsKey("acMinActiveCoresPercent") ? current.CpuTuning?.DcMinActiveCoresPercent ?? throw new InvalidDataException("dcParkingUnreadable") : null),
              BoostPolicy = boostPolicy, Advanced = advanced == new AdvancedCpuTuningPlan() ? null : advanced };
            var plans = new List<CpuTuningPlan>();
            if (plan != new CpuTuningPlan(null, null, null, null, null, null, null, null)) plans.Add(plan);
            if (pptWatts is double watts) plans.Add(new(null, null, null, null, null, null, null, null) { Advanced = new(PptWatts: watts) });
            if (mp1Temperature is int temperature) plans.Add(new(null, null, null, null, null, null, null, null) { Advanced = new(Mp1TemperatureC: temperature) });
            if (tdcCurrent is int milliamps) plans.Add(new(null, null, null, null, null, null, null, null) { Advanced = new(TdcCurrentMilliamps: milliamps) });
            if (edcCurrent is int edcMilliamps) plans.Add(new(null, null, null, null, null, null, null, null) { Advanced = new(EdcCurrentMilliamps: edcMilliamps) });
            if (plans.Count > 0) commands.Add(new SetCpuTuningBatchCommand(Guid.NewGuid(), plans.ToArray(), true));
        }
        if (page == ControlPageId.Gpu)
        {
            int[]? originalCurve = null;
            int[]? expectedCurve = null;
            if (values.ContainsKey("coreOffsetKhz") || values.ContainsKey("vfOffsetsKhz"))
            {
                if (current.GpuVf is not { Error: null, Nodes.Length: 127 } vf)
                    throw new InvalidDataException("vfBaselineUnreadable");
                originalCurve = vf.Nodes.Select(node => node.OffsetKhz).ToArray();
                if (originalCurve[0] != 0 || originalCurve.Skip(1).Any(value => value is < -200000 or > 200000))
                    throw new InvalidDataException("vfBaselineUnreadable");
                int delta = values.ContainsKey("coreOffsetKhz")
                    ? checked(Number("coreOffsetKhz")!.Value - (vf.CoreOffsetKhz ?? throw new InvalidDataException("coreBaselineUnreadable"))) : 0;
                // P0 core writes shift the table; memory writes preserve it. Do not rebase on unrelated external edits.
                expectedCurve = originalCurve.Select((value, index) => index == 0 ? value : checked(value + delta)).ToArray();
                if (expectedCurve.Skip(1).Any(value => value is < -200000 or > 200000))
                    throw new InvalidDataException("gpuIntermediateCurveOutOfRange");
            }
            if (values.ContainsKey("coreFrequencyLimitMhz")) commands.Add(new SetGpuFrequencyLimitCommand(Guid.NewGuid(), Number("coreFrequencyLimitMhz"), true));
            if (values.ContainsKey("coreOffsetKhz")) commands.Add(new SetGpuCoreOffsetCommand(Guid.NewGuid(), current.GpuVf?.CoreOffsetKhz ?? throw new InvalidDataException("coreBaselineUnreadable"), Number("coreOffsetKhz")!.Value, true)
                { ExpectedVfOffsetsKhz = originalCurve });
            if (values.ContainsKey("memoryOffsetKhz")) commands.Add(new SetGpuMemoryOffsetCommand(Guid.NewGuid(), current.GpuVf?.MemoryOffsetKhz ?? throw new InvalidDataException("memoryBaselineUnreadable"), Number("memoryOffsetKhz")!.Value, true));
            if (values.ContainsKey("vfOffsetsKhz")) commands.Add(new SetGpuVfCurveCommand(Guid.NewGuid(), expectedCurve!, Value<int[]>("vfOffsetsKhz")!, true));
        }
        if (page == ControlPageId.Fan)
        {
            if (Boolean("strongCooling") == true && values.Keys.Any(field => field != "strongCooling"))
                throw new InvalidDataException("strongCoolingConflictsWithFanPlan");
            if (Boolean("strongCooling") is bool cooling) commands.Add(new SetStrongCoolingCommand(Guid.NewGuid(), cooling));
            var planValues = values.Keys.Any(field => field != "strongCooling");
            if (planValues)
            {
                string strategy = Value<string>("strategy") ?? current.ActiveFanControlPlan?.Strategy ?? "Auto";
                FanPoint[]? Points(string field, FanPoint[]? fallback) => values.TryGetValue(field, out var value) ? value.Value!.Value.EnumerateArray()
                    .Select(point => new FanPoint(point.GetProperty("temperature").GetInt32(), point.GetProperty("targetPercent").GetInt32())).ToArray() : fallback;
                bool shared = Boolean("isShared") ?? (values.ContainsKey("shared") || current.ActiveFanControlPlan?.GpuPoints is null);
                if (strategy == "Auto" && Number("maximumRpm") is null) commands.Add(new ReleaseFanControlCommand(Guid.NewGuid(), ReleaseReason.UserRequested));
                else commands.Add(new SetFanControlCommand(Guid.NewGuid(), new(Points(shared ? "shared" : "cpu", current.ActiveFanControlPlan?.Points) ?? [])
                { Strategy = strategy, GpuPoints = shared ? null : Points("gpu", current.ActiveFanControlPlan?.GpuPoints), FixedRpm = Number("fixedRpm") ?? current.ActiveFanControlPlan?.FixedRpm,
                  MaximumRpm = Number("maximumRpm") ?? current.ActiveFanControlPlan?.MaximumRpm }, true));
            }
        }
        if (page == ControlPageId.Lighting)
        {
            var before = current.KeyboardLighting ?? throw new InvalidDataException("lightingBaselineUnreadable");
            int level = Number("brightness") ?? before.BrightnessLevel ?? throw new InvalidDataException("lightingLevelUnreadable");
            string? hex = Value<string>("hex");
            var plan = before with { Effect = Value<string>("effect") ?? before.Effect, BrightnessLevel = level,
                Brightness = (int)Math.Round(level * 100d / 3), Speed = values.ContainsKey("speed") ? Value<double>("speed") : before.Speed,
                LogoEnabled = Boolean("logo") ?? before.LogoEnabled,
                Red = hex is null ? before.Red : Convert.ToByte(hex.Substring(1, 2), 16),
                Green = hex is null ? before.Green : Convert.ToByte(hex.Substring(3, 2), 16),
                Blue = hex is null ? before.Blue : Convert.ToByte(hex.Substring(5, 2), 16) };
            commands.Add(new SetKeyboardLightingCommand(Guid.NewGuid(), plan));
        }
        if (commands.Any(command => CommandValidation.Validate(command) is not null)) throw new InvalidDataException("compiledCommandInvalid");
        return commands.ToArray();
    }

    // Static speed has no firmware state; gradient speed remains observable in the software effect.
    // Firmware reads Fixed; UI/presets use Static. The exact OEM level owns its legacy percentage.
    internal static KeyboardLightingPlan CanonicalLighting(KeyboardLightingPlan plan) => plan with
    {
        Effect = plan.Effect == "Fixed" ? "Static" : plan.Effect,
        Brightness = plan.BrightnessLevel is >= 0 and <= 3 ? (int)Math.Round(plan.BrightnessLevel.Value * 100d / 3) : plan.Brightness,
        Speed = (plan.Effect is "Static" or "Fixed") && plan.IsValid() ? 1 : plan.Speed
    };

    internal static bool Same(SettingRequest? left, SettingRequest? right) => left?.Intent == right?.Intent &&
        left?.Value?.GetRawText() == right?.Value?.GetRawText();
}
