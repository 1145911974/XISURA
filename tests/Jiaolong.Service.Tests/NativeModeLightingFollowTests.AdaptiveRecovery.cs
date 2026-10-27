using System.Reflection;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong.Service.Home;

namespace Jiaolong.Service.Tests;

public sealed partial class NativeModeLightingFollowTests
{
    [TestMethod]
    public async Task Pending_store_recovery_blocks_enable_without_rewriting_and_disable_preserves_it()
    {
        using var fixture = await Fixture.CreateAsync();
        var beforeRecovery = await fixture.Store.ReadAsync(CancellationToken.None);
        var pending = new AdaptiveAutomationRecovery(PerformanceMode.Balanced, null, PerformanceMode.Turbo, true, true);
        Assert.IsTrue(await fixture.Store.TryBeginRecoveryAsync(pending, beforeRecovery.Revision, CancellationToken.None));
        await fixture.Store.MarkManualModeChangeAsync(DateTimeOffset.UtcNow, true, CancellationToken.None);

        string file = PersistedFile(fixture);
        byte[] beforeEnable = await File.ReadAllBytesAsync(file);
        var beforeEnableState = await fixture.Store.ReadAsync(CancellationToken.None);
        Assert.AreEqual(beforeRecovery.ManualOverrideUntilUtc, beforeEnableState.ManualOverrideUntilUtc,
            "A retained recovery record also preserves its original override fence.");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fixture.Store.SetConfigurationAsync(
            beforeEnableState.Configuration!, CancellationToken.None, beforeEnableState.Revision,
            AutomationConfigurationIntent.Enable, beforeEnableState.OwnerSid));
        CollectionAssert.AreEqual(beforeEnable, await File.ReadAllBytesAsync(file));

        await fixture.Store.SetConfigurationAsync(beforeEnableState.Configuration!, CancellationToken.None,
            beforeEnableState.Revision, AutomationConfigurationIntent.Disable, beforeEnableState.OwnerSid);
        var disabled = await fixture.Store.ReadAsync(CancellationToken.None);
        Assert.IsFalse(disabled.Configuration!.Enabled);
        Assert.AreEqual(pending, disabled.Recovery);
        Assert.AreEqual(beforeRecovery.ManualOverrideUntilUtc, disabled.ManualOverrideUntilUtc);
    }

    [TestMethod]
    public async Task Automatic_hardware_entry_rejects_pending_store_recovery_before_provider_calls()
    {
        using var fixture = await Fixture.CreateAsync();
        var saved = await fixture.Store.ReadAsync(CancellationToken.None);
        await fixture.Store.SetConfigurationAsync(saved.Configuration! with { Enabled = true }, CancellationToken.None,
            saved.Revision, AutomationConfigurationIntent.Enable, saved.OwnerSid);
        saved = await fixture.Store.ReadAsync(CancellationToken.None);
        var pending = new AdaptiveAutomationRecovery(PerformanceMode.Balanced, null, PerformanceMode.Turbo, true, true);
        Assert.IsTrue(await fixture.Store.TryBeginRecoveryAsync(pending, saved.Revision, CancellationToken.None));
        byte[] before = await File.ReadAllBytesAsync(PersistedFile(fixture));

        var provider = DispatchProxy.Create<IHomeHardwareProvider, RecoveryHardwareProxy>();
        var proxy = (RecoveryHardwareProxy)(object)provider;
        var bridge = new AdaptiveAutomationHardwareProvider(provider, fixture.Store);
        var runtime = new HomeServiceRuntime(bridge);
        using var worker = new AdaptiveAutomationWorker(fixture.Store, runtime, bridge,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AdaptiveAutomationWorker>.Instance);
        await (Task)typeof(AdaptiveAutomationWorker).GetMethod("TickAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(worker, [CancellationToken.None])!;
        var result = await bridge.ExecuteAutomaticCommandAsync(runtime,
            new SetPerformanceModeCommand(Guid.NewGuid(), PerformanceMode.Quiet), CancellationToken.None, saved.Revision);

        Assert.AreEqual(CommandState.Rejected, result.State);
        Assert.AreEqual(HardwareMutation.NoMutation, result.Mutation);
        Assert.AreEqual(0, proxy.Calls);
        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(PersistedFile(fixture)));
    }

    [TestMethod]
    public async Task Pending_store_recovery_stops_native_lighting_observation_without_rewriting_snapshot()
    {
        using var fixture = await Fixture.CreateAsync();
        var saved = await fixture.Store.ReadAsync(CancellationToken.None);
        var pending = new AdaptiveAutomationRecovery(PerformanceMode.Balanced, null, PerformanceMode.Turbo, true, true);
        Assert.IsTrue(await fixture.Store.TryBeginRecoveryAsync(pending, saved.Revision, CancellationToken.None));
        string file = PersistedFile(fixture);
        byte[] before = await File.ReadAllBytesAsync(file);

        await fixture.TickAsync();

        Assert.AreEqual(0, fixture.Provider.NativeModeReads);
        Assert.HasCount(0, fixture.Provider.Commands);
        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(file));
    }

    private static string PersistedFile(Fixture fixture)
    {
        var root = (string?)typeof(Fixture).GetField("root", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture);
        Assert.IsNotNull(root);
        return Path.Combine(root, "adaptive.json");
    }

    public class RecoveryHardwareProxy : DispatchProxy
    {
        public int Calls { get; private set; }
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            // Runtime construction installs lifecycle metadata; every actual hardware operation is forbidden.
            if (method?.Name == nameof(IHomeHardwareProvider.SetBackgroundWorkState)) return null;
            Calls++;
            throw new InvalidOperationException("Unexpected hardware operation while legacy recovery is pending.");
        }
    }
}
