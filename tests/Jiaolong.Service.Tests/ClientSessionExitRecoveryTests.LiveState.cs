using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong.Hardware.Mechrevo.Controls;
using Jiaolong.Service.Home;
using System.Text.Json;

namespace Jiaolong.Service.Tests;

public sealed partial class ClientSessionExitRecoveryTests
{
    [TestMethod]
    public async Task Prior_live_state_matches_original_multi_page_ownership_and_restores_without_replacing_capture()
    {
        using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
        var handle = await fixture.Begin(-30);
        foreach (HardwareCommand command in new HardwareCommand[]
        {
            new SetCpuTuningCommand(Guid.NewGuid(), CpuPlan(4600, -5), true),
            new SetGpuMemoryOffsetCommand(Guid.NewGuid(), 0, 1000, true),
            new SetKeyboardLightingCommand(Guid.NewGuid(), fixture.Provider.Light with { Red = 12, LogoEnabled = false }),
            new SetPerformanceModeCommand(Guid.NewGuid(), PerformanceMode.Balanced)
        }) Assert.AreEqual(CommandState.Applied, (await fixture.Write(command, handle)).State);
        var capture = ReadCapturedSession(fixture);
        int writes = fixture.Provider.WriteCount;
        var restarted = Restarted(fixture);
        var result = await restarted.RecoverExitedClientSessionAsync(new ProcessProbe(fixture.Owner)
            { State = ClientProcessState.Exited }, CancellationToken.None);
        Assert.AreEqual(CommandState.Applied, result!.State);
        Assert.IsTrue(fixture.Provider.WriteCount > writes);
        Assert.IsFalse(restarted.CanRunClientAutomation(fixture.Owner.UserSid));
        Assert.AreEqual(4700, fixture.Provider.Cpu.AcFrequency());
        Assert.AreEqual(-30, fixture.Provider.Cpu.NegativeCurveOptimizer);
        Assert.AreEqual(0, fixture.Provider.Gpu.MemoryOffsetKhz);
        Assert.AreEqual(Fixture.OriginalLight, fixture.Provider.Light);
        Assert.AreEqual(PerformanceMode.Turbo, fixture.Provider.Mode);
        var completed = ReadCapturedSession(fixture);
        Assert.IsTrue(JsonElement.DeepEquals(capture, JsonSerializer.SerializeToElement(
            (await new ClientControlSessionStore(fixture.Root).ReadAsync(CancellationToken.None))! with
                { Phase = ClientControlSessionPhase.Active }, new JsonSerializerOptions(JsonSerializerDefaults.Web))));
        Assert.AreEqual("Completed", completed.GetProperty("phase").GetString());
        var journal = await new ControlExecutionJournal(fixture.Root).ReadAsync(CancellationToken.None);
        Assert.AreEqual(0, journal.Unresolved.Length);
        Assert.AreEqual(0, journal.Current!.Fields.Count);
    }

    [TestMethod]
    public async Task Prior_live_state_preserves_foreign_cpu_gpu_and_lighting_values()
    {
        foreach (string field in new[] { "cpu", "gpu", "lighting" })
        {
            using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
            var handle = await fixture.Begin(null);
            await fixture.Write(new SetCpuTuningCommand(Guid.NewGuid(), CpuPlan(4600, -5), true), handle);
            await fixture.Write(new SetGpuMemoryOffsetCommand(Guid.NewGuid(), 0, 1000, true), handle);
            if (field == "cpu") fixture.Provider.ReadBaselineOverride = controls => controls with
            { CpuTuning = controls.CpuTuning! with { PerCoreCurveOptimizer = controls.CpuTuning.PerCoreCurveOptimizer!
                .ToDictionary(pair => pair.Key, pair => pair.Key == 0 ? -6 : pair.Value) } };
            if (field == "gpu") fixture.Provider.SetExternalVfOffset(17, 12345);
            if (field == "lighting") fixture.Provider.ReadBaselineOverride = controls => controls with
                { KeyboardLighting = controls.KeyboardLighting! with { Red = 12 } };
            var original = OriginalRecords(fixture);
            int writes = fixture.Provider.WriteCount;
            string reason = field switch { "cpu" => "LiveStateConflict:performance.negativeCurveOptimizer",
                "gpu" => "LiveStateConflict:gpu.vfOffsetsKhz", _ => "LiveStateConflict:lighting.hex" };
            AssertPriorRecovery(await Restarted(fixture).RecoverExitedClientSessionAsync(new ProcessProbe(fixture.Owner)
                { State = ClientProcessState.Exited }, CancellationToken.None), reason);
            Assert.AreEqual(writes, fixture.Provider.WriteCount);
            AssertOriginalRecords(fixture, original);
        }
    }

    [TestMethod]
    public async Task Prior_live_state_checks_inactive_windows_schemes_without_activating_or_repairing_them()
    {
        using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
        var handle = await fixture.Begin(null);
        Assert.AreEqual(CommandState.Applied, (await fixture.Write(new SetCpuTuningCommand(Guid.NewGuid(), CpuPlan(4600, -5), true), handle)).State);
        Assert.AreEqual(CommandState.Applied, (await fixture.Write(new SetCpuTuningCommand(Guid.NewGuid(),
            CpuPlan(3800, -4) with { WindowsPowerSchemeId = Fixture.OtherScheme }, true), handle)).State);
        Assert.AreEqual(CommandState.Applied, (await fixture.Write(new SetCpuTuningCommand(Guid.NewGuid(), CpuPlan(4600, -5), true), handle)).State);
        fixture.Provider.ExternalSchemeIndex(Fixture.OtherScheme, WindowsCpuSetting.AcMaxFrequencyMhz, 3700);
        var original = OriginalRecords(fixture);
        int writes = fixture.Provider.WriteCount;
        AssertPriorRecovery(await Restarted(fixture).RecoverExitedClientSessionAsync(new ProcessProbe(fixture.Owner)
            { State = ClientProcessState.Exited }, CancellationToken.None), "WindowsScopeLiveStateConflict");
        Assert.AreEqual(Fixture.Scheme, fixture.Provider.Cpu.WindowsPowerSchemeId);
        Assert.AreEqual(writes, fixture.Provider.WriteCount);
        AssertOriginalRecords(fixture, original);
    }

