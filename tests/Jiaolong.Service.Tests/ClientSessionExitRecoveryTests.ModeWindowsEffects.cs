using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;

namespace Jiaolong.Service.Tests;

public sealed partial class ClientSessionExitRecoveryTests
{
    [TestMethod]
    public async Task Mode_only_exit_preserves_unattributed_windows_drift_and_resumes_after_exact_reconciliation()
    {
        using var fixture = new Fixture();
        fixture.Provider.JournalEveryModeStep = true;
        var original = fixture.Provider.ReadScheme(Fixture.Scheme);
        var handle = await fixture.Begin(null);
        fixture.Provider.ModeWriteEffects = () => fixture.Provider.SetExternalAcFrequency(original.AcMaxFrequencyMhz + 100);
        var applied = await fixture.Write(new SetPerformanceModeCommand(Guid.NewGuid(), PerformanceMode.Quiet), handle);
        Assert.AreEqual(CommandState.Applied, applied.State);
        Assert.AreEqual(original.AcMaxFrequencyMhz + 100, fixture.Provider.Cpu.AcFrequency());
        fixture.Provider.ModeWriteEffects = null;

        var ended = await fixture.End(handle);
        Assert.AreEqual(CommandState.RecoveryRequired, ended.State);
        Assert.AreEqual(original.AcMaxFrequencyMhz + 100, fixture.Provider.Cpu.AcFrequency());
        Assert.AreEqual(PerformanceMode.Quiet, fixture.Provider.Mode);
        Assert.AreEqual(0, fixture.Provider.WindowsRestorationWrites.Count);
        var pending = ExitGpuPlan(fixture);
        var operation = pending["operationId"]!.GetValue<string>();
        Assert.AreEqual("Conflict", pending["steps"]!.AsArray().Single(step => step!["key"]!.GetValue<string>() == "mode")!["status"]!.GetValue<string>());
        Assert.IsTrue((await fixture.Journal.ReadAsync(CancellationToken.None)).Unresolved.Length > 0);

        fixture.Provider.SetExternalAcFrequency(original.AcMaxFrequencyMhz);
        Assert.AreEqual(CommandState.Applied, (await fixture.End(handle)).State);
        Assert.AreEqual(operation, ExitGpuPlan(fixture)["operationId"]!.GetValue<string>());
        Assert.AreEqual(original, fixture.Provider.ReadScheme(Fixture.Scheme));
        Assert.AreEqual(PerformanceMode.Turbo, fixture.Provider.Mode);
        Assert.AreEqual(0, (await fixture.Journal.ReadAsync(CancellationToken.None)).Unresolved.Length);
    }

    [TestMethod]
    public async Task Mode_restore_preserves_unattributed_windows_drift_without_replaying_verified_mode()
    {
        using var fixture = new Fixture();
        fixture.Provider.JournalEveryModeStep = true;
        var original = fixture.Provider.ReadScheme(Fixture.Scheme);
        var handle = await fixture.Begin(null);
        Assert.AreEqual(CommandState.Applied,
            (await fixture.Write(new SetPerformanceModeCommand(Guid.NewGuid(), PerformanceMode.Quiet), handle)).State);
        int effects = 0;
        fixture.Provider.ModeWriteEffects = () =>
        {
            effects++;
            fixture.Provider.SetExternalAcFrequency(original.AcMaxFrequencyMhz + 100);
        };

        var ended = await fixture.End(handle);
        Assert.AreEqual(1, effects);
        Assert.AreEqual(CommandState.RecoveryRequired, ended.State);
        Assert.AreEqual(original.AcMaxFrequencyMhz + 100, fixture.Provider.Cpu.AcFrequency());
        Assert.AreEqual(0, fixture.Provider.WindowsRestorationWrites.Count);
        var pending = ExitGpuPlan(fixture);
        var operation = pending["operationId"]!.GetValue<string>();
        Assert.AreEqual("Verified", pending["steps"]!.AsArray().Single(step => step!["key"]!.GetValue<string>() == "mode")!["status"]!.GetValue<string>());
        Assert.AreEqual("Conflict", pending["steps"]!.AsArray().Single(step => step!["key"]!.GetValue<string>() == "performance.acMaxFrequencyMhz")!["status"]!.GetValue<string>());
        fixture.Provider.ModeWriteEffects = null;
        fixture.Provider.SetExternalAcFrequency(original.AcMaxFrequencyMhz);
        int writes = fixture.Provider.NativeModeScopes.Count;
        Assert.AreEqual(CommandState.Applied, (await fixture.End(handle)).State);
        Assert.AreEqual(writes, fixture.Provider.NativeModeScopes.Count);
        Assert.AreEqual(operation, ExitGpuPlan(fixture)["operationId"]!.GetValue<string>());
        Assert.AreEqual(original, fixture.Provider.ReadScheme(Fixture.Scheme));
        Assert.AreEqual(PerformanceMode.Turbo, fixture.Provider.Mode);
        Assert.AreEqual(0, (await fixture.Journal.ReadAsync(CancellationToken.None)).Unresolved.Length);
    }
}
