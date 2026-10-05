using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.ViewModels;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class PerformanceViewModelTests
{
    [TestMethod]
    public void Mrid6_23_verified_ranges_reject_values_outside_observed_control_center_bounds()
    {
        Assert.IsFalse(PerformanceDraftValidator.Validate(new PerformanceDraft { TemperatureLimitC = 44 }).IsValid);
        Assert.IsFalse(PerformanceDraftValidator.Validate(new PerformanceDraft { TemperatureLimitC = 101 }).IsValid);
        Assert.IsFalse(PerformanceDraftValidator.Validate(new PerformanceDraft { SplWatts = 19 }).IsValid);
        Assert.IsFalse(PerformanceDraftValidator.Validate(new PerformanceDraft { SplWatts = 106 }).IsValid);
        Assert.IsFalse(PerformanceDraftValidator.Validate(new PerformanceDraft { SpptWatts = 19 }).IsValid);
        Assert.IsFalse(PerformanceDraftValidator.Validate(new PerformanceDraft { SpptWatts = 121 }).IsValid);
        Assert.IsFalse(PerformanceDraftValidator.Validate(new PerformanceDraft { MaxFrequencyMhz = 1499 }).IsValid);
        Assert.IsFalse(PerformanceDraftValidator.Validate(new PerformanceDraft { MaxFrequencyMhz = 5401 }).IsValid);
    }

    [TestMethod]
    public void Performance_view_model_exposes_six_editable_profiles()
    {
        var property = typeof(PerformanceViewModel).GetProperty("EditableProfiles");

        Assert.IsNotNull(property);
        var profiles = property!.GetValue(new PerformanceViewModel()) as System.Collections.IEnumerable;
        Assert.IsNotNull(profiles);
        Assert.AreEqual(6, profiles!.Cast<object>().Count());
    }

    [TestMethod]
    public void Curve_optimizer_is_a_draft_write_but_core_count_is_not()
    {
        Assert.IsNull(typeof(PerformanceDraft).GetProperty("EnabledCoreCount"));
        Assert.IsNotNull(typeof(PerformanceDraft).GetProperty("NegativeCurveOptimizer"));
    }

    [TestMethod]
    public async Task Editing_draft_does_not_send_a_command_until_apply()
    {
        var client = new RecordingControlCenterClient();
        var vm = PerformanceFixture.Create(client);

        vm.Draft.SplWatts += 1;
        Assert.AreEqual(0, client.SentCommands.Count);

        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.AreEqual(1, client.SentCommands.Count);
    }

    [TestMethod]
    public void Performance_command_factory_maps_every_supported_draft_field()
    {
        var schemeId = Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e");
        var draft = new PerformanceDraft
        {
            TemperatureLimitC = 82,
            SplWatts = 55,
            SpptWatts = 80,
            MaxFrequencyMhz = 4700,
            IsBoostEnabled = true,
            NegativeCurveOptimizer = -10,
            WindowsPowerSchemeId = schemeId
        };

        var command = PerformanceCommandFactory.Create(draft, riskConfirmed: true);

        Assert.AreEqual(draft.TemperatureLimitC, command.Plan.TemperatureLimitC);
        Assert.AreEqual(draft.SplWatts, command.Plan.SplWatts);
        Assert.AreEqual(draft.SpptWatts, command.Plan.SpptWatts);
        Assert.AreEqual(draft.MaxFrequencyMhz, command.Plan.MaxFrequencyMhz);
        Assert.AreEqual(draft.IsBoostEnabled, command.Plan.BoostEnabled);
        Assert.AreEqual(schemeId, command.Plan.WindowsPowerSchemeId);
        Assert.AreEqual(draft.NegativeCurveOptimizer, command.Plan.NegativeCurveOptimizer);
        Assert.IsTrue(command.RiskConfirmed);
    }

    [TestMethod]
    public void Performance_command_factory_preserves_distinct_ac_and_dc_frequencies()
    {
        var draft = new PerformanceDraft
        {
            AcMaxFrequencyMhz = 4_700,
            DcMaxFrequencyMhz = 2_200
        };

        var command = PerformanceCommandFactory.Create(draft, riskConfirmed: true);

        Assert.AreEqual(4_700, command.Plan.AcMaxFrequencyMhz);
        Assert.AreEqual(2_200, command.Plan.DcMaxFrequencyMhz);
        Assert.AreEqual(4_700, command.Plan.MaxFrequencyMhz);
    }

    [TestMethod]
    public void Readback_only_factory_skips_unreadable_cpu_limits_and_cached_curve_values()
    {
        Guid schemeId = Guid.NewGuid();
        var state = new CpuTuningState(null, null, null, 4_700, null, null, schemeId, -15, "cached")
        {
            AcMaxFrequencyMhz = 4_700,
            DcMaxFrequencyMhz = 3_800
        };
        var draft = new PerformanceDraft
        {
            TemperatureLimitC = 90,
            SplWatts = 55,
            SpptWatts = 75,
            AcMaxFrequencyMhz = 4_800,
            DcMaxFrequencyMhz = 3_900,
            IsBoostEnabled = true,
            WindowsPowerSchemeId = schemeId,
            NegativeCurveOptimizer = -10,
            AdvancedCpuTuning = new AdvancedCpuTuningDraft { StapmWatts = 65 }
        };

        SetCpuTuningCommand command = PerformanceCommandFactory.CreateForReadBackOnly(draft, state, true);

        Assert.IsNull(command.Plan.TemperatureLimitC);
        Assert.IsNull(command.Plan.SplWatts);
        Assert.IsNull(command.Plan.SpptWatts);
        Assert.IsNull(command.Plan.BoostEnabled);
        Assert.AreEqual(4_800, command.Plan.AcMaxFrequencyMhz);
        Assert.AreEqual(3_900, command.Plan.DcMaxFrequencyMhz);
        Assert.AreEqual(schemeId, command.Plan.WindowsPowerSchemeId);
        Assert.IsNull(command.Plan.NegativeCurveOptimizer);
        Assert.IsNull(command.Plan.Advanced);
        Assert.IsTrue(PerformanceCommandFactory.HasReadBackTargets(draft, state));
        Assert.IsFalse(PerformanceCommandFactory.HasCompleteReadBackCoverage(draft, state));
        Assert.IsFalse(PerformanceCommandFactory.HasReadBackTargets(draft, null));

        SetCpuTuningCommand restore = PerformanceCommandFactory.CreateRestoreForReadBackOnly(
            command.Plan, state, riskConfirmed: true);
        Assert.AreEqual(4_700, restore.Plan.AcMaxFrequencyMhz);
        Assert.AreEqual(3_800, restore.Plan.DcMaxFrequencyMhz);
        Assert.IsNull(restore.Plan.TemperatureLimitC);
        Assert.IsNull(restore.Plan.SpptWatts);
    }

    [TestMethod]
    public void Performance_command_factory_creates_a_new_operation_id_when_not_supplied()
    {
        var first = PerformanceCommandFactory.Create(new PerformanceDraft(), riskConfirmed: true);
        var second = PerformanceCommandFactory.Create(new PerformanceDraft(), riskConfirmed: true);

        Assert.AreNotEqual(Guid.Empty, first.OperationId);
        Assert.AreNotEqual(first.OperationId, second.OperationId);
    }

    [TestMethod]
    public void Applied_result_without_hardware_readback_is_not_reported_as_success()
    {
        var result = new CommandResult(
            Guid.NewGuid(),
            CommandState.Applied,
            null,
            RequiredUserAction.None,
            null,
            false);

        StringAssert.Contains(PerformanceCommandOutcome.Describe(result), "未确认");
    }

    [TestMethod]
    public void Rolled_back_result_reports_that_the_draft_was_not_applied()
    {
        var result = new CommandResult(
            Guid.NewGuid(),
            CommandState.RolledBack,
            null,
            RequiredUserAction.None,
            null,
            false);

        StringAssert.Contains(PerformanceCommandOutcome.Describe(result), "已回滚");
    }
}

internal static class PerformanceFixture
{
    public static PerformanceViewModel Create(RecordingControlCenterClient client) =>
        new(client, new RiskAcknowledgement(true, true));
}

internal sealed class RecordingControlCenterClient : IHardwareCommandSender
{
    public List<HardwareCommand> SentCommands { get; } = [];

    public Task<CommandResult> SendAsync(HardwareCommand command, CancellationToken cancellationToken)
    {
        SentCommands.Add(command);
        return Task.FromResult(new CommandResult(command.OperationId, CommandState.Applied, null, RequiredUserAction.None, null, false));
    }
}