    [TestMethod]
    public async Task Prior_live_state_rejects_unknown_stale_or_changing_mode_and_incomplete_gpu_readback()
    {
        foreach (string failure in new[] { "mode-missing", "mode-stale", "mode-changing", "gpu" })
        {
            using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
            var handle = await fixture.Begin(null);
            await fixture.Write(new SetGpuMemoryOffsetCommand(Guid.NewGuid(), 0, 1000, true), handle);
            fixture.Provider.NativeEvidenceAvailable = failure != "mode-missing";
            if (failure == "mode-stale") fixture.Provider.ModeEvidenceAge = TimeSpan.FromMinutes(1);
            if (failure == "mode-changing") fixture.Provider.ChangeModeOnSecondEvidence = true;
            if (failure == "gpu") fixture.Provider.ReadBaselineOverride = controls => controls with { GpuVf = null };
            var original = OriginalRecords(fixture);
            int writes = fixture.Provider.WriteCount;
            AssertPriorRecovery(await Restarted(fixture).RecoverExitedClientSessionAsync(new ProcessProbe(fixture.Owner)
                { State = ClientProcessState.Exited }, CancellationToken.None), failure == "gpu" ? "GpuLiveStateUnreadable" : "NativeModeLiveStateUnverified");
            Assert.AreEqual(writes, fixture.Provider.WriteCount);
            AssertOriginalRecords(fixture, original);
        }
    }

    [TestMethod]
    public async Task Prior_live_state_rechecks_fields_between_native_mode_observations()
    {
        using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
        var handle = await fixture.Begin(null);
        await fixture.Write(new SetGpuMemoryOffsetCommand(Guid.NewGuid(), 0, 1000, true), handle);
        int reads = 0, writes = fixture.Provider.WriteCount;
        fixture.Provider.ReadBaselineOverride = controls => ++reads == 1 ? controls : controls with
            { KeyboardLighting = controls.KeyboardLighting! with { Red = 12 } };
        var original = OriginalRecords(fixture);
        AssertPriorRecovery(await Restarted(fixture).RecoverExitedClientSessionAsync(new ProcessProbe(fixture.Owner)
            { State = ClientProcessState.Exited }, CancellationToken.None), "LiveStateConflict:lighting.hex");
        Assert.AreEqual(2, reads);
        Assert.AreEqual(writes, fixture.Provider.WriteCount);
        AssertOriginalRecords(fixture, original);
    }

    [TestMethod]
    public async Task Prior_live_state_retries_original_partial_exit_plan_and_retains_unavailable_native_fan_recovery()
    {
        foreach (bool partialExit in new[] { false, true })
        {
            using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
            var handle = await fixture.Begin(null);
            if (partialExit)
            {
                await fixture.Write(new SetGpuMemoryOffsetCommand(Guid.NewGuid(), 0, 1000, true), handle);
                fixture.Provider.FailMemoryRestore = true;
                Assert.AreEqual(CommandState.RecoveryRequired, (await fixture.End(handle)).State);
            }
            else await fixture.Write(new SetStrongCoolingCommand(Guid.NewGuid(), true), handle);
            var capture = ReadCapturedSession(fixture);
            var records = OriginalRecords(fixture);
            string path = Path.Combine(fixture.Root, "client-session-recovery.json");
            using var originalDocument = partialExit ? JsonDocument.Parse(File.ReadAllText(path)) : null;
            int writes = fixture.Provider.WriteCount;
            var restarted = Restarted(fixture);
            var result = await restarted.RecoverExitedClientSessionAsync(new ProcessProbe(fixture.Owner)
                { State = ClientProcessState.Exited }, CancellationToken.None);
            Assert.AreEqual(CommandState.RecoveryRequired, result!.State);
            Assert.AreEqual(writes, fixture.Provider.WriteCount);
            Assert.IsFalse(restarted.CanRunClientAutomation(fixture.Owner.UserSid));
            if (!partialExit)
            {
                // This legacy fan owner has no durable EC proof, so inspection cannot start a restore.
                AssertPriorRecovery(result, "NativeFanRecoveryVerificationRequired");
                AssertOriginalRecords(fixture, records);
                Assert.IsFalse(File.Exists(path));
                Assert.IsTrue((await fixture.Provider.ReadControlsAsync(CancellationToken.None)).StrongCooling);
                continue;
            }
            var closing = await new ClientControlSessionStore(fixture.Root).ReadAsync(CancellationToken.None);
            Assert.AreEqual(ClientControlSessionPhase.Closing, closing!.Phase);
            Assert.IsTrue(JsonElement.DeepEquals(capture, JsonSerializer.SerializeToElement(closing with
                { Phase = ClientControlSessionPhase.Closing },
                new JsonSerializerOptions(JsonSerializerDefaults.Web))));
            using var plan = JsonDocument.Parse(File.ReadAllText(path));
            Assert.AreEqual(handle.SessionId, plan.RootElement.GetProperty("sessionId").GetGuid());
            Assert.AreEqual(originalDocument!.RootElement.GetProperty("operationId").GetGuid(), plan.RootElement.GetProperty("operationId").GetGuid());
            Assert.AreEqual(1000, fixture.Provider.Gpu.MemoryOffsetKhz);
        }
    }
}
