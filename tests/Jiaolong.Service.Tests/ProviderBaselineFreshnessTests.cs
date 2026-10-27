using System.Reflection;
using Jiaolong.Hardware.Mechrevo.Controls;
using Jiaolong.Service.Home;
using Microsoft.Win32.SafeHandles;

namespace Jiaolong.Service.Tests;

[TestClass]
public sealed class ProviderBaselineFreshnessTests
{
    [TestMethod]
    public void Advanced_baseline_reads_each_refresh_the_real_SMU_reader_without_waiting_for_cache_expiry()
    {
        using var fixture = new CacheFixture();
        Assert.AreEqual(120d, fixture.ReadLimits().Limits.PptWatts);
        fixture.Device.Ppt = 55;
        fixture.ReadBaseline(true);
        Assert.AreEqual(55d, fixture.Observed!.Limits.PptWatts);
        fixture.Device.Ppt = 50;
        fixture.ReadBaseline(true);
        Assert.AreEqual(50d, fixture.Observed!.Limits.PptWatts);
        Assert.AreEqual(3, fixture.Device.TableReads);
    }

    [TestMethod]
    public void Ordinary_controls_and_nonadvanced_baselines_keep_the_existing_SMU_cache()
    {
        using var fixture = new CacheFixture();
        fixture.ReadLimits();
        fixture.Device.Ppt = 55;
        fixture.ReadBaseline(false);
        Assert.AreEqual(120d, fixture.Observed!.Limits.PptWatts);
        Assert.ThrowsExactly<ReadStopped>(() => fixture.Provider.ReadControlsAsync(CancellationToken.None));
        Assert.AreEqual(120d, fixture.Observed!.Limits.PptWatts);
        Assert.AreEqual(1, fixture.Device.TableReads);
    }

    private sealed class CacheFixture : IHomeRadioStateReader, IDisposable
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        public FakeDevice Device { get; } = new();
        public WindowsHomeHardwareProvider Provider { get; }
        public CpuSmuLimitSnapshot? Observed { get; private set; }
        public CacheFixture()
        {
            Provider = new(this);
            typeof(WindowsHomeHardwareProvider).GetField("curveOptimizer", Private)!.SetValue(Provider,
                new PawnIoCurveOptimizerTransport(Device, pciMutexName: "Local\\XisuraFreshness-" + Guid.NewGuid()));
        }
        public CpuSmuLimitSnapshot ReadLimits() => (CpuSmuLimitSnapshot)typeof(WindowsHomeHardwareProvider)
            .GetMethod("ReadSmuLimits", Private)!.Invoke(Provider, [CancellationToken.None])!;
        public void ReadBaseline(bool advanced) => Assert.ThrowsExactly<ReadStopped>(() =>
            Provider.ReadControlBaselineAsync(CancellationToken.None, advanced));
        public Task<HomeRadioSnapshot> ReadAsync(CancellationToken token)
        {
            // Exercise the real baseline entry and cache reader, stopping before unrelated physical devices.
            Observed = ReadLimits();
            throw new ReadStopped();
        }
        public void Dispose() => Provider.Dispose();
    }

    private sealed class ReadStopped : Exception;
    private sealed class FakeDevice : IPawnIoDeviceApi
    {
        public float Ppt { get; set; } = 120;
        public int TableReads { get; private set; }
        public SafeFileHandle Open() => new(new IntPtr(1), ownsHandle: false);
        public bool LoadModule(SafeFileHandle handle, byte[] module) => true;
        public bool Execute(SafeFileHandle handle, string function, ulong[]? input, ulong[]? output)
        {
            if (function == "ioctl_get_code_name") output![0] = 28;
            if (function == "ioctl_resolve_pm_table") { output![0] = 0x540105; output[1] = 1; }
            if (function == "ioctl_read_pm_table")
            {
                TableReads++;
                float[] table = new float[64];
                table[2] = Ppt; table[8] = 108; table[10] = 99; table[61] = 160;
                Buffer.BlockCopy(table, 0, output!, 0, 256);
            }
            return true;
        }
    }
}
