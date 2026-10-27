using System.Text.Json;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong.Service.Home;

namespace Jiaolong.Service.Tests;

public sealed partial class ClientSessionExitRecoveryTests
{
    [TestMethod]
    public async Task Compound_fan_completed_before_lost_confirmation_resumes_original_parent_without_replaying_gpu_child()
    {
        using var fixture = new Fixture(readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
        var baseline = fixture.Provider.ReadScheme(Fixture.Scheme);
        var prior = await SeedCompoundGpuFan(fixture, "fan");
        var journal = new ControlExecutionJournal(fixture.Root);
        fixture.BindModeJournal(journal);
        fixture.BindGpuJournal(journal);
        var runtime = new HomeServiceRuntime(fixture.Provider, journal: journal, requireClientSessions: true,
            readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
        var probe = new ProcessProbe(fixture.Owner) { State = ClientProcessState.Exited };
        FanEcRecoveryScope? originalScope = null;
        int parentCalls = 0;
        fixture.Provider.FanRecovery = async (id, scope, token) =>
        {
            Assert.AreEqual(prior.Parent, id);
            originalScope = scope;
            parentCalls++;
            await CompleteRuntimeFan(fixture, journal, id, scope, token);
            return new(id, CommandState.RecoveryRequired, null, RequiredUserAction.None, null, false)
                { Mutation = HardwareMutation.Unknown };
        };
        var partial = await runtime.RestorePriorClientSessionAsync(probe, CancellationToken.None);
        Assert.IsNotNull(partial);
        Assert.AreEqual(CommandState.RecoveryRequired, partial.State, JsonSerializer.Serialize(partial));
        Assert.AreEqual(1, parentCalls);
        Assert.IsNotNull(originalScope);
        var pending = await journal.ReadAsync(CancellationToken.None);
        Assert.AreEqual(prior.Parent, pending.Unresolved.Single().OperationId);
        Assert.AreEqual(nameof(ApplyControlRequestCommand), pending.Unresolved.Single().CommandKind);
        Assert.AreEqual(originalScope, pending.FanEcRecovery!.Scope);
        Assert.IsTrue(ControlExecutionJournal.FanCheckpointComplete(pending.FanEcRecovery.Checkpoint));
        Assert.IsTrue(ControlExecutionJournal.FanFirmwareComplete(pending.FanFirmwareRecovery!));
        Assert.AreEqual((byte)0, fixture.Provider.Fan.Control);
        Assert.IsTrue(fixture.Provider.FirmwareStrongCooling);
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Root, "client-session-recovery.json")));
        Assert.AreEqual(4600, fixture.Provider.Cpu.AcFrequency());
        Assert.AreEqual(10000, fixture.Provider.Gpu.CoreOffsetKhz);
        Assert.AreEqual(1000, fixture.Provider.Gpu.MemoryOffsetKhz);
        Assert.IsTrue(fixture.Provider.Offsets().Skip(1).All(value => value == 75000));
        int gpuRecoveries = fixture.Provider.GpuRecoveries;

        journal = new(fixture.Root);
        fixture.BindModeJournal(journal);
        fixture.BindGpuJournal(journal);
        runtime = new(fixture.Provider, journal: journal, requireClientSessions: true,
            readBootTime: ReadRestartBoot, readPowerContinuity: ReadRestartPower);
        fixture.Provider.FanRecovery = async (id, scope, token) =>
        {
            Assert.AreEqual(originalScope, scope);
            Assert.AreEqual(new ControlExecutionJournal.FanRecoveryOperation(id, scope), journal.CurrentFanRecoveryOperation);
            if (id == prior.Parent)
            {
                parentCalls++;
                Assert.AreEqual(gpuRecoveries, fixture.Provider.GpuRecoveries);
                Assert.AreEqual(10000, fixture.Provider.Gpu.CoreOffsetKhz);
                Assert.AreEqual(prior.Parent, (await journal.ReadAsync(token)).Unresolved.Single().OperationId);
            }
            // The first call completed hardware/WAL; subsequent calls only verify it.
            return new(id, CommandState.Applied, null, RequiredUserAction.None, null, false)
                { Mutation = HardwareMutation.NoMutation };
        };
        var commands = new List<HardwareCommand>();
        fixture.Provider.BeforeWrite = command => { commands.Add(command); return Task.CompletedTask; };
        var restored = await runtime.RestorePriorClientSessionAsync(probe, CancellationToken.None);
        Assert.IsNotNull(restored);
        Assert.AreEqual(CommandState.Applied, restored.State, JsonSerializer.Serialize(restored));
        Assert.AreEqual(2, parentCalls);
        Assert.IsFalse(commands.Any(command => command is ApplyControlRequestCommand or SetFanControlCommand or SetPerformanceModeCommand));
        Assert.IsTrue(commands.OfType<SetGpuCoreOffsetCommand>().All(command => command.OffsetKhz == 0));
        Assert.IsTrue(commands.OfType<SetGpuMemoryOffsetCommand>().All(command => command.OffsetKhz == 0));
        Assert.IsTrue(commands.OfType<SetGpuVfCurveCommand>().All(command => command.OffsetsKhz.All(value => value == 0)));
        Assert.AreEqual(baseline, fixture.Provider.ReadScheme(Fixture.Scheme));
        Assert.AreEqual(OriginalCurve, fixture.Provider.Cpu.NegativeCurveOptimizer);
        Assert.AreEqual(0, fixture.Provider.Gpu.CoreOffsetKhz);
        Assert.AreEqual(0, fixture.Provider.Gpu.MemoryOffsetKhz);
        Assert.IsTrue(fixture.Provider.Offsets().All(value => value == 0));
        Assert.AreEqual(Fixture.OriginalLight, fixture.Provider.Light);
        Assert.IsTrue(fixture.Provider.FirmwareStrongCooling);
        var completed = await journal.ReadAsync(CancellationToken.None);
        Assert.AreEqual(0, completed.Unresolved.Length);
        Assert.AreEqual(0, completed.Current!.Fields.Count);
        using var capture = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(fixture.Root, "client-control-session.json")));
        Assert.AreEqual("Completed", capture.RootElement.GetProperty("phase").GetString());
    }
}
