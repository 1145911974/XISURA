using System.ComponentModel;
using System.Text;
using Jiaolong.Hardware.Mechrevo.Controls;

namespace Jiaolong.Hardware.Tests;

[TestClass]
public sealed class WindowsPowerLifecycleTests
{
    private static readonly WindowsPowerSettingsSnapshot Original = new(
        Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e"), 4800, 3400, 3, 4)
    { AcMinActiveCoresPercent = 100, DcMinActiveCoresPercent = 50 };

    [TestMethod]
    public void Pause_between_native_power_calls_stops_following_reads_writes_activation_and_recovery()
    {
        foreach (var (name, operation) in WriteOperations())
        foreach (bool failCall in new[] { false, true })
        {
            bool allowed = true;
            var api = new FakeApi { AfterCall = call => { if (call.StartsWith("write") || call == "activate") allowed = false; }, FailCall = failCall };
            var transport = new WindowsPowerSettingsTransport(api, () => allowed);
            AssertInterrupted(() => operation(transport), name);
            Assert.IsTrue(api.Calls.Last().StartsWith("write") || api.Calls.Last() == "activate", name);
            Assert.AreEqual(1, api.Calls.Count(call => call.StartsWith("write") || call == "activate"), name);
        }
        foreach (string stage in new[] { "active", "parking-ac", "name-size", "name-buffer" })
        {
            bool allowed = true;
            var api = new FakeApi { AfterCall = call => { if (call == stage) allowed = false; }, FailCall = stage == "parking-ac" };
            var transport = new WindowsPowerSettingsTransport(api, () => allowed);
            AssertInterrupted(() => transport.ReadAsync(CancellationToken.None), stage);
            Assert.AreEqual(stage, api.Calls.Last(), stage);
        }
    }

    [TestMethod]
    public async Task Diagnostic_read_permission_does_not_allow_any_power_write_or_restore()
    {
        bool readsAllowed = true;
        var api = new FakeApi();
        var transport = new WindowsPowerSettingsTransport(api, () => false, () => readsAllowed);
        var state = await transport.ReadAsync(CancellationToken.None);
        Assert.AreEqual(4800, state.AcMaxFrequencyMhz);
        Assert.AreEqual(3400, state.DcMaxFrequencyMhz);
        Assert.AreEqual(3, state.AcBoostMode);
        Assert.AreEqual(4, state.DcBoostMode);
        Assert.AreEqual(100, state.AcMinActiveCoresPercent);
        Assert.AreEqual(50, state.DcMinActiveCoresPercent);
        Assert.AreEqual("Test", state.ActiveSchemeName);
        int before = api.Calls.Count;
        foreach (var (name, operation) in WriteOperations()) AssertInterrupted(() => operation(transport), name);
        readsAllowed = false;
        AssertInterrupted(() => transport.ReadAsync(CancellationToken.None), "revokedRead");
        Assert.AreEqual(before, api.Calls.Count, "Rejected operations must not start a native call.");
    }

    private static IEnumerable<(string Name, Func<WindowsPowerSettingsTransport, Task> Operation)> WriteOperations()
    {
        yield return ("frequency", transport => transport.SetMaxFrequencyAsync(4200, 2800, CancellationToken.None));
        yield return ("boost", transport => transport.SetBoostAsync(false, CancellationToken.None));
        yield return ("parking", transport => transport.SetCoreParkingAsync(80, 40, CancellationToken.None));
        yield return ("scheme", transport => transport.SetSchemeAsync(Original.ActiveSchemeId, CancellationToken.None));
        foreach (var field in new[] { CpuTuningField.MaxFrequencyMhz, CpuTuningField.BoostEnabled,
                     CpuTuningField.CoreParkingPercent, CpuTuningField.WindowsPowerSchemeId })
            yield return ("restore:" + field, transport => transport.RestoreAsync(field, Original, CancellationToken.None));
    }

    private static void AssertInterrupted(Func<Task> operation, string stage)
    {
        Exception? failure = null;
        try { operation().GetAwaiter().GetResult(); }
        catch (Exception error) { failure = error; }
        Assert.IsNotNull(failure, stage);
        Assert.AreEqual("hardwareAccessPaused", failure.Message, stage);
    }

    private sealed class FakeApi : IWindowsPowerSettingsApi
    {
        public List<string> Calls { get; } = [];
        public Action<string>? AfterCall { get; init; }
        public bool FailCall { get; init; }
        private void Called(string call)
        {
            Calls.Add(call);
            AfterCall?.Invoke(call);
            if (FailCall && (call.StartsWith("write") || call is "activate" or "parking-ac"))
                throw new Win32Exception(5, "simulatedNativeFailure");
        }
        public Guid ReadActiveScheme() { Called("active"); return Original.ActiveSchemeId; }
        public uint ReadIndex(Guid scheme, Guid setting, bool ac)
        {
            string prefix = setting == Guid.Parse("0cc5b647-c1df-4637-891a-dec35c318583") ? "parking" :
                setting == Guid.Parse("75b0ae3f-bce0-45a7-8c89-c9611c25e100") ? "frequency" : "boost";
            Called(prefix + (ac ? "-ac" : "-dc"));
            return prefix switch { "parking" => ac ? 100u : 50u, "frequency" => ac ? 4800u : 3400u, _ => ac ? 3u : 4u };
        }
        public void WriteIndex(Guid scheme, Guid setting, uint value, bool ac) => Called(ac ? "write-ac" : "write-dc");
        public void SetActiveScheme(Guid scheme) => Called("activate");
        public uint ReadFriendlyName(Guid scheme, byte[]? buffer, ref uint size)
        {
            Called(buffer is null ? "name-size" : "name-buffer");
            byte[] name = Encoding.Unicode.GetBytes("Test\0");
            size = (uint)name.Length;
            if (buffer is not null) name.CopyTo(buffer, 0);
            return 0;
        }
    }
}
