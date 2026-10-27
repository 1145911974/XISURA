using Jiaolong.Service.Home;
using Microsoft.Win32.SafeHandles;

namespace Jiaolong.Service.Tests;

[TestClass]
[DoNotParallelize] // One test intentionally holds the production name; do not block unrelated fake transports.
public sealed class PawnIoPciMutexTests
{
    [TestMethod]
    public void Default_transport_refuses_PCI_contention_before_resolving_the_PM_table()
    {
        using var mutex = new Mutex(false, @"Global\Access_PCI");
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var owner = new Thread(() =>
        {
            if (!mutex.WaitOne(3000)) return;
            try { held.Set(); release.Wait(); }
            finally { mutex.ReleaseMutex(); }
        });
        owner.Start();
        var api = new FakeDeviceApi();
        using var transport = new PawnIoCurveOptimizerTransport(api);
        try
        {
            Assert.IsTrue(held.Wait(TimeSpan.FromSeconds(5)));
            Assert.ThrowsExactly<IOException>(() => transport.ReadLimitSnapshot(CancellationToken.None));
            Assert.AreEqual(0, api.PciCalls);
        }
        finally { release.Set(); owner.Join(); }
    }

    [TestMethod]
    public void PM_snapshot_and_mailbox_commands_hold_the_shared_mutex_and_release_on_failure()
    {
        string name = "Local\\XisuraPci-" + Guid.NewGuid();
        var api = new FakeDeviceApi { BeforePci = () => Assert.IsFalse(AvailableOnOtherThread(name)) };
        using var transport = new PawnIoCurveOptimizerTransport(api, pciMutexName: name);
        Assert.AreEqual(65d, transport.ReadLimitSnapshot(CancellationToken.None).Limits.PptWatts);
        Assert.AreEqual(3, transport.Read(CancellationToken.None));
        transport.Write(2, CancellationToken.None);
        api.RejectMp1 = true;
        transport.WriteAdvancedLimit(new(true, 0x36, 99000, 0x35), CancellationToken.None);
        CollectionAssert.Contains(api.Commands, 0x35u);
        Assert.IsTrue(AvailableOnOtherThread(name));
        api.Failure = "ioctl_read_pm_table";
        Assert.ThrowsExactly<IOException>(() => transport.ReadLimitSnapshot(CancellationToken.None));
        Assert.IsTrue(AvailableOnOtherThread(name));
    }

    [TestMethod]
    public void Timeout_and_cancellation_do_not_dispatch_PCI_reads_or_writes()
    {
        string name = "Local\\XisuraPci-" + Guid.NewGuid();
        using var mutex = new Mutex(false, name);
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var owner = new Thread(() =>
        {
            mutex.WaitOne();
            try { held.Set(); release.Wait(); }
            finally { mutex.ReleaseMutex(); }
        });
        owner.Start();
        var api = new FakeDeviceApi();
        using var transport = new PawnIoCurveOptimizerTransport(api, pciMutexName: name);
        try
        {
            Assert.IsTrue(held.Wait(TimeSpan.FromSeconds(5)));
            Action[] operations = [
                () => transport.Read(CancellationToken.None),
                () => transport.Write(2, CancellationToken.None),
                () => transport.WriteAdvancedLimit(new(true, 0x36, 99000), CancellationToken.None),
                () => transport.ReadPerCore(CancellationToken.None),
                () => transport.WritePerCore(new Dictionary<int, int> { [0] = -49 }, CancellationToken.None),
                () => transport.RestorePerCore(Enumerable.Range(0, 8).ToDictionary(core => core, _ => -50), CancellationToken.None)
            ];
            foreach (var operation in operations)
                Assert.AreEqual("cpuSmuPciMutexTimeout", Assert.ThrowsExactly<IOException>(operation).Message);
            using var cancelled = new CancellationTokenSource();
            cancelled.CancelAfter(30);
            Assert.ThrowsExactly<OperationCanceledException>(() => transport.ReadLimitSnapshot(cancelled.Token));
            Assert.AreEqual(0, api.PciCalls);
        }
        finally { release.Set(); owner.Join(); }
        Assert.AreEqual(65d, transport.ReadLimitSnapshot(CancellationToken.None).Limits.PptWatts);
    }

