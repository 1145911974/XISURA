using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong.Service.Home;
using Jiaolong_ControlCenter.Services;

namespace Jiaolong.Service.Tests;

public sealed partial class ClientSessionExitRecoveryTests
{
    private static ClientControlSessionLifecycle ManagedPipeLifecycle(ControlCenterClient main, ControlCenterClient peer, int? curve)
    {
        main.RequireControlServiceIdentity();
        return new(main.BeginClientSessionAsync, main.ConfigureClientSessionAsync, peer.EndClientSessionAsync,
            key => curve is int value ? new ExitDeviceDefaults(1, key, value) : null);
    }

    [TestMethod]
    public async Task Pipe_handoff_restarts_the_same_endpoint_recovers_the_old_capture_and_restores_the_new_session()
    {
        using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        string name = "Xisura-pipe-handoff-" + Guid.NewGuid().ToString("N");
        await using var firstHost = new LifecyclePipeHost(fixture.Runtime, fixture.Owner, name);
        await using var first = new ControlCenterClient(name);
        var oldLifecycle = ManagedPipeLifecycle(first, first, -30);
        await oldLifecycle.StartAsync(deadline.Token);
        foreach (HardwareCommand command in new HardwareCommand[]
        {
            new SetCpuTuningCommand(Guid.NewGuid(), CpuPlan(4600, -5), true),
            new SetGpuMemoryOffsetCommand(Guid.NewGuid(), 0, 1000, true),
            new SetKeyboardLightingCommand(Guid.NewGuid(), fixture.Provider.Light with { Red = 12 }),
            new SetPerformanceModeCommand(Guid.NewGuid(), PerformanceMode.Balanced)
        }) Assert.AreEqual(CommandState.Applied, (await oldLifecycle.SendAsync(command, first.SendCommandAsync, deadline.Token)).State);
        var oldCapture = ReadCapturedSession(fixture);
        var oldId = oldCapture.GetProperty("handle").GetProperty("sessionId").GetGuid();
        await firstHost.DisposeAsync();
        var runtime = RestartForPartialCpuEnd(fixture);
        var newOwner = fixture.Owner with { ProcessId = 43 };
        await using var secondHost = new LifecyclePipeHost(runtime, newOwner, name);
        while (first.IsConnected) await Task.Delay(10, deadline.Token);
        Assert.AreEqual(PerformanceMode.Balanced, (await first.GetHomeStateAsync(deadline.Token)).Controls.PerformanceMode);
        Assert.IsFalse(first.IsControlServiceCurrent);
        await using var main = new ControlCenterClient(name);
        await using var peer = main.CreatePeer();
        var newLifecycle = ManagedPipeLifecycle(main, peer, null);
        int writes = fixture.Provider.WriteCount;
        var before = OriginalRecords(fixture);
        await Assert.ThrowsAsync<InvalidOperationException>(() => newLifecycle.StartAsync(deadline.Token));
        AssertOriginalRecords(fixture, before);
        await Assert.ThrowsAsync<InvalidOperationException>(() => oldLifecycle.SendAsync(new SetLidLogoCommand(Guid.NewGuid(), false),
            first.SendCommandAsync, deadline.Token));
        Assert.AreEqual(writes, fixture.Provider.WriteCount);
        var probe = new ProcessProbe(fixture.Owner);
        Assert.IsNull(await runtime.RecoverExitedClientSessionAsync(probe, deadline.Token));
        Assert.AreEqual(PerformanceMode.Balanced, (await main.GetHomeStateAsync(deadline.Token)).Controls.PerformanceMode);
        Assert.AreEqual(writes, fixture.Provider.WriteCount);
        probe.State = ClientProcessState.Exited;
        Assert.AreEqual(CommandState.Applied, (await runtime.RecoverExitedClientSessionAsync(probe, deadline.Token))?.State);
        Assert.AreEqual("Completed", ReadCapturedSession(fixture).GetProperty("phase").GetString());
        Assert.AreEqual(4700, fixture.Provider.Cpu.AcFrequency());
        Assert.AreEqual(-30, fixture.Provider.Cpu.NegativeCurveOptimizer);
        Assert.AreEqual(0, fixture.Provider.Gpu.MemoryOffsetKhz);
        CollectionAssert.AreEqual(new int[127], fixture.Provider.Offsets());
        Assert.AreEqual(Fixture.OriginalLight, fixture.Provider.Light);
        Assert.AreEqual(PerformanceMode.Turbo, (await peer.GetHomeStateAsync(deadline.Token)).Controls.PerformanceMode);
        writes = fixture.Provider.WriteCount;
        await newLifecycle.StartAsync(deadline.Token);
        Assert.AreEqual(writes, fixture.Provider.WriteCount, "New Begin/configuration must only capture and configure recovery.");
        Assert.AreNotEqual(oldId, ReadCapturedSession(fixture).GetProperty("handle").GetProperty("sessionId").GetGuid());
        Assert.AreEqual(runtime.SessionId, ReadCapturedSession(fixture).GetProperty("serviceSessionId").GetGuid());
        Assert.AreEqual(CommandState.Applied, (await newLifecycle.SendAsync(new SetCpuTuningCommand(Guid.NewGuid(), CpuPlan(4300, -4), true),
            peer.SendCommandAsync, deadline.Token)).State);
        Assert.AreEqual(CommandState.Applied, (await newLifecycle.EndAsync(deadline.Token))?.State);
        Assert.AreEqual(4700, fixture.Provider.Cpu.AcFrequency());
        Assert.AreEqual(-30, fixture.Provider.Cpu.NegativeCurveOptimizer);
        Assert.AreEqual(Fixture.OriginalLight, fixture.Provider.Light);
        var saved = await runtime.ReadControlJournalAsync(deadline.Token);
        Assert.AreEqual(0, saved!.Unresolved.Length);
        Assert.AreEqual(0, saved.Current!.Fields.Count);
    }

