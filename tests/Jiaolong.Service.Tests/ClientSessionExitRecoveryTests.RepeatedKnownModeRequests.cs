using System.Text.Json;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong.Service.Home;

namespace Jiaolong.Service.Tests;

public sealed partial class ClientSessionExitRecoveryTests
{
    [TestMethod]
    public async Task Repeated_successful_mode_and_cpu_requests_restore_after_last_cpu_batch_is_pending()
    {
        using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
        var handle = await fixture.Begin(null);
        fixture.Provider.JournalEveryModeStep = true;

        PagePresetEnvelope Preset(int slot, int ac, int dc, int curve)
        {
            var key = PresetKey.Create(ControlModeId.Gaming, slot);
            var preset = new PagePresetEnvelope(PagePresetEnvelope.CurrentSchemaVersion, ControlPageId.Performance, key,
                $"repeated producer {slot}", JsonSerializer.SerializeToElement(new
                {
                    temperatureLimitC = 85,
                    splWatts = 55,
                    spptWatts = 65,
                    acMaxFrequencyMhz = ac,
                    dcMaxFrequencyMhz = dc,
                    isBoostEnabled = false,
                    windowsPowerSchemeId = Fixture.Scheme,
                    acMinActiveCoresPercent = (int?)null,
                    dcMinActiveCoresPercent = (int?)null,
                    negativeCurveOptimizer = curve,
                    advancedCpuTuning = (AdvancedCpuTuningPlan?)null
                }), DateTimeOffset.UtcNow)
            {
                Revision = 1,
                Settings = new()
                {
                    ["acMaxFrequencyMhz"] = SettingRequest.Set(ac),
                    ["dcMaxFrequencyMhz"] = SettingRequest.Set(dc),
                    ["isBoostEnabled"] = SettingRequest.Set(false),
                    ["negativeCurveOptimizer"] = SettingRequest.Set(curve)
                }
            };
            Assert.IsTrue(PresetValidation.Validate(preset).IsValid,
                string.Join(";", PresetValidation.Validate(preset).Errors));
            return preset;
        }

        var presets = new[] { Preset(1, 4500, 4400, -20), Preset(2, 4600, 4500, -10), Preset(3, 4550, 4450, -5) };
        Assert.AreEqual(CommandState.Applied,
            (await fixture.Write(new PublishPresetCatalogCommand(Guid.NewGuid(), 0, presets), handle)).State);

        async Task<(Guid OperationId, CommandResult Result)> Apply(int slot, PerformanceMode mode)
        {
            Guid operationId = Guid.NewGuid();
            var command = new ApplyControlRequestCommand(operationId, new(operationId, 0, ControlOrigin.PresetUse,
                ControlModeId.Gaming, mode,
                [new(ControlPageId.Performance, true, PresetKey.Create(ControlModeId.Gaming, slot), 1)]));
            return (operationId, await fixture.Write(command, handle));
        }

        var first = await Apply(1, PerformanceMode.Balanced);
        Assert.AreEqual(CommandState.Applied, first.Result.State);
        Assert.AreEqual(PerformanceMode.Balanced, fixture.Provider.Mode);
        Assert.AreEqual(4500, fixture.Provider.Cpu.AcFrequency());
        Assert.AreEqual(4400, fixture.Provider.Cpu.DcFrequency());
        Assert.AreEqual(-20, fixture.Provider.Cpu.NegativeCurveOptimizer);
        Assert.AreEqual(0, (await fixture.Journal.ReadAsync(CancellationToken.None)).Unresolved.Length);

        var second = await Apply(2, PerformanceMode.Quiet);
        Assert.AreEqual(CommandState.Applied, second.Result.State);
        Assert.AreNotEqual(first.OperationId, second.OperationId);
        Assert.AreEqual(PerformanceMode.Quiet, fixture.Provider.Mode);
        Assert.AreEqual(4600, fixture.Provider.Cpu.AcFrequency());
        Assert.AreEqual(4500, fixture.Provider.Cpu.DcFrequency());
        Assert.AreEqual(-10, fixture.Provider.Cpu.NegativeCurveOptimizer);
        Assert.AreEqual(0, (await fixture.Journal.ReadAsync(CancellationToken.None)).Unresolved.Length);

        fixture.Provider.InterruptCpuBatchAfterPlans = 1;
        var partial = await Apply(3, PerformanceMode.Balanced);
        Assert.AreEqual(CommandState.RecoveryRequired, partial.Result.State);
        Assert.AreNotEqual(second.OperationId, partial.OperationId);
        Assert.AreNotEqual(first.OperationId, partial.OperationId);

        var saved = await fixture.Journal.ReadAsync(CancellationToken.None);
        var session = await new ClientControlSessionStore(fixture.Root).ReadAsync(CancellationToken.None);
        Assert.IsNotNull(session);
        var pending = saved.Unresolved.Single();
        Assert.AreEqual(partial.OperationId, pending.OperationId);
        Assert.AreEqual(nameof(ApplyControlRequestCommand), pending.CommandKind);
        Assert.AreEqual(fixture.Owner.UserSid, pending.OwnerSid);
        Assert.AreEqual(saved.Identity, pending.Identity);
        Assert.AreEqual(handle.HardwareEpoch, pending.State.HardwareEpoch);
        Assert.IsNotNull(pending.NativeModeAttempt);
        var modeAttempt = pending.NativeModeAttempt!;
        Assert.AreEqual(partial.OperationId, modeAttempt.StepId);
        Assert.AreEqual(handle.HardwareEpoch, modeAttempt.HardwareEpoch);
        Assert.AreEqual(fixture.Owner.UserSid, modeAttempt.OwnerSid);
        Assert.AreEqual(handle.SessionId, modeAttempt.ClientSessionId);
        Assert.AreEqual(session!.ServiceSessionId, modeAttempt.ServiceSessionId);
        Assert.AreEqual(pending.Identity, modeAttempt.Identity);
        Assert.AreEqual(PerformanceMode.Quiet, modeAttempt.Before);
        Assert.AreEqual(PerformanceMode.Balanced, modeAttempt.Target);
        Assert.AreEqual(PerformanceMode.Balanced, modeAttempt.Observed);
        Assert.AreEqual(PerformanceMode.Balanced, saved.Current!.NativeMode);
        Assert.AreEqual(ApplyState.Confirmed, saved.Current.NativeModeApplyState);
        Assert.AreEqual(handle.SessionId, saved.Current.NativeModeOwnerClientSessionId);
        Assert.AreEqual(partial.OperationId, saved.Current.CpuWriteBatch!.OperationId);
        Assert.AreEqual(4550, saved.Current.CpuWriteBatch.Plans.Single().AcMaxFrequencyMhz);

        string journalPath = Path.Combine(fixture.Root, "control-execution.json");
        string journalBeforeRefusal = await File.ReadAllTextAsync(journalPath);
        int writesBeforeRefusal = fixture.Provider.WriteCount;
        var refused = await Apply(1, PerformanceMode.Quiet);
        Assert.AreEqual(CommandState.RecoveryRequired, refused.Result.State);
        Assert.AreEqual(HardwareMutation.NoMutation, refused.Result.Mutation);
        Assert.AreEqual("persistedRecoveryRequired", refused.Result.Error?.Details.GetValueOrDefault("reason"));
        Assert.AreEqual(bool.FalseString, refused.Result.Error?.Details.GetValueOrDefault("writeAttempted"));
        Assert.AreEqual(writesBeforeRefusal, fixture.Provider.WriteCount);
        Assert.AreEqual(journalBeforeRefusal, await File.ReadAllTextAsync(journalPath));

        var recovered = await RestartForPartialCpuEnd(fixture).RestorePriorClientSessionAsync(
            new ProcessProbe(fixture.Owner) { State = ClientProcessState.Exited }, CancellationToken.None);
        Assert.AreEqual(CommandState.Applied, recovered!.State,
            recovered.Error?.Details.GetValueOrDefault("clientSessionRecovery"));
        Assert.AreEqual(PerformanceMode.Turbo, fixture.Provider.Mode);
        Assert.AreEqual(4700, fixture.Provider.Cpu.AcFrequency());
        Assert.AreEqual(4700, fixture.Provider.Cpu.DcFrequency());
        Assert.AreEqual(OriginalCurve, fixture.Provider.Cpu.NegativeCurveOptimizer);
        var final = await new ControlExecutionJournal(fixture.Root).ReadAsync(CancellationToken.None);
        Assert.AreEqual(0, final.Unresolved.Length);
        Assert.AreEqual(0, final.Current!.Fields.Count);
        Assert.AreEqual(ClientControlSessionPhase.Completed,
            (await new ClientControlSessionStore(fixture.Root).ReadAsync(CancellationToken.None))!.Phase);
    }
}
