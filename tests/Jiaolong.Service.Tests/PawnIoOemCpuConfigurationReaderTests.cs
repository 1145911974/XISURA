using Jiaolong.Service.Home;
using Microsoft.Win32.SafeHandles;

namespace Jiaolong.Service.Tests;

[TestClass]
public sealed class PawnIoOemCpuConfigurationReaderTests
{
    private static readonly PawnIoLpcEcPorts.DriverIdentity Trusted = new("Running",
        @"C:\Windows\System32\drivers\PawnIO.sys",
        "FCA6E7D58B0CF38DBB913A2B9E532F48629145D395F454B16A9F58E97B8D3940", "valid");

    [TestMethod]
    public void Reads_stable_raw_zero_and_nonzero_configuration_using_only_extended_read_addresses()
    {
        foreach (byte[] values in new byte[][] { [0, 0, 0, 0, 2, 1], [0x80, 85, 110, 95, 1, 2] })
        {
            var api = new FakeApi(values);
            var result = Create(api).Read();
            CollectionAssert.AreEqual(values, new byte[] { result.CustomModeByte, result.SplWatts,
                result.FpptWatts, result.TemperatureLimitC, result.Itsm, result.Stsm });
            Assert.AreEqual(12, api.CompletedReads);
            CollectionAssert.AreEqual(Enumerable.Repeat(new byte[] { 0xF0, 0xF6, 0xF7, 0xF8, 0xE4, 0xF4 }, 2)
                .SelectMany(sample => sample).ToArray(), api.Offsets.ToArray());
            Assert.IsTrue(api.Handle!.IsClosed);
        }
    }

    [TestMethod]
    public void Rejects_untrusted_unstable_failed_and_interrupted_reads_without_partial_lifecycle_transactions()
    {
        foreach (bool badModule in new[] { false, true })
        {
            var api = new FakeApi([0, 0, 0, 0, 0, 0]);
            Assert.ThrowsExactly<IOException>(() => Create(api,
                module: badModule ? () => [1] : null,
                driver: badModule ? null : () => Trusted with { State = "Stopped" }).Read());
            Assert.IsNull(api.Handle);
        }
        foreach (string failure in new[] { "read", "write", "timeout", "pending", "unstable", "load" })
        {
            var api = new FakeApi([0, 0, 0, 0, 0, 0]) { Failure = failure };
            Assert.ThrowsExactly<IOException>(() => Create(api).Read(), failure);
            Assert.IsTrue(api.Handle!.IsClosed, failure);
        }
        foreach (bool cancel in new[] { false, true })
        {
            bool allowed = true;
            using var cancellation = new CancellationTokenSource();
            var api = new FakeApi([0, 0, 0, 0, 0, 0])
            {
                AfterCommand = () => { if (cancel) cancellation.Cancel(); else allowed = false; }
            };
            var reader = Create(api, () => allowed);
            if (cancel) Assert.ThrowsExactly<OperationCanceledException>(() => reader.Read(cancellation.Token));
            else Assert.ThrowsExactly<IOException>(() => reader.Read());
            Assert.AreEqual(1, api.CompletedReads);
            Assert.IsTrue(api.Handle!.IsClosed);
        }
        var untouched = new FakeApi([0, 0, 0, 0, 0, 0]);
        Assert.ThrowsExactly<IOException>(() => Create(untouched, () => false).Read());
        Assert.IsNull(untouched.Handle);
    }

    private static PawnIoOemCpuConfigurationReader Create(FakeApi api, Func<bool>? allowed = null,
        Func<byte[]?>? module = null, Func<PawnIoLpcEcPorts.DriverIdentity?>? driver = null) =>
        new(allowed ?? (() => true), api,
            module ?? (() => PawnIoLpcEcPorts.ReadEmbeddedModule(PawnIoOemCpuConfigurationReader.ModuleName)),
            driver ?? (() => Trusted), "Local\\JiaolongOemConfigurationTest-" + Guid.NewGuid(), 5);

    private sealed class FakeApi(byte[] values) : IPawnIoDeviceApi
    {
        public SafeFileHandle? Handle { get; private set; }
        public string? Failure { get; init; }
        public Action? AfterCommand { get; init; }
        public int CompletedReads { get; private set; }
        public List<byte> Offsets { get; } = [];
        private int phase;

        public SafeFileHandle Open() => Handle = new(new IntPtr(1), ownsHandle: false);
        public bool LoadModule(SafeFileHandle device, byte[] module) => Failure != "load";
        public bool Execute(SafeFileHandle device, string functionName, ulong[]? input, ulong[]? output)
        {
            Assert.IsNotNull(input);
            if (functionName == "ioctl_pio_write")
            {
                if (Failure == "write") return false;
                if (phase == 0)
                {
                    CollectionAssert.AreEqual(new ulong[] { 0x66, 0x92 }, input);
                    phase = 1;
                    AfterCommand?.Invoke();
                }
                else if (phase == 1)
                {
                    CollectionAssert.AreEqual(new ulong[] { 0x62, 0x84 }, input);
                    phase = 2;
                }
                else
                {
                    Assert.AreEqual(2, phase);
                    Assert.AreEqual(0x62UL, input[0]);
                    Offsets.Add(checked((byte)input[1]));
                    phase = 3;
                }
                return true;
            }
            Assert.AreEqual("ioctl_pio_read", functionName);
            Assert.IsNotNull(output);
            if (Failure == "read") return false;
            if (input[0] == 0x66)
                output[0] = Failure == "timeout" ? 2UL : Failure == "pending" || phase == 3 ? 1UL : 0UL;
            else
            {
                Assert.AreEqual(0x62UL, input[0]);
                Assert.AreEqual(3, phase);
                output[0] = values[CompletedReads % 6];
                if (Failure == "unstable" && CompletedReads == 6) output[0] ^= 1;
                CompletedReads++;
                phase = 0;
            }
            return true;
        }
    }
}