    [TestMethod]
    public async Task Pipe_handoff_failed_recovery_preserves_original_records_and_new_client_can_retry_after_worker_completion()
    {
        using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
        var handle = await fixture.Begin(null);
        await fixture.Write(new SetGpuMemoryOffsetCommand(Guid.NewGuid(), 0, 1000, true), handle);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var runtime = RestartForPartialCpuEnd(fixture);
        string name = "Xisura-pipe-retry-" + Guid.NewGuid().ToString("N");
        await using var host = new LifecyclePipeHost(runtime, fixture.Owner with { ProcessId = 43 }, name);
        await using var main = new ControlCenterClient(name);
        var lifecycle = ManagedPipeLifecycle(main, main, null);
        var probe = new ProcessProbe(fixture.Owner) { State = ClientProcessState.Exited };
        fixture.Provider.FailMemoryRestore = true;
        Assert.AreEqual(CommandState.RecoveryRequired, (await runtime.RecoverExitedClientSessionAsync(probe, deadline.Token))?.State);
        var before = OriginalRecords(fixture);
        var pending = await runtime.ReadControlJournalAsync(deadline.Token);
        Assert.AreEqual(1, pending!.Unresolved.Length);
        Guid parent = pending.Unresolved[0].OperationId;
        int writes = fixture.Provider.WriteCount;
        await Assert.ThrowsAsync<InvalidOperationException>(() => lifecycle.StartAsync(deadline.Token));
        Assert.AreEqual(1000, (await main.GetHomeStateAsync(deadline.Token)).Controls.GpuVf!.MemoryOffsetKhz);
        Assert.AreEqual(writes, fixture.Provider.WriteCount);
        AssertOriginalRecords(fixture, before);
        fixture.Provider.FailMemoryRestore = false;
        Assert.AreEqual(CommandState.Applied, (await runtime.RecoverExitedClientSessionAsync(probe, deadline.Token))?.State);
        Assert.AreEqual(0, fixture.Provider.Gpu.MemoryOffsetKhz);
        Assert.AreEqual("Completed", ReadCapturedSession(fixture).GetProperty("phase").GetString());
        writes = fixture.Provider.WriteCount;
        await lifecycle.StartAsync(deadline.Token);
        Assert.AreEqual(writes, fixture.Provider.WriteCount);
        Assert.AreEqual(CommandState.Applied, (await lifecycle.SendAsync(new SetLidLogoCommand(Guid.NewGuid(), false), main.SendCommandAsync, deadline.Token)).State);
        Assert.AreEqual(CommandState.Applied, (await lifecycle.EndAsync(deadline.Token))?.State);
        Assert.AreEqual(Fixture.OriginalLight, fixture.Provider.Light);
        Assert.IsTrue(before["client-session-recovery.json"].Contains(parent.ToString(), StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task Pipe_handoff_unknown_process_and_external_values_keep_reads_available_without_replacing_the_capture()
    {
        foreach (bool external in new[] { false, true })
        {
            using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
            var handle = await fixture.Begin(null);
            await fixture.Write(new SetLidLogoCommand(Guid.NewGuid(), false), handle);
            if (external) fixture.Provider.SetExternalVfOffset(17, 12345);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var runtime = RestartForPartialCpuEnd(fixture);
            string name = "Xisura-pipe-refuse-" + Guid.NewGuid().ToString("N");
            await using var host = new LifecyclePipeHost(runtime, fixture.Owner with { ProcessId = 43 }, name);
            await using var main = new ControlCenterClient(name);
            var original = OriginalRecords(fixture);
            int writes = fixture.Provider.WriteCount;
            Assert.AreEqual(CommandState.RecoveryRequired, (await runtime.RecoverExitedClientSessionAsync(new ProcessProbe(fixture.Owner)
                { State = external ? ClientProcessState.Exited : ClientProcessState.Unknown }, deadline.Token))?.State);
            var lifecycle = ManagedPipeLifecycle(main, main, null);
            await Assert.ThrowsAsync<InvalidOperationException>(() => lifecycle.StartAsync(deadline.Token));
            Assert.AreEqual(PerformanceMode.Turbo, (await main.GetHomeStateAsync(deadline.Token)).Controls.PerformanceMode);
            await Assert.ThrowsAsync<InvalidOperationException>(() => lifecycle.SendAsync(new SetLidLogoCommand(Guid.NewGuid(), true), main.SendCommandAsync, deadline.Token));
            Assert.AreEqual(writes, fixture.Provider.WriteCount);
            AssertOriginalRecords(fixture, original);
        }
    }
}
