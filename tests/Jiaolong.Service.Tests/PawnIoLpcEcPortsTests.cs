using System.Text;
using Jiaolong.Service.Home;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32.SafeHandles;

namespace Jiaolong.Service.Tests;

[TestClass]
public sealed class PawnIoLpcEcPortsTests
{
    private const string DriverHash = "FCA6E7D58B0CF38DBB913A2B9E532F48629145D395F454B16A9F58E97B8D3940";
    private static readonly PawnIoLpcEcPorts.DriverIdentity Trusted = new("Running", @"C:\Windows\System32\drivers\PawnIO.sys", DriverHash, "valid");

    [TestMethod]
    public void Dependency_preflight_is_read_only_and_rejects_untrusted_or_failed_inspection()
    {
        var method = typeof(PawnIoLpcEcPorts).GetMethod("HasTrustedDependency", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.IsNotNull(method, "The production preflight must use the pinned PawnIO dependency.");
        bool Check(Func<byte[]?> module, Func<PawnIoLpcEcPorts.DriverIdentity?> driver) =>
            (bool)method.Invoke(null, [module, driver])!;
        Assert.IsTrue(Check(ReadModule, () => Trusted));
        foreach (var driver in new PawnIoLpcEcPorts.DriverIdentity?[] { null, Trusted with { State = "Stopped" }, Trusted with { Path = "PawnIO.sys" }, Trusted with { Sha256 = new string('0', 64) }, Trusted with { Authenticode = "unsigned" } })
            Assert.IsFalse(Check(ReadModule, () => driver));
        foreach (byte[]? module in new byte[]?[] { null, new byte[17172], new byte[18076] })
            Assert.IsFalse(Check(() => module, () => Trusted));
        Assert.IsFalse(Check(() => throw new IOException("module inspection failed"), () => Trusted));
        Assert.IsFalse(Check(ReadModule, () => throw new IOException("driver inspection failed")));
        var api = new FakeDeviceApi();
        var currentDriver = Trusted;
        Assert.IsTrue(Check(ReadModule, () => currentDriver));
        using var ports = Create(api, driver: () => currentDriver);
        Assert.AreEqual(0, api.Calls.Count);
        currentDriver = Trusted with { State = "Stopped" };
        using (ports.AcquireBus()) Assert.ThrowsExactly<IOException>(ports.Open);
        Assert.AreEqual(0, api.Calls.Count, "Open must recheck trust after a successful preflight.");
    }

    [TestMethod]
    public void Open_requires_trusted_driver_pinned_module_and_lease_and_exposes_only_slot1_ec_ports()
    {
        var api = new FakeDeviceApi();
        using var ports = Create(api);
        Assert.ThrowsExactly<InvalidOperationException>(ports.Open);
        using (ports.AcquireBus())
        {
            ports.Open(); ports.Open();
            Assert.AreEqual((byte)0x55, ports.ReadByte(0x4F));
            ports.WriteByte(0x4E, 0xFF);
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => ports.ReadByte(0x2E));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => ports.WriteByte(0x2F, 0));
            api.ReadValue = 256;
            Assert.ThrowsExactly<IOException>(() => ports.ReadByte(0x4F));
            api.FailExecute = true;
            Assert.ThrowsExactly<IOException>(() => ports.WriteByte(0x4E, 0));
        }
        CollectionAssert.AreEqual(new[] { "open", "load", "ioctl_select_slot:1:0", "ioctl_pio_inb:79:1", "ioctl_pio_outb:78,255:0", "ioctl_pio_inb:79:1", "ioctl_pio_outb:78,0:0" }, api.Calls);
        Assert.ThrowsExactly<InvalidOperationException>(() => ports.ReadByte(0x4F));
        using var unrelated = new SafeFileHandle(new IntPtr(2), ownsHandle: false);
        ports.Dispose();
        Assert.IsTrue(api.Handle!.IsClosed);
        Assert.IsFalse(unrelated.IsClosed);
        foreach (var identity in new PawnIoLpcEcPorts.DriverIdentity?[] { null, Trusted with { State = "Stopped" }, Trusted with { Path = "PawnIO.sys" }, Trusted with { Sha256 = new string('0', 64) }, Trusted with { Authenticode = "unsigned" } })
        {
            var rejected = new FakeDeviceApi();
            using var candidate = Create(rejected, driver: () => identity);
            using (candidate.AcquireBus()) Assert.ThrowsExactly<IOException>(candidate.Open);
            Assert.AreEqual(0, rejected.Calls.Count);
        }
        foreach (byte[]? module in new byte[]?[] { null, new byte[17172], new byte[18076] })
        {
            var rejected = new FakeDeviceApi();
            using var candidate = Create(rejected, module: () => module);
            using (candidate.AcquireBus()) Assert.ThrowsExactly<IOException>(candidate.Open);
            Assert.AreEqual(0, rejected.Calls.Count);
        }
    }

    [TestMethod]
    public void Bus_rejects_timeout_and_abandonment_and_lifecycle_changes_close_only_candidate_handle()
    {
        string name = "Local\\JiaolongPawnIoTest-" + Guid.NewGuid();
        using var mutex = new Mutex(false, name);
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var owner = new Thread(() => { mutex.WaitOne(); held.Set(); release.Wait(); mutex.ReleaseMutex(); });
        owner.Start();
        Assert.IsTrue(held.Wait(TimeSpan.FromSeconds(5)));
        var api = new FakeDeviceApi();
        using var ports = Create(api, mutexName: name);
        try { Assert.ThrowsExactly<IOException>(() => ports.AcquireBus()); }
        finally { release.Set(); owner.Join(); }
        Assert.AreEqual(0, api.Calls.Count);
        var abandoned = new Thread(() => mutex.WaitOne());
        abandoned.Start(); abandoned.Join();
        Assert.ThrowsExactly<IOException>(() => ports.AcquireBus());
        Assert.IsTrue(mutex.WaitOne(0));
        mutex.ReleaseMutex();
        foreach (string stage in new[] { "entry", "open", "load", "ioctl_select_slot" })
        {
            bool allowed = stage != "entry";
            var interrupted = new FakeDeviceApi { AfterCall = call => { if (call == stage) allowed = false; } };
            using var candidate = Create(interrupted, allowed: () => allowed);
            if (stage == "entry") Assert.ThrowsExactly<IOException>(() => candidate.AcquireBus());
            else
            {
                using (candidate.AcquireBus()) Assert.ThrowsExactly<IOException>(candidate.Open);
                Assert.IsTrue(interrupted.Handle!.IsClosed, stage);
            }
            CollectionAssert.AreEqual(stage switch
            {
                "entry" => Array.Empty<string>(), "open" => new[] { "open" }, "load" => new[] { "open", "load" },
                _ => new[] { "open", "load", "ioctl_select_slot:1:0" }
            }, interrupted.Calls, stage);
        }
        bool current = true;
        var transactionApi = new FakeDeviceApi();
        using var transaction = Create(transactionApi, allowed: () => current);
        using (transaction.AcquireBus())
        {
            transaction.Open();
            current = false;
            // Blding owns complete-register lifecycle boundaries; bytes must finish in progress.
            transaction.WriteByte(0x4E, 0x11);
            Assert.AreEqual((byte)0x55, transaction.ReadByte(0x4F));
        }
        Assert.ThrowsExactly<IOException>(() => transaction.AcquireBus());
    }

    [TestMethod]
    public void Native_execute_preserves_request_layout_and_rejects_short_extra_or_failed_output()
    {
        uint returnedBytes = 8;
        bool success = true;
        byte[]? request = null;
        uint ioctl = 0;
        var api = new PawnIoLpcEcPorts.NativeDeviceApi((SafeFileHandle handle, uint code, byte[] input, byte[]? output, out uint returned) =>
        {
            request = input; ioctl = code; returned = returnedBytes;
            if (output is not null) BitConverter.GetBytes(0x55UL).CopyTo(output, 0);
            return success;
        });
        using var handle = new SafeFileHandle(new IntPtr(1), ownsHandle: false);
        var result = new ulong[1];
        Assert.IsTrue(api.Execute(handle, "ioctl_pio_inb", [0x4F], result));
        Assert.AreEqual(0x55UL, result[0]);
        Assert.AreEqual((41394u << 16) | (0x841u << 2), ioctl);
        Assert.AreEqual(40, request!.Length);
        Assert.AreEqual("ioctl_pio_inb", Encoding.ASCII.GetString(request, 0, 32).TrimEnd('\0'));
        Assert.AreEqual(0x4FUL, BitConverter.ToUInt64(request, 32));
        foreach (uint count in new uint[] { 0, 7, 9 })
        {
            returnedBytes = count; result[0] = 0xAA;
            Assert.IsFalse(api.Execute(handle, "ioctl_pio_inb", [0x4F], result));
            Assert.AreEqual(0xAAUL, result[0]);
        }
        returnedBytes = 8; success = false;
        Assert.IsFalse(api.Execute(handle, "ioctl_pio_inb", [0x4F], result));
        success = true; returnedBytes = 0;
        Assert.IsTrue(api.Execute(handle, "ioctl_pio_outb", [0x4E, 0xFF], null));
        Assert.AreEqual(48, request!.Length);
        returnedBytes = 1;
        Assert.IsFalse(api.Execute(handle, "ioctl_select_slot", [1], null));
    }

    private static PawnIoLpcEcPorts Create(FakeDeviceApi api, Func<bool>? allowed = null,
        Func<byte[]?>? module = null, Func<PawnIoLpcEcPorts.DriverIdentity?>? driver = null, string? mutexName = null) =>
        new(allowed ?? (() => true), api, module ?? ReadModule, driver ?? (() => Trusted),
            mutexName ?? "Local\\JiaolongPawnIoTest-" + Guid.NewGuid(), 20);

    private static byte[] ReadModule()
    {
        using var stream = typeof(Computer).Assembly.GetManifestResourceStream("LibreHardwareMonitor.Resources.PawnIo.LpcIO.bin")!;
        using var buffer = new MemoryStream(); stream.CopyTo(buffer); return buffer.ToArray();
    }

    private sealed class FakeDeviceApi : IPawnIoDeviceApi
    {
        public List<string> Calls { get; } = [];
        public SafeFileHandle? Handle { get; private set; }
        public Action<string>? AfterCall { get; init; }
        public ulong ReadValue { get; set; } = 0x55;
        public bool FailExecute { get; set; }
        public SafeFileHandle Open()
        {
            Calls.Add("open"); AfterCall?.Invoke("open");
            return Handle = new SafeFileHandle(new IntPtr(1), ownsHandle: false);
        }
        public bool LoadModule(SafeFileHandle device, byte[] module)
        { Calls.Add("load"); AfterCall?.Invoke("load"); return true; }
        public bool Execute(SafeFileHandle device, string functionName, ulong[]? input, ulong[]? output)
        {
            Calls.Add($"{functionName}:{string.Join(',', input ?? [])}:{output?.Length ?? 0}");
            if (output is { Length: > 0 }) output[0] = ReadValue;
            AfterCall?.Invoke(functionName); return !FailExecute;
        }
    }
}
