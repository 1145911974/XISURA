using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.ViewModels;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class PerformanceLiveEditTests
{
    [TestMethod]
    public void Unknown_spl_does_not_block_a_per_core_co_edit_or_add_unedited_targets()
    {
        var state = State(spl: null, sppt: null);
        var draft = new PerformanceDraft
        {
            NegativeCurveOptimizer = null,
            AdvancedCpuTuning = new AdvancedCpuTuningDraft
            {
                CurveOptimizerMode = CpuCurveOptimizerMode.PerCore,
                PerCoreCurveOptimizer = new Dictionary<int, int> { [7] = -49 },
                FastPptWatts = 108
            }
        };

        var prepared = PerformanceLiveEdit.Prepare(draft, state, Edits("advancedCpuTuning"));
        Assert.IsNotNull(prepared);
        var plan = Plan(prepared!, state);

        Assert.IsNull(plan.Error);
        Assert.AreEqual(1, plan.Steps.Count);
        var command = plan.Steps.Single().Command.Plan;
        Assert.IsNull(command.TemperatureLimitC);
        Assert.IsNull(command.SplWatts);
        Assert.IsNull(command.SpptWatts);
        Assert.IsNull(command.AcMaxFrequencyMhz);
        Assert.IsNull(command.DcMaxFrequencyMhz);
        Assert.IsNull(command.Advanced?.FastPptWatts);
        Assert.IsNull(command.Advanced?.PboScalar);
        Assert.IsNull(command.Advanced?.CurveOptimizerAll);
        Assert.AreEqual(-49, command.Advanced?.PerCoreCurveOptimizer![7]);
        Assert.AreEqual(1, command.Advanced?.PerCoreCurveOptimizer!.Count);
        Assert.IsNull(PerformanceLiveEdit.Prepare(draft,
            state with { CurveOptimizerVerification = null }, Edits("advancedCpuTuning")));
    }

    [TestMethod]
    public void Ac_frequency_edit_preserves_dc_accepts_unlimited_and_rejects_unknown_or_out_of_range()
    {
        var state = State();
        var acEdit = PerformanceLiveEdit.Prepare(
            new PerformanceDraft { AcMaxFrequencyMhz = 4600, DcMaxFrequencyMhz = 4700, NegativeCurveOptimizer = null },
            state, Edits("acMaxFrequencyMhz"));
        Assert.IsNotNull(acEdit);
        var plan = Plan(acEdit!, state);
        Assert.IsNull(plan.Error);
        var pair = plan.Steps.Single().Command.Plan;
        Assert.AreEqual(4600, pair.AcMaxFrequencyMhz);
        Assert.AreEqual(4700, pair.DcMaxFrequencyMhz);

        var unlimited = PerformanceLiveEdit.Prepare(
            new PerformanceDraft { AcMaxFrequencyMhz = 0, DcMaxFrequencyMhz = 4700, NegativeCurveOptimizer = null },
            state, Edits("acMaxFrequencyMhz"));
        Assert.IsNotNull(unlimited);
        Assert.AreEqual(0, Plan(unlimited!, state).Steps.Single().Command.Plan.AcMaxFrequencyMhz);

        var unreadableDc = state with { MaxFrequencyMhz = null, DcMaxFrequencyMhz = null };
        Assert.IsNull(PerformanceLiveEdit.Prepare(new PerformanceDraft { AcMaxFrequencyMhz = 4600 },
            unreadableDc, Edits("acMaxFrequencyMhz")));
        Assert.IsNull(PerformanceLiveEdit.Prepare(
            new PerformanceDraft { DcMaxFrequencyMhz = 4500, NegativeCurveOptimizer = null },
            unreadableDc, Edits("dcMaxFrequencyMhz")));

        var outOfRange = PerformanceLiveEdit.Prepare(
            new PerformanceDraft { AcMaxFrequencyMhz = 1499, NegativeCurveOptimizer = null },
            state, Edits("acMaxFrequencyMhz"));
        Assert.IsNotNull(outOfRange);
        var rejected = Plan(outOfRange!, state);
        Assert.IsNotNull(rejected.Error);
        Assert.AreEqual(0, rejected.Steps.Count);
    }

    [TestMethod]
    public void Only_explicitly_edited_fields_project_and_unreadable_edits_are_refused()
    {
        var state = State();
        var draft = new PerformanceDraft { AcMaxFrequencyMhz = 4600 };

        Assert.IsNull(PerformanceLiveEdit.Prepare(draft, state, Edits()));
        Assert.IsNull(PerformanceLiveEdit.Prepare(
            new PerformanceDraft { SplWatts = 60 }, State(spl: null), Edits("splWatts")));

        var prepared = PerformanceLiveEdit.Prepare(draft, state, Edits("acMaxFrequencyMhz"));
        Assert.IsNotNull(prepared);
        var plan = Plan(prepared!, state);
        Assert.IsNull(plan.Error);
        Assert.AreEqual(1, plan.Steps.Count);
        Assert.IsNull(plan.Steps[0].Command.Plan.SplWatts);
        Assert.IsNull(plan.Steps[0].Command.Plan.SpptWatts);
        Assert.AreEqual(CpuCurveOptimizerMode.AllCore,
            PerformanceLiveEdit.ResolveCurveMode(prepared!, CpuCurveOptimizerMode.AllCore));
        Assert.AreEqual(CpuCurveOptimizerMode.PerCore,
            PerformanceLiveEdit.ResolveCurveMode(prepared!, CpuCurveOptimizerMode.PerCore));
        Assert.IsNull(plan.Steps[0].Command.Plan.NegativeCurveOptimizer);
        Assert.IsNull(plan.Steps[0].Command.Plan.Advanced);
    }

    [TestMethod]
    public void Fast_ppt_can_be_edited_alone_but_explicit_conflicting_sppt_is_rejected()
    {
        var state = State(sppt: 65);
        var draft = new PerformanceDraft
        {
            SpptWatts = 65,
            NegativeCurveOptimizer = null,
            AdvancedCpuTuning = new AdvancedCpuTuningDraft { FastPptWatts = 60 }
        };
        var fastOnly = PerformanceLiveEdit.Prepare(draft, state, Edits("advancedCpuTuning"));
        Assert.IsNotNull(fastOnly);
        var aggregate = new FieldCapability("advancedCpuTuning", CapabilityState.Available, true, true,
            null, null, null, "", "hardwareReadback", "PreservesSelectedMode", DateTimeOffset.UtcNow, 7);
        var capabilities = new CapabilitySnapshot([]) { Fields = [aggregate] };
        PerformancePresetCommandPlan Create(CapabilitySnapshot? caps, bool smu = true) =>
            PerformanceLiveEdit.CreatePlan(fastOnly!, state, true, smu, true, true, caps, 7);
        var fastPlan = Create(capabilities);
        Assert.IsNull(fastPlan.Error);
        Assert.AreEqual(1, fastPlan.Steps.Count);
        Assert.AreEqual(60, fastPlan.Steps[0].Command.Plan.Advanced?.FastPptWatts);
        Assert.IsNull(fastPlan.Steps[0].Command.Plan.SpptWatts);

        foreach (var denied in new CapabilitySnapshot?[] { null, capabilities with { Fields = [] },
            capabilities with { Fields = [aggregate with { Writable = false }] },
            capabilities with { Fields = [aggregate with { Readable = false }] },
            capabilities with { Fields = [aggregate with { VerifiedAtUtc = null }] },
            capabilities with { Fields = [aggregate with { Source = "uiSuggestion" }] },
            capabilities with { Fields = [aggregate with { Availability = CapabilityState.ReadOnly }] },
            capabilities with { Fields = [aggregate with { HardwareEpoch = 8 }] } })
        {
            var refused = Create(denied);
            Assert.IsNotNull(refused.Error);
            Assert.AreEqual(0, refused.Steps.Count);
        }
        Assert.IsNotNull(Create(capabilities, smu: false).Error);

        var bothSet = PerformanceLiveEdit.Prepare(
            draft, state, Edits("advancedCpuTuning", "spptWatts"));
        Assert.IsNotNull(bothSet);
        var conflict = Plan(bothSet!, state);
        Assert.IsNotNull(conflict.Error);
        Assert.AreEqual(0, conflict.Steps.Count);
    }

    [TestMethod]
    public void Full_and_live_out_of_range_values_still_fail_and_normalizing_keep_does_not_mutate_draft()
    {
        var state = State();
        var full = PerformanceCommandFactory.CreatePresetCommands(
            new PerformanceDraft { SplWatts = 106, NegativeCurveOptimizer = null },
            state, true, true, true, true);
        Assert.IsNotNull(full.Error);
        Assert.AreEqual(0, full.Steps.Count);

        var live = PerformanceLiveEdit.Prepare(
            new PerformanceDraft { SplWatts = 106, NegativeCurveOptimizer = null },
            state, Edits("splWatts"));
        Assert.IsNotNull(live);
        var livePlan = Plan(live!, state);
        Assert.IsNotNull(livePlan.Error);
        Assert.AreEqual(0, livePlan.Steps.Count);

        var keepDraft = new PerformanceDraft
        {
            SplWatts = 999,
            NegativeCurveOptimizer = null,
            SettingRequests = new Dictionary<string, SettingRequest>
            {
                ["splWatts"] = SettingRequest.Keep
            }
        };
        var normalized = PerformanceLiveEdit.Normalize(keepDraft, state);
        Assert.AreEqual(999, keepDraft.SplWatts);
        Assert.AreNotEqual(999, normalized.SplWatts);
        Assert.IsTrue(PerformanceDraftValidator.Validate(normalized).IsValid);
    }

    [TestMethod]
    public void Core_parking_requires_a_complete_target_pair_and_readable_baseline_pair()
    {
        var state = State(acParking: 100, dcParking: 17);
        var edits = Edits("acMinActiveCoresPercent", "dcMinActiveCoresPercent");
        var draft = new PerformanceDraft
        {
            AcMinActiveCoresPercent = 90,
            DcMinActiveCoresPercent = 17,
            NegativeCurveOptimizer = null
        };
        var prepared = PerformanceLiveEdit.Prepare(draft, state, edits);
        Assert.IsNotNull(prepared);
        var plan = Plan(prepared!, state);
        Assert.IsNull(plan.Error);
        var parking = plan.Steps.Single().Command.Plan;
        Assert.AreEqual(90, parking.AcMinActiveCoresPercent);
        Assert.AreEqual(17, parking.DcMinActiveCoresPercent);

        Assert.IsNull(PerformanceLiveEdit.Prepare(
            draft, state with { AcMinActiveCoresPercent = null }, edits));
        Assert.IsNull(PerformanceLiveEdit.Prepare(
            draft, state with { DcMinActiveCoresPercent = null }, edits));
        Assert.IsNull(PerformanceLiveEdit.Prepare(
            draft with { DcMinActiveCoresPercent = null }, state, edits));
    }

    private static IReadOnlySet<string> Edits(params string[] fields) =>
        new HashSet<string>(fields, StringComparer.Ordinal);

    private static CpuTuningState State(
        int? spl = 65,
        int? sppt = 65,
        int? acFrequency = 4700,
        int? dcFrequency = 4700,
        int? acParking = 100,
        int? dcParking = 17,
        double? fastPpt = 108) =>
        new(null, spl, sppt, null, null, 8, null, -50, "hardwareReadback")
        {
            AcMaxFrequencyMhz = acFrequency,
            DcMaxFrequencyMhz = dcFrequency,
            AcMinActiveCoresPercent = acParking,
            DcMinActiveCoresPercent = dcParking,
            PerCoreCurveOptimizer = Enumerable.Range(0, 8).ToDictionary(core => core, _ => -50),
            AdvancedLimits = new AdvancedCpuTuningPlan(FastPptWatts: fastPpt)
        };

    private static PerformancePresetCommandPlan Plan(PerformanceDraft draft, CpuTuningState state) =>
        PerformanceLiveEdit.CreatePlan(draft, state, cpu: true, smu: true, pbo: true, curve: true);
}
