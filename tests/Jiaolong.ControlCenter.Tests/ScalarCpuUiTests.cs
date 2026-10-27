using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.ViewModels;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class ScalarCpuUiTests
{
    [TestMethod]
    public void Direct_rows_use_independent_capabilities_without_oem_or_residual_targets()
    {
        var state = State();
        var prepared = Prepare(new() { PptWatts = 60.125, Mp1TemperatureC = 90 }, state);
        Assert.IsNotNull(prepared);
        var plan = Plan(prepared!, state, Capabilities());
        Assert.IsNull(plan.Error);
        Assert.AreEqual(2, plan.Steps.Count);
        Assert.AreEqual(60.125, plan.Steps.Single(step => step.Label == "PPT").Command.Plan.Advanced!.PptWatts);
        Assert.AreEqual(90, plan.Steps.Single(step => step.Label == "MP1 温度").Command.Plan.Advanced!.Mp1TemperatureC);
        foreach (var step in plan.Steps)
        {
            var command = step.Command.Plan;
            Assert.IsNull(command.TemperatureLimitC);
            Assert.IsNull(command.SpptWatts);
            Assert.IsNull(command.SplWatts);
            Assert.IsNull(command.NegativeCurveOptimizer);
            Assert.IsNull(command.Advanced!.FastPptWatts);
            Assert.IsNull(command.Advanced.RsmuTemperatureC);
        }
        foreach (double watts in new[] { 45d, 75.001, 119.999, 120d })
        {
            var expanded = Prepare(new() { PptWatts = watts }, state)!;
            var result = Plan(expanded, state, Capabilities());
            Assert.IsNull(result.Error);
            Assert.AreEqual(watts, result.Steps.Single().Command.Plan.Advanced!.PptWatts);
        }
        foreach (bool ppt in new[] { true, false })
        {
            var single = Prepare(ppt ? new() { PptWatts = 60.125 } : new() { Mp1TemperatureC = 90 }, state)!;
            var caps = Capabilities();
            caps = caps with { Fields = caps.Fields.Select(field => field.Key == (ppt ? "mp1TemperatureC" : "pptWatts")
                ? field with { Writable = false } : field).ToArray() };
            Assert.IsNull(Plan(single, state, caps).Error);
        }
    }

    [TestMethod]
    public void Direct_rows_reject_missing_stale_unverified_or_out_of_range_authority()
    {
        var state = State();
        foreach (string key in new[] { "pptWatts", "mp1TemperatureC" })
        {
            var prepared = Prepare(key == "pptWatts" ? new() { PptWatts = 60.125 } : new() { Mp1TemperatureC = 90 }, state)!;
            var caps = Capabilities();
            var field = caps.Fields.Single(item => item.Key == key);
            var invalid = new FieldCapability[]
            {
                field with { Writable = false }, field with { Readable = false },
                field with { VerifiedAtUtc = null }, field with { Source = "uiSuggestion" },
                field with { Availability = CapabilityState.Unavailable }, field with { HardwareEpoch = 0 },
                field with { HardwareEpoch = 8 }, field with { Maximum = 50 }
            };
            foreach (var bad in invalid)
            {
                var rejected = Plan(prepared, state, caps with { Fields = caps.Fields.Select(item => item.Key == key ? bad : item).ToArray() }, smu: true);
                Assert.IsNotNull(rejected.Error, key + ": " + bad);
                Assert.AreEqual(0, rejected.Steps.Count);
            }
            foreach (var missing in new CapabilitySnapshot?[] { null, caps with { Fields = caps.Fields.Where(item => item.Key != key).ToArray() } })
                Assert.IsNotNull(Plan(prepared, state, missing, smu: true).Error);
            Assert.IsNotNull(Plan(prepared, state, caps, epoch: 8).Error);
        }
        Assert.IsNull(Prepare(new() { PptWatts = 60 }, state with { AdvancedLimits = new(Mp1TemperatureC: 99) }));
        Assert.IsNull(Prepare(new() { Mp1TemperatureC = 90 }, state with { AdvancedLimits = new(PptWatts: 120.125) }));
        foreach (double watts in new[] { 44.999, 120.001 })
            Assert.IsNotNull(Plan(Prepare(new() { PptWatts = watts }, state)!, state, Capabilities()).Error);
        var offStep = Prepare(new() { PptWatts = 60.1255 }, state)!;
        Assert.IsNotNull(Plan(offStep, state, Capabilities()).Error);
    }

    [TestMethod]
    public void Direct_authority_never_unlocks_residual_and_keep_or_unchanged_adds_no_targets()
    {
        var state = State();
        var mixed = Prepare(new() { PptWatts = 60.125, FastPptWatts = 60 }, state)!;
        var rejected = Plan(mixed, state, Capabilities());
        Assert.IsNotNull(rejected.Error);
        Assert.AreEqual(0, rejected.Steps.Count);
        var unchanged = Prepare(new() { PptWatts = 120.125, Mp1TemperatureC = 99 }, state)!;
        var noOp = Plan(unchanged, state, Capabilities());
        Assert.IsNull(noOp.Error);
        Assert.AreEqual(0, noOp.Steps.Count);
        var keep = unchanged with { AdvancedCpuTuning = new() { PptWatts = 60.125 },
            SettingRequests = new Dictionary<string, SettingRequest> { ["advancedCpuTuning"] = SettingRequest.Keep } };
        Assert.AreEqual(0, Plan(keep, state, Capabilities()).Steps.Count);
    }

    [TestMethod]
    public void Tdc_scalar_uses_independent_authority_and_never_emits_vrm_or_oem_fields()
    {
        var state = State() with
        {
            AdvancedLimits = State().AdvancedLimits! with { TdcCurrentMilliamps = 108_000 }
        };
        var capabilities = Capabilities() with
        {
            Fields = [.. Capabilities().Fields,
                new("tdcCurrentMilliamps", CapabilityState.Available, true, true, 1_000, 200_000, 1,
                    "mA", "hardwareReadback", "PreservesSelectedMode", DateTimeOffset.UtcNow, 7)]
        };
        var prepared = Prepare(new() { TdcCurrentMilliamps = 107_999 }, state);
        Assert.IsNotNull(prepared);
        var plan = Plan(prepared!, state, capabilities);
        Assert.IsNull(plan.Error);
        var step = plan.Steps.Single();
        var command = step.Command.Plan;
        Assert.AreEqual(107_999, command.Advanced!.TdcCurrentMilliamps);
        Assert.IsNull(command.TemperatureLimitC);
        Assert.IsNull(command.SplWatts);
        Assert.IsNull(command.SpptWatts);
        Assert.IsNull(command.NegativeCurveOptimizer);
        Assert.IsNull(command.Advanced.PptWatts);
        Assert.IsNull(command.Advanced.VrmCurrentMilliamps);
        Assert.IsNull(command.Advanced.EdcCurrentMilliamps);
        Assert.IsNull(command.Advanced.Mp1TemperatureC);
        Assert.IsNull(command.Advanced.RsmuTemperatureC);
        Assert.IsNull(command.Advanced.PboScalar);
        Assert.IsNull(command.Advanced.CurveOptimizerAll);
        Assert.IsNull(command.Advanced.PerCoreCurveOptimizer);

        var unchanged = Prepare(new() { TdcCurrentMilliamps = 108_000 }, state)!;
        Assert.AreEqual(0, Plan(unchanged, state, capabilities).Steps.Count);

        foreach (var bad in new[]
        {
            capabilities with { Fields = capabilities.Fields.Where(field => field.Key != "tdcCurrentMilliamps").ToArray() },
            capabilities with { Fields = capabilities.Fields.Select(field => field.Key == "tdcCurrentMilliamps" ? field with { HardwareEpoch = 6 } : field).ToArray() },
            capabilities with { Fields = capabilities.Fields.Select(field => field.Key == "tdcCurrentMilliamps" ? field with { VerifiedAtUtc = null } : field).ToArray() },
            capabilities with { Fields = capabilities.Fields.Select(field => field.Key == "tdcCurrentMilliamps" ? field with { Writable = false } : field).ToArray() }
        })
        {
            var rejected = Plan(prepared!, state, bad, smu: true);
            Assert.IsNotNull(rejected.Error);
            Assert.AreEqual(0, rejected.Steps.Count);
        }
        Assert.IsNotNull(Plan(prepared!, state, capabilities, smu: true, epoch: 8).Error);

        var residualState = state with
        {
            AdvancedLimits = state.AdvancedLimits! with { EdcCurrentMilliamps = 160_000, VrmCurrentMilliamps = 95_000 }
        };
        foreach (var residual in new[]
        {
            new AdvancedCpuTuningDraft { TdcCurrentMilliamps = 107_999, EdcCurrentMilliamps = 90_000 },
            new AdvancedCpuTuningDraft { TdcCurrentMilliamps = 107_999, VrmCurrentMilliamps = 90_000 }
        })
        {
            var residualDraft = Prepare(residual, residualState);
            Assert.IsNotNull(residualDraft);
            var rejected = Plan(residualDraft!, residualState, capabilities);
            Assert.IsNotNull(rejected.Error);
            Assert.AreEqual(0, rejected.Steps.Count);
        }
    }

    [TestMethod]
    public void Edc_scalar_uses_independent_authority_and_never_emits_vrm_or_oem_fields()
    {
        var state = State() with
        {
            AdvancedLimits = State().AdvancedLimits! with { EdcCurrentMilliamps = 160_000 }
        };
        var capabilities = Capabilities() with
        {
            Fields = [.. Capabilities().Fields,
                new("edcCurrentMilliamps", CapabilityState.Available, true, true, 1_000, 200_000, 1,
                    "mA", "hardwareReadback", "PreservesSelectedMode", DateTimeOffset.UtcNow, 7)]
        };
        var prepared = Prepare(new() { EdcCurrentMilliamps = 159_999 }, state);
        Assert.IsNotNull(prepared);
        var plan = Plan(prepared!, state, capabilities);
        Assert.IsNull(plan.Error);
        var step = plan.Steps.Single();
        var command = step.Command.Plan;
        Assert.AreEqual(159_999, command.Advanced!.EdcCurrentMilliamps);
        Assert.IsNull(command.TemperatureLimitC);
        Assert.IsNull(command.SplWatts);
        Assert.IsNull(command.SpptWatts);
        Assert.IsNull(command.NegativeCurveOptimizer);
        Assert.IsNull(command.Advanced.PptWatts);
        Assert.IsNull(command.Advanced.VrmCurrentMilliamps);
        Assert.IsNull(command.Advanced.TdcCurrentMilliamps);
        Assert.IsNull(command.Advanced.Mp1TemperatureC);
        Assert.IsNull(command.Advanced.RsmuTemperatureC);
        Assert.IsNull(command.Advanced.PboScalar);
        Assert.IsNull(command.Advanced.CurveOptimizerAll);
        Assert.IsNull(command.Advanced.PerCoreCurveOptimizer);

        var unchanged = Prepare(new() { EdcCurrentMilliamps = 160_000 }, state)!;
        Assert.AreEqual(0, Plan(unchanged, state, capabilities).Steps.Count);

        foreach (var bad in new[]
        {
            capabilities with { Fields = capabilities.Fields.Where(field => field.Key != "edcCurrentMilliamps").ToArray() },
            capabilities with { Fields = capabilities.Fields.Select(field => field.Key == "edcCurrentMilliamps" ? field with { HardwareEpoch = 6 } : field).ToArray() },
            capabilities with { Fields = capabilities.Fields.Select(field => field.Key == "edcCurrentMilliamps" ? field with { VerifiedAtUtc = null } : field).ToArray() },
            capabilities with { Fields = capabilities.Fields.Select(field => field.Key == "edcCurrentMilliamps" ? field with { Writable = false } : field).ToArray() }
        })
        {
            var rejected = Plan(prepared!, state, bad, smu: true);
            Assert.IsNotNull(rejected.Error);
            Assert.AreEqual(0, rejected.Steps.Count);
        }
        Assert.IsNotNull(Plan(prepared!, state, capabilities, smu: true, epoch: 8).Error);

        var residualState = state with
        {
            AdvancedLimits = state.AdvancedLimits! with { TdcCurrentMilliamps = 108_000, VrmCurrentMilliamps = 95_000 }
        };
        foreach (var residual in new[]
        {
            new AdvancedCpuTuningDraft { EdcCurrentMilliamps = 159_999, TdcCurrentMilliamps = 90_000 },
            new AdvancedCpuTuningDraft { EdcCurrentMilliamps = 159_999, VrmCurrentMilliamps = 90_000 }
        })
        {
            var residualDraft = Prepare(residual, residualState);
            Assert.IsNotNull(residualDraft);
            var rejected = Plan(residualDraft!, residualState, capabilities);
            Assert.IsNotNull(rejected.Error);
            Assert.AreEqual(0, rejected.Steps.Count);
        }
    }

    private static CpuTuningState State() => new(99, null, null, null, null, 8, null, null, "hardwareReadback")
    { AdvancedLimits = new(PptWatts: 120.125, FastPptWatts: 108, Mp1TemperatureC: 99) };

    private static PerformanceDraft? Prepare(AdvancedCpuTuningDraft advanced, CpuTuningState state) =>
        PerformanceLiveEdit.Prepare(new() { NegativeCurveOptimizer = null, AdvancedCpuTuning = advanced }, state,
            new HashSet<string> { "advancedCpuTuning" });

    private static CapabilitySnapshot Capabilities() => new([])
    {
        Fields = [
            new("pptWatts", CapabilityState.Available, true, true, 45, 120, .001, "W", "hardwareReadback", "PreservesSelectedMode", DateTimeOffset.UtcNow, 7),
            new("mp1TemperatureC", CapabilityState.Available, true, true, 40, 100, 1, "°C", "hardwareReadback", "PreservesSelectedMode", DateTimeOffset.UtcNow, 7),
            new("advancedCpuTuning", CapabilityState.Available, true, false, null, null, null, "", "hardwareReadback", "Unknown", null, 7)
        ]
    };

    private static PerformancePresetCommandPlan Plan(PerformanceDraft draft, CpuTuningState state,
        CapabilitySnapshot? capabilities, bool smu = false, long epoch = 7) =>
        PerformanceLiveEdit.CreatePlan(draft, state, cpu: true, smu: smu, pbo: true, curve: true,
            capabilities: capabilities, hardwareEpoch: epoch);
}
