using System.Reflection;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong.Contracts.Errors;
using Jiaolong.Hardware.Abstractions.Compatibility;
using Jiaolong.Hardware.Mechrevo.Wmi;
using Jiaolong.Service.Home;

namespace Jiaolong.Service.Tests;

[TestClass]
public sealed class MiProviderLifecycleTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [TestMethod]
    public void Direct_mi_baseline_first_write_and_partial_pause_preserve_reliable_mutation_classification()
    {
        foreach (string stage in new[] { "baseline", "firstWrite", "partial" })
        {
            bool writesAllowed = true, readsAllowed = true;
            var bus = new FakeBus(kind =>
            {
                if (stage == "baseline" || stage == "partial" && kind == "write") readsAllowed = writesAllowed = false;
                if (stage == "firstWrite" && kind == "read") writesAllowed = false;
            });
            using var provider = new WindowsHomeHardwareProvider();
            typeof(WindowsHomeHardwareProvider).GetField("miInterface", Private)!.SetValue(provider,
                new MiCommonInterfaceClient(bus, bus, () => writesAllowed, () => readsAllowed));
            typeof(WindowsHomeHardwareProvider).GetField("compatibilityDecision", Private)!.SetValue(provider,
                new CompatibilityDecision(CompatibilityMode.Writable,
                    new CapabilitySnapshot([new("performanceMode", CapabilityState.Available, null)]),
                    new CompatibilityManifest { ManifestId = "fake", WmiProvider = new() { Namespace = "root\\WMI", Class = "MICommonInterface", InstanceName = "fake", ReadType = 250, WriteType = 251 } }, []));
            CommandResult? result = null;
            Exception? error = Capture(() => result = (CommandResult)typeof(WindowsHomeHardwareProvider).GetMethod("ExecuteLocked", Private)!
                .Invoke(provider, [new SetPerformanceModeCommand(Guid.NewGuid(), PerformanceMode.Balanced), CancellationToken.None])!);
            Assert.IsNull(error, stage);
            Assert.AreEqual(stage == "partial" ? CommandState.RecoveryRequired : CommandState.Rejected, result?.State, stage);
            Assert.AreEqual(stage == "partial" ? HardwareMutation.Unknown : HardwareMutation.NoMutation, result?.Mutation, stage);
            Assert.AreEqual(stage == "partial" ? 1 : 0, bus.Writes, "No recovery dispatch: " + stage);
            Assert.AreEqual((stage == "partial").ToString(), result!.Error!.Details["writeAttempted"]);
            Assert.IsNull(result.VerifiedPerformanceMode);
        }
    }

    [TestMethod]
    public void Paused_dispose_and_fan_release_keep_pending_snapshots_even_inside_recovery_read_scope()
    {
        foreach (bool recoveryScope in new[] { false, true })
        {
            var provider = new WindowsHomeHardwareProvider();
            var lease = (LightingPreviewLease)typeof(WindowsHomeHardwareProvider).GetField("lightingPreview", Private)!.GetValue(provider)!;
            lease.Begin(new("Fixed", 50), DateTimeOffset.UtcNow);
            var cooling = typeof(WindowsHomeHardwareProvider).GetField("strongCoolingActive", Private)!;
            cooling.SetValue(provider, true); // No EC transport, write ownership or OEM baseline is injected.
            IDisposable? scope = null;
            if (recoveryScope)
            {
                var lifecycle = new PowerLifecycleState(provider);
                scope = lifecycle.AllowRecoveryReads(lifecycle.Pause());
            }
            else provider.SetBackgroundWorkPaused(true);
            try
            {
                var error = Capture(() => typeof(WindowsHomeHardwareProvider).GetMethod("ReleaseFanLocked", Private)!.Invoke(provider, null));
                Assert.AreEqual("hardwareAccessPaused", error?.Message);
                Assert.AreEqual(true, cooling.GetValue(provider));
                provider.Dispose();
                Assert.AreEqual(true, cooling.GetValue(provider), "Dispose must not clear unconfirmed fan ownership.");
                Assert.IsNotNull(lease.Original);
                Assert.IsNull(typeof(WindowsHomeHardwareProvider).GetField("fanTimer", Private)!.GetValue(provider));
                Assert.IsNull(typeof(WindowsHomeHardwareProvider).GetField("lightingTimer", Private)!.GetValue(provider));
                provider.Dispose();
            }
            finally
            {
                scope?.Dispose();
                lease.Complete();
                cooling.SetValue(provider, false);
                provider.Dispose();
            }
        }
    }

    [TestMethod]
    public void Lighting_zero_write_cancellation_returns_no_mutation_but_written_cancellation_stays_distinct()
    {
        foreach (string stage in new[] { "baseline", "firstWrite", "partial" })
        {
            using var cancellation = new CancellationTokenSource();
            int reads = 0;
            var bus = new FakeBus(kind =>
            {
                if (kind == "read") reads++;
                if (stage == "baseline" && reads == 1 || stage == "firstWrite" && reads == 3 || stage == "partial" && kind == "write")
                    cancellation.Cancel();
            });
            using var provider = new WindowsHomeHardwareProvider(() => false);
            typeof(WindowsHomeHardwareProvider).GetField("miInterface", Private)!.SetValue(provider, new MiCommonInterfaceClient(bus, bus));
            typeof(WindowsHomeHardwareProvider).GetField("compatibilityDecision", Private)!.SetValue(provider,
                new CompatibilityDecision(CompatibilityMode.Writable,
                    new CapabilitySnapshot([new("keyboardLighting", CapabilityState.Available, null)]),
                    new CompatibilityManifest { ManifestId = "fake", WmiProvider = new() { Namespace = "root\\WMI", Class = "MICommonInterface", InstanceName = "fake", ReadType = 250, WriteType = 251 } }, []));
            CommandResult? result = null;
            var error = Capture(() => result = (CommandResult)typeof(WindowsHomeHardwareProvider).GetMethod("ExecuteLightingLocked", Private)!
                .Invoke(provider, [new SetKeyboardLightingCommand(Guid.NewGuid(), new("Fixed", 50)), cancellation.Token])!);
            if (stage == "partial")
            {
                Assert.IsInstanceOfType<OperationCanceledException>(error);
                Assert.AreEqual(true, error!.Data["keyboardLightingWriteAttempted"]);
                Assert.IsTrue(bus.Writes > 0);
            }
            else
            {
                Assert.IsNull(error, stage);
                Assert.AreEqual(CommandState.Rejected, result?.State, stage);
                Assert.AreEqual(HardwareMutation.NoMutation, result?.Mutation, stage);
                Assert.AreEqual(ErrorCode.CancelledBeforeApply, result?.Error?.Code, stage);
                Assert.AreEqual(0, bus.Writes, stage);
            }
        }
    }

    private static Exception? Capture(Action action)
    {
        try { action(); return null; }
        catch (TargetInvocationException error) { return error.InnerException; }
        catch (Exception error) { return error; }
    }

    private sealed class FakeBus(Action<string> afterCall) : IMiReadTransport, IMiWriteTransport
    {
        private byte value = 1;
        private readonly Dictionary<byte, byte[]> lighting = new() { [16] = [2], [17] = [10, 20, 30], [18] = [1] };
        public int Writes { get; private set; }
        public Task<byte[]?> ReadAsync(MiReadBinding binding, CancellationToken token)
        {
            byte[] response = new byte[32];
            response[4] = value;
            if (lighting.TryGetValue(binding.MethodName, out var bytes)) bytes.CopyTo(response, 4);
            afterCall("read");
            return Task.FromResult<byte[]?>(response);
        }
        public Task<byte[]?> WriteAsync(VerifiedWmiBinding binding, ReadOnlyMemory<byte> request, CancellationToken token)
        {
            Writes++;
            value = request.Span[4];
            if (lighting.ContainsKey(binding.MethodName)) lighting[binding.MethodName] = request.Slice(4, binding.MethodName == 17 ? 3 : 1).ToArray();
            afterCall("write");
            return Task.FromResult<byte[]?>([]);
        }
    }
}
