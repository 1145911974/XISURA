using Jiaolong.Hardware.Mechrevo.Controls;
using System.ComponentModel;

namespace Jiaolong.Hardware.Tests;

[TestClass]
public sealed class WindowsScopedPowerRecoveryTests
{
    [TestMethod]
    public async Task Inactive_scheme_reads_and_single_index_restore_preserve_active_scheme_and_siblings()
    {
        var api = new Api();
        var transport = new WindowsPowerSettingsTransport(api);
        var baseline = await transport.ReadSchemeAsync(Api.Other, CancellationToken.None);
        Assert.AreEqual(Api.Other, baseline.ActiveSchemeId);
        Assert.AreEqual(3900, baseline.AcMaxFrequencyMhz);
        Assert.AreEqual(Api.Main, api.Active);
        Assert.IsTrue(await transport.RestoreSettingAsync(Api.Other, WindowsCpuSetting.AcMaxFrequencyMhz, 3900, 3800, CancellationToken.None));
        var restored = await transport.ReadSchemeAsync(Api.Other, CancellationToken.None);
        Assert.AreEqual(baseline with { AcMaxFrequencyMhz = 3800 }, restored);
        Assert.AreEqual(4700, (await transport.ReadSchemeAsync(Api.Main, CancellationToken.None)).AcMaxFrequencyMhz);
        Assert.AreEqual(Api.Main, api.Active);
        Assert.AreEqual(0, api.Activations);
        Assert.AreEqual(1, api.Writes);
        Assert.IsFalse(await transport.RestoreSettingAsync(Api.Other, WindowsCpuSetting.AcMaxFrequencyMhz, 3900, 3800, CancellationToken.None));
        Assert.AreEqual(1, api.Writes);
    }

    [TestMethod]
    public async Task External_change_cancellation_invalid_value_and_read_only_lifecycle_never_write()
    {
        var api = new Api();
        var transport = new WindowsPowerSettingsTransport(api);
        var conflict = await Assert.ThrowsAsync<InvalidOperationException>(() => transport.RestoreSettingAsync(Api.Other,
            WindowsCpuSetting.AcMaxFrequencyMhz, 3800, 3700, CancellationToken.None));
        Assert.AreEqual(false, conflict.Data["windowsPowerWriteAttempted"]);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => transport.RestoreSettingAsync(Api.Other, WindowsCpuSetting.AcBoostMode, 2, 7, CancellationToken.None));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => transport.RestoreSettingAsync(Api.Other, WindowsCpuSetting.AcBoostMode, 2, 1, cancelled.Token));
        var paused = new WindowsPowerSettingsTransport(api, () => false, () => true);
        await paused.ReadSchemeAsync(Api.Other, CancellationToken.None);
        await Assert.ThrowsAsync<HardwareLifecycleInterruptedException>(() => paused.RestoreSettingAsync(Api.Other, WindowsCpuSetting.AcBoostMode, 2, 1, CancellationToken.None));
        Assert.AreEqual(0, api.Writes);
        Assert.AreEqual(0, api.Activations);
    }

    [TestMethod]
    public async Task Active_restore_refreshes_only_its_scheme_and_partial_failures_preserve_evidence()
    {
        var api = new Api();
        var transport = new WindowsPowerSettingsTransport(api);
        Assert.IsTrue(await transport.RestoreSettingAsync(Api.Main, WindowsCpuSetting.AcBoostMode, 2, 1, CancellationToken.None));
        Assert.AreEqual(1, api.Activations);
        Assert.AreEqual(Api.Main, api.Active);
        api.OnWrite = () => api.Active = Api.Other;
        var changed = await Assert.ThrowsAsync<IOException>(() => transport.RestoreSettingAsync(Api.Main, WindowsCpuSetting.DcBoostMode, 2, 1, CancellationToken.None));
        Assert.AreEqual(true, changed.Data["windowsPowerWriteAttempted"]);
        Assert.AreEqual(Api.Other, api.Active);
        Assert.AreEqual(1, api.Activations);
        api.OnWrite = null; api.IgnoreWrites = true;
        var mismatch = await Assert.ThrowsAsync<IOException>(() => transport.RestoreSettingAsync(Api.Main, WindowsCpuSetting.AcMaxFrequencyMhz, 4700, 4600, CancellationToken.None));
        Assert.AreEqual(true, mismatch.Data["windowsPowerWriteAttempted"]);
        int writes = api.Writes;
        await Assert.ThrowsAsync<Win32Exception>(() => transport.RestoreSettingAsync(Guid.NewGuid(), WindowsCpuSetting.AcBoostMode, 2, 1, CancellationToken.None));
        Assert.AreEqual(writes, api.Writes);
    }

    private sealed class Api : IWindowsPowerSettingsApi
    {
        public static readonly Guid Main = Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e");
        public static readonly Guid Other = Guid.Parse("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
        private static readonly Guid Frequency = Guid.Parse("75b0ae3f-bce0-45a7-8c89-c9611c25e100");
        private static readonly Guid Boost = Guid.Parse("be337238-0d82-4146-a960-4f3749d470c7");
        private static readonly Guid Parking = Guid.Parse("0cc5b647-c1df-4637-891a-dec35c318583");
        private readonly Dictionary<(Guid Scheme, Guid Setting, bool Ac), uint> values = new();
        public Guid Active { get; set; } = Main;
        public int Writes { get; private set; }
        public int Activations { get; private set; }
        public bool IgnoreWrites { get; set; }
        public Action? OnWrite { get; set; }
        public Api()
        {
            foreach (var scheme in new[] { Main, Other }) foreach (bool ac in new[] { true, false })
            { values[(scheme, Frequency, ac)] = scheme == Main ? 4700u : 3900u; values[(scheme, Boost, ac)] = 2; values[(scheme, Parking, ac)] = ac ? 100u : 17u; }
        }
        public Guid ReadActiveScheme() => Active;
        public uint ReadIndex(Guid scheme, Guid setting, bool ac) => values.TryGetValue((scheme, setting, ac), out uint value) ? value : throw new Win32Exception(2);
        public void WriteIndex(Guid scheme, Guid setting, uint value, bool ac)
        { Writes++; if (!IgnoreWrites) values[(scheme, setting, ac)] = value; OnWrite?.Invoke(); }
        public void SetActiveScheme(Guid scheme) { Activations++; Active = scheme; }
        public uint ReadFriendlyName(Guid scheme, byte[]? buffer, ref uint size)
        { size = 2; if (buffer is not null) { buffer[0] = 0; buffer[1] = 0; } return 0; }
    }
}