    [TestMethod]
    public void Abandoned_mutex_is_rejected_without_IO_and_released_for_a_fresh_attempt()
    {
        string name = "Local\\XisuraPci-" + Guid.NewGuid();
        using var mutex = new Mutex(false, name);
        var owner = new Thread(() => mutex.WaitOne());
        owner.Start(); owner.Join();
        var api = new FakeDeviceApi();
        using var transport = new PawnIoCurveOptimizerTransport(api, pciMutexName: name);
        Assert.AreEqual("cpuSmuPciMutexAbandoned",
            Assert.ThrowsExactly<IOException>(() => transport.ReadLimitSnapshot(CancellationToken.None)).Message);
        Assert.AreEqual(0, api.PciCalls);
        Assert.IsTrue(AvailableOnOtherThread(name));
        Assert.AreEqual(65d, transport.ReadLimitSnapshot(CancellationToken.None).Limits.PptWatts);
    }

    [TestMethod]
    public void CO_partial_write_and_exact_rollback_keep_one_PCI_lease_through_confirmation()
    {
        string name = "Local\\XisuraPci-" + Guid.NewGuid();
        var values = Enumerable.Range(0, 8).ToDictionary(core => core, _ => -50);
        bool fail = false;
        using var transport = new PawnIoCurveOptimizerTransport((command, argument) =>
        {
            Assert.IsFalse(AvailableOnOtherThread(name));
            int core = (int)(argument >> 20);
            if (command == 0xD5) return unchecked((ushort)(short)values[core]);
            values[core] = unchecked((short)argument);
            if (fail) { fail = false; throw new IOException("partialWrite"); }
            return 1;
        }, pciMutexName: name);
        Assert.AreEqual(8, transport.ReadPerCore(CancellationToken.None).Count);
        transport.WritePerCore(new Dictionary<int, int> { [0] = -49 }, CancellationToken.None);
        transport.RestorePerCore(Enumerable.Range(0, 8).ToDictionary(core => core, _ => -50), CancellationToken.None);
        fail = true;
        var error = Assert.ThrowsExactly<IOException>(() =>
            transport.WritePerCore(new Dictionary<int, int> { [0] = -49 }, CancellationToken.None));
        Assert.AreEqual(false, error.Data["curveOptimizerRollbackFailed"]);
        Assert.IsTrue(values.Values.All(value => value == -50));
        Assert.IsTrue(AvailableOnOtherThread(name));
    }

    private static bool AvailableOnOtherThread(string name)
    {
        bool available = false;
        var observer = new Thread(() =>
        {
            using var mutex = new Mutex(false, name);
            available = mutex.WaitOne(0);
            if (available) mutex.ReleaseMutex();
        });
        observer.Start();
        Assert.IsTrue(observer.Join(TimeSpan.FromSeconds(5)));
        return available;
    }

    private sealed class FakeDeviceApi : IPawnIoDeviceApi
    {
        private readonly Dictionary<ulong, ulong> registers = new()
        { [0x03B10570] = 1, [0x03B1057C] = 1 };
        public int PciCalls { get; private set; }
        public Action? BeforePci { get; init; }
        public string? Failure { get; set; }
        public bool RejectMp1 { get; set; }
        public List<uint> Commands { get; } = [];
        public SafeFileHandle Open() => new(new IntPtr(1), ownsHandle: false);
        public bool LoadModule(SafeFileHandle handle, byte[] module) => true;
        public bool Execute(SafeFileHandle handle, string function, ulong[]? input, ulong[]? output)
        {
            if (function == "ioctl_get_code_name") { output![0] = 28; return true; }
            BeforePci?.Invoke();
            PciCalls++;
            if (Failure == function) throw new IOException("simulatedReadFailure");
            if (function == "ioctl_resolve_pm_table") { output![0] = 0x540105; output[1] = 1; }
            if (function == "ioctl_read_pm_table")
            {
                float[] table = new float[64];
                table[2] = 65; table[8] = 108; table[10] = 99; table[61] = 160;
                Buffer.BlockCopy(table, 0, output!, 0, 256);
            }
            if (function == "ioctl_read_smu_register") output![0] = registers.GetValueOrDefault(input![0]);
            if (function == "ioctl_write_smu_register")
            {
                registers[input![0]] = input[1];
                if (input[0] is 0x03B10524 or 0x03B10530)
                {
                    Commands.Add((uint)input[1]);
                    registers[input[0] == 0x03B10524 ? 0x03B10570UL : 0x03B1057CUL] =
                        input[0] == 0x03B10530 && RejectMp1 ? 0xFFUL : 1UL;
                    registers[input[0] == 0x03B10524 ? 0x03B10A40UL : 0x03B109C4UL] = 0x40400000;
                }
            }
            return true;
        }
    }
}
