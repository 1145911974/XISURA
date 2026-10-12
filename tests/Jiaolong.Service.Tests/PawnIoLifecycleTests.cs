using Jiaolong.Hardware.Mechrevo.Controls;
using Jiaolong.Service.Home;
using Microsoft.Win32.SafeHandles;

namespace Jiaolong.Service.Tests;

[TestClass]
public sealed class PawnIoLifecycleTests
{
    [TestMethod]
    public void Pause_stops_initialization_table_refresh_sampling_and_alternate_mailbox_at_operation_boundaries()
    {
        foreach (string stage in new[] { "entry", "open", "load", "codeName", "codeNameFailure", "pmResolve", "pmRefresh", "pmRefreshFailure", "pmRefreshFalse", "msrUnit", "pboResult", "fallbackRejection" })
        {
            bool allowed = stage != "entry";
            var api = new FakeDeviceApi { RejectMp1 = stage == "fallbackRejection", FailAfterPause = stage.EndsWith("Failure"), RefreshReturnsFalse = stage == "pmRefreshFalse" };
            string target = stage switch
            {
                "entry" => "unused", "codeName" or "codeNameFailure" => "ioctl_get_code_name",
                "pmResolve" => "ioctl_resolve_pm_table", "pmRefresh" or "pmRefreshFailure" or "pmRefreshFalse" => "ioctl_update_pm_table",
                "msrUnit" => "msr:C0010299", "pboResult" => "smu:03B10A40", "fallbackRejection" => "smu:03B1057C",
                _ => stage
            };
            api.AfterCall = call =>
            {
                if (call != target || stage == "fallbackRejection" && api.Commands.Count == 0) return;
                allowed = false;
                api.Calls.Add("paused");
            };
            using var transport = new PawnIoCurveOptimizerTransport(api, hardwareAccessAllowed: () => allowed);
            Exception? failure = null;
            try
            {
                if (stage.StartsWith("pm")) transport.ReadLimitSnapshot(CancellationToken.None);
                else if (stage == "msrUnit") transport.ReadPackagePower(CancellationToken.None);
                else if (stage == "pboResult") transport.Read(CancellationToken.None);
                else if (stage == "fallbackRejection") transport.WriteAdvancedLimit(new(true, 0x36, 0, 0x35), CancellationToken.None);
                else transport.TryInitialize();
            }
            catch (Exception error) { failure = error; }
            Assert.AreEqual("hardwareAccessPaused", failure?.Message, stage);
            if (stage == "entry") Assert.AreEqual(0, api.Calls.Count);
            else Assert.AreEqual("paused", api.Calls.Last(), "No next operation after pause: " + stage);
            if (stage is "open" or "load" or "codeName" or "codeNameFailure")
                Assert.IsTrue(api.Handles.All(handle => handle.IsClosed), "Interrupted initialization must close its handle: " + stage);
            if (stage == "fallbackRejection") Assert.AreEqual(1, api.Commands.Count, "Pause must not permit the alternate mailbox.");
        }
    }

    [TestMethod]
    public void Recovery_reads_allow_pbo_query_but_no_performance_write_and_revocation_stops_cached_initialization()
    {
        bool readsAllowed = true;
        var api = new FakeDeviceApi();
        using var transport = new PawnIoCurveOptimizerTransport(api, hardwareAccessAllowed: () => false, hardwareReadAllowed: () => readsAllowed);
        Assert.IsTrue(transport.TryInitialize());
        Assert.AreEqual(3, transport.Read(CancellationToken.None));
        Assert.AreEqual(0x6Du, api.Commands.Single());
        int before = api.Calls.Count;
        Assert.Throws<IOException>(() => transport.Write(5, CancellationToken.None));
        Assert.Throws<IOException>(() => transport.WriteAdvancedLimit(new(true, 0x36, 0, 0x35), CancellationToken.None));
        readsAllowed = false;
        Assert.Throws<IOException>(() => transport.TryInitialize());
        Assert.Throws<IOException>(() => transport.ReadLimitSnapshot(CancellationToken.None));
        Assert.Throws<IOException>(() => transport.ReadPackagePower(CancellationToken.None));
        Assert.AreEqual(before, api.Calls.Count);
    }

    [TestMethod]
    public void Initialization_pause_closes_handle_and_retry_uses_a_fresh_handle_without_losing_safe_cleanup()
    {
        bool allowed = true;
        var api = new FakeDeviceApi { AfterCall = call => { if (call == "load") allowed = false; } };
        using var transport = new PawnIoCurveOptimizerTransport(api, hardwareAccessAllowed: () => allowed);
        Assert.Throws<IOException>(() => transport.TryInitialize());
        Assert.IsTrue(api.Handles.Single().IsClosed);
        allowed = true;
        api.AfterCall = null;
        Assert.IsTrue(transport.TryInitialize());
        Assert.AreEqual(2, api.Handles.Count);
        Assert.IsFalse(api.Handles[1].IsClosed);
        allowed = false;
        transport.Dispose();
        Assert.IsTrue(api.Handles.All(handle => handle.IsClosed), "Handle cleanup must remain possible while paused.");
    }

    private sealed class FakeDeviceApi : IPawnIoDeviceApi
    {
        private readonly Dictionary<ulong, ulong> registers = new()
        { [0x03B10570] = 1, [0x03B1057C] = 1 };
        public List<string> Calls { get; } = [];
        public List<SafeFileHandle> Handles { get; } = [];
        public List<uint> Commands { get; } = [];
        public Action<string>? AfterCall { get; set; }
        public bool RejectMp1 { get; init; }
        public bool FailAfterPause { get; init; }
        public bool RefreshReturnsFalse { get; init; }
        private void Called(string call)
        {
            Calls.Add(call);
            AfterCall?.Invoke(call);
            if (FailAfterPause && Calls.Last() == "paused") throw new IOException("simulatedDeviceFailure");
        }
        public SafeFileHandle Open()
        {
            var handle = new SafeFileHandle(new IntPtr(1), ownsHandle: false);
            Handles.Add(handle);
            Called("open");
            return handle;
        }
        public bool LoadModule(SafeFileHandle device, byte[] module) { Called("load"); return true; }
        public bool Execute(SafeFileHandle device, string function, ulong[]? input, ulong[]? output)
        {
            string call = function;
            switch (function)
            {
                case "ioctl_get_code_name": output![0] = 28; break;
                case "ioctl_resolve_pm_table": output![0] = 0x540108; output[1] = 1; break;
                case "ioctl_read_msr": call = $"msr:{input![0]:X8}"; output![0] = 0; break;
                case "ioctl_read_smu_register":
                    call = $"smu:{input![0]:X8}";
                    output![0] = registers.GetValueOrDefault(input[0]);
                    break;
                case "ioctl_write_smu_register":
                    registers[input![0]] = input[1];
                    if (input[0] is 0x03B10524 or 0x03B10530)
                    {
                        Commands.Add((uint)input[1]);
                        registers[input[0] == 0x03B10524 ? 0x03B10570UL : 0x03B1057CUL] =
                            input[0] == 0x03B10530 && RejectMp1 ? 0xFFUL : 1UL;
                        registers[input[0] == 0x03B10524 ? 0x03B10A40UL : 0x03B109C4UL] = 0x40400000;
                    }
                    break;
            }
            Called(call);
            return !(RefreshReturnsFalse && function == "ioctl_update_pm_table");
        }
    }
}
