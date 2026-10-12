using Jiaolong.Contracts.Models;
using Jiaolong.Hardware.Abstractions.Compatibility;
using Jiaolong.Hardware.Mechrevo.Controls;
using Jiaolong.Hardware.Mechrevo.Wmi;

namespace Jiaolong.Hardware.Tests;

[TestClass]
public sealed class MiLifecycleTests
{
    private static readonly VerifiedWmiBinding Binding = new("root\\WMI", "MICommonInterface", "fake", 250, 251, 8, "fake");

    [TestMethod]
    public async Task Pause_blocks_mi_operations_before_and_after_dispatch_including_transport_failures()
    {
        foreach (string stage in new[] { "entryRead", "entryWrite", "read", "write", "readFailure", "writeFailure" })
        {
            bool allowed = !stage.StartsWith("entry");
            var bus = new FakeBus { AfterCall = _ => allowed = false, FailAfterCall = stage.EndsWith("Failure") };
            var client = new MiCommonInterfaceClient(bus, bus, () => allowed, () => allowed);
            bool write = stage.Contains("Write") || stage.StartsWith("write");
            Exception? error = await Capture(async () =>
            {
                if (write) await client.WriteOnceAsync(Binding, new byte[] { 2 }, CancellationToken.None);
                else await client.ReadAsync(Binding, CancellationToken.None);
            });
            Assert.AreEqual("hardwareAccessPaused", error?.Message, stage);
            Assert.AreEqual(stage.StartsWith("entry") ? 0 : 1, bus.Calls, stage);
            if (write) Assert.AreEqual(!stage.StartsWith("entry"), error!.Data["miWriteAttempted"], stage);
        }
    }

    [TestMethod]
    public async Task Recovery_read_permission_never_allows_mi_writes_or_bypasses_missing_transport_guards()
    {
        bool readsAllowed = true;
        var bus = new FakeBus();
        var client = new MiCommonInterfaceClient(bus, bus, () => false, () => readsAllowed);
        Assert.AreEqual(DataQuality.Good, (await client.ReadAsync(Binding, CancellationToken.None)).Quality);
        var write = await Capture(async () => await client.WriteOnceAsync(Binding, new byte[] { 2 }, CancellationToken.None));
        Assert.AreEqual("hardwareAccessPaused", write?.Message);
        Assert.AreEqual(false, write!.Data["miWriteAttempted"]);
        Assert.AreEqual(1, bus.Calls);
        readsAllowed = false;
        Assert.AreEqual("hardwareAccessPaused", (await Capture(async () => await client.ReadAsync(Binding, CancellationToken.None)))?.Message);
        var missing = new MiCommonInterfaceClient(hardwareAccessAllowed: () => false);
        Assert.AreEqual("hardwareAccessPaused", (await Capture(async () => await missing.ReadAsync(Binding, CancellationToken.None)))?.Message);
        Assert.AreEqual("hardwareAccessPaused", (await Capture(async () => await missing.WriteOnceAsync(Binding, new byte[] { 2 }, CancellationToken.None)))?.Message);
        var ordinary = new MiCommonInterfaceClient(new FakeBus { FailAfterCall = true });
        Assert.AreEqual("readFailed", (await ordinary.ReadAsync(Binding, CancellationToken.None)).Reason);
    }

    [TestMethod]
    public async Task Lighting_reliable_zero_write_skips_rollback_and_partial_pause_never_dispatches_recovery()
    {
        foreach (string stage in new[] { "baseline", "firstWrite", "partial" })
        {
            bool writeAllowed = true, readsAllowed = true;
            var bus = new FakeBus();
            bus.AfterCall = kind =>
            {
                if (stage == "baseline" && kind == "read") readsAllowed = writeAllowed = false;
                if (stage == "firstWrite" && bus.Reads == 3) writeAllowed = false;
                if (stage == "partial" && kind == "write") readsAllowed = writeAllowed = false;
            };
            var client = new MiCommonInterfaceClient(bus, bus, () => writeAllowed, () => readsAllowed);
            var decision = new CompatibilityDecision(CompatibilityMode.Writable,
                new CapabilitySnapshot([new("keyboardLighting", CapabilityState.Available, null)]),
                new CompatibilityManifest { ManifestId = "fake", WmiProvider = new() { Namespace = "root\\WMI", Class = "MICommonInterface", InstanceName = "fake", ReadType = 250, WriteType = 251 } }, []);
            var error = await Capture(async () => await new MiKeyboardLightingController(client, decision).ApplyAsync(new("Fixed", 50), CancellationToken.None));
            Assert.AreEqual(stage == "partial" ? "rollbackFailed" : "hardwareAccessPaused", error?.Message, stage);
            Assert.AreEqual(stage == "partial", error!.Data["keyboardLightingWriteAttempted"], stage);
            Assert.AreEqual(stage == "partial" ? 1 : 0, bus.Writes, "No native recovery writes: " + stage);
        }
    }

    private static async Task<Exception?> Capture(Func<Task> action)
    {
        try { await action(); return null; }
        catch (Exception error) { return error; }
    }

    private sealed class FakeBus : IMiReadTransport, IMiWriteTransport
    {
        public Action<string>? AfterCall { get; set; }
        public bool FailAfterCall { get; init; }
        public int Reads { get; private set; }
        public int Writes { get; private set; }
        public int Calls => Reads + Writes;
        private readonly Dictionary<byte, byte[]> values = new() { [8] = [1], [16] = [2], [17] = [10, 20, 30], [18] = [1] };
        public Task<byte[]?> ReadAsync(MiReadBinding binding, CancellationToken token)
        {
            Reads++;
            byte[] response = new byte[32];
            values[binding.MethodName].CopyTo(response, 4);
            AfterCall?.Invoke("read");
            if (FailAfterCall) throw new IOException("simulatedReadFailure");
            return Task.FromResult<byte[]?>(response);
        }
        public Task<byte[]?> WriteAsync(VerifiedWmiBinding binding, ReadOnlyMemory<byte> request, CancellationToken token)
        {
            Writes++;
            values[binding.MethodName] = request.Slice(4, binding.MethodName == 17 ? 3 : 1).ToArray();
            AfterCall?.Invoke("write");
            if (FailAfterCall) throw new IOException("simulatedWriteFailure");
            return Task.FromResult<byte[]?>([]);
        }
    }
}
