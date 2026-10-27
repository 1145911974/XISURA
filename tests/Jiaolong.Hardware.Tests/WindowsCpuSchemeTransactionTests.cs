using Jiaolong.Contracts.Commands;
using Jiaolong.Hardware.Mechrevo.Controls;

namespace Jiaolong.Hardware.Tests;

[TestClass]
public sealed class WindowsCpuSchemeTransactionTests
{
    private static CpuTuningPlan Plan(Guid scheme, int frequency) => new(null, null, null, null, false, null, scheme, null)
        { AcMaxFrequencyMhz = frequency, DcMaxFrequencyMhz = frequency - 100,
          AcMinActiveCoresPercent = 40, DcMinActiveCoresPercent = 20 };

    private static (WindowsCpuTuningTransport Transport, PerformanceController Controller) Create(Api api)
    {
        var power = new WindowsPowerSettingsTransport(api, hardwareAccessAllowed: () => api.Allowed);
        var transport = new WindowsCpuTuningTransport(null!, null!, power, hardwareAccessAllowed: () => api.Allowed);
        return (transport, new(cpuTransport: transport, hardwareAccessAllowed: () => api.Allowed));
    }

    private static Task<CpuTuningTransactionResult> FailBatch(PerformanceController controller) =>
        controller.ApplyCpuTuningBatchAsync([Plan(Api.Main, 4500), Plan(Api.Other, 3700), Plan(Api.Main, 4400)], true, CancellationToken.None);

    [TestMethod]
    public async Task Windows_scheme_transaction_restores_all_touched_indices_and_original_selection_on_failure()
    {
        foreach (int failAt in new[] { 7, 13 })
        {
            var api = new Api { FailAtWrite = failAt };
            var before = api.Values.ToDictionary(pair => pair.Key, pair => pair.Value);
            var result = await FailBatch(Create(api).Controller);
            Assert.IsTrue(api.FailureTriggered);
            Assert.AreEqual(CommandState.RolledBack, result.State, result.Error?.Details.GetValueOrDefault("cause"));
            CollectionAssert.AreEquivalent(before.ToArray(), api.Values.ToArray(), "Every affected scheme, not just the last selected one.");
            Assert.AreEqual(Api.Main, api.Active);
        }
    }

    [TestMethod]
    public async Task Windows_scheme_transaction_preserves_external_index_and_reports_recovery_required()
    {
        var api = new Api { FailAtWrite = 7 };
        api.OnFailure = () => api.Values[(Api.Main, Api.Frequency, false)] = 4123;
        var result = await FailBatch(Create(api).Controller);
        Assert.AreEqual(CommandState.RecoveryRequired, result.State);
        Assert.AreEqual(4123u, api.Values[(Api.Main, Api.Frequency, false)]);
        Assert.AreEqual(4700u, api.Values[(Api.Main, Api.Frequency, true)]);
        Assert.AreEqual(3900u, api.Values[(Api.Other, Api.Frequency, true)]);
    }

    [TestMethod]
    public async Task Windows_scheme_transaction_detects_ignored_restore_and_retries_original_snapshots()
    {
        var api = new Api { FailAtWrite = 7, IgnoreOriginalMainFrequency = true };
        var before = api.Values.ToDictionary(pair => pair.Key, pair => pair.Value);
        var pipeline = Create(api);
        Assert.AreEqual(CommandState.RecoveryRequired, (await FailBatch(pipeline.Controller)).State);
        api.IgnoreOriginalMainFrequency = false;
        await pipeline.Transport.RestoreTransactionAsync(CancellationToken.None);
        CollectionAssert.AreEquivalent(before.ToArray(), api.Values.ToArray());
        Assert.AreEqual(Api.Main, api.Active);
    }

    [TestMethod]
    public async Task Windows_scheme_transaction_pause_stops_recovery_calls_and_keeps_original_snapshots()
    {
        var api = new Api { FailAtWrite = 7, PauseOnRestore = true };
        var before = api.Values.ToDictionary(pair => pair.Key, pair => pair.Value);
        var pipeline = Create(api);
        Assert.AreEqual(CommandState.RecoveryRequired, (await FailBatch(pipeline.Controller)).State);
        Assert.IsFalse(api.Allowed);
        Assert.AreEqual("paused", api.Calls.Last(), "No native API call may follow the lifecycle pause.");
        api.Allowed = true; api.PauseOnRestore = false;
        await pipeline.Transport.RestoreTransactionAsync(CancellationToken.None);
        CollectionAssert.AreEquivalent(before.ToArray(), api.Values.ToArray());
        Assert.AreEqual(Api.Main, api.Active);
    }

    [TestMethod]
    public async Task Native_prepare_failure_preserves_unowned_windows_changes_and_keeps_recovery_pending()
    {
        foreach (bool changeSelection in new[] { false, true })
        {
            var api = new Api();
            var pipeline = Create(api);
            var result = await pipeline.Controller.ApplyCpuTuningBatchAsync(
                [Plan(Api.Main, 4500)], true, CancellationToken.None,
                _ =>
                {
                    if (changeSelection) api.SetActiveScheme(Api.Other);
                    else api.Values[(Api.Main, Api.Frequency, true)] = 4123;
                    api.Calls.Clear();
                    throw new IOException("nativePrepareFailed");
                }, _ => Task.CompletedTask, _ => Task.FromResult(true));
            Assert.AreEqual(CommandState.RecoveryRequired, result.State);
            Assert.IsFalse(result.HardwareReadBackConfirmed);
            Assert.AreEqual(changeSelection ? Api.Other : Api.Main, api.Active);
            Assert.AreEqual(changeSelection ? 4700u : 4123u, api.Values[(Api.Main, Api.Frequency, true)]);
            Assert.IsFalse(api.Calls.Contains("write") || api.Calls.Contains("select"));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => pipeline.Transport.RestoreTransactionAsync(CancellationToken.None));
            Assert.IsFalse(api.Calls.Contains("write") || api.Calls.Contains("select"));
            // The original capture remains pending until the external change has been resolved.
            api.SetActiveScheme(Api.Main);
            api.Values[(Api.Main, Api.Frequency, true)] = 4700;
            await pipeline.Transport.RestoreTransactionAsync(CancellationToken.None);
        }
    }

    [TestMethod]
    public async Task Native_rollback_restores_owned_writes_without_claiming_intervening_windows_drift()
    {
        foreach (string drift in new[] { "none", "prepare-index", "restore-index", "prepare-selection" })
        {
            var api = new Api();
            var before = api.Values.ToDictionary(pair => pair.Key, pair => pair.Value);
            var result = await Create(api).Controller.ApplyCpuTuningBatchAsync(
                [Plan(Api.Main, 4500)], true, CancellationToken.None,
                _ =>
                {
                    if (drift == "prepare-index") api.Values[(Api.Main, Api.Frequency, false)] = 4123;
                    if (drift == "prepare-selection") api.SetActiveScheme(Api.Other);
                    return Task.CompletedTask;
                },
                _ =>
                {
                    if (drift == "restore-index") api.Values[(Api.Main, Api.Frequency, false)] = 4123;
                    return Task.CompletedTask;
                }, _ => Task.FromResult(false));
            Assert.AreEqual(drift is "prepare-index" or "restore-index" ? 4123u : 4700u,
                api.Values[(Api.Main, Api.Frequency, false)], drift);
            Assert.AreEqual(drift == "prepare-selection" ? Api.Other : Api.Main, api.Active, drift);
            Assert.AreEqual(drift == "none" ? CommandState.RolledBack : CommandState.RecoveryRequired, result.State, drift);
            foreach (var pair in before.Where(pair => pair.Key != (Api.Main, Api.Frequency, false)))
                Assert.AreEqual(pair.Value, api.Values[pair.Key], pair.Key.ToString());
        }
    }

    [TestMethod]
    public async Task Later_explicit_write_rejects_unowned_drift_before_overwriting_it()
    {
        var api = new Api();
        bool secondWrite = false;
        api.AfterWrite = (setting, ac, value) =>
        {
            if (setting == Api.Parking && !ac && value == 20)
                api.Values[(Api.Main, Api.Frequency, false)] = 4123;
            if (setting == Api.Frequency && ac && value == 4300) secondWrite = true;
        };
        var result = await Create(api).Controller.ApplyCpuTuningBatchAsync(
            [Plan(Api.Main, 4500), Plan(Api.Main, 4300) with { DcMaxFrequencyMhz = null }], true, CancellationToken.None);
        Assert.IsFalse(secondWrite, "Reject unowned drift before the second partial write reaches Windows.");
        Assert.AreEqual(4123u, api.Values[(Api.Main, Api.Frequency, false)]);
        Assert.AreEqual(4700u, api.Values[(Api.Main, Api.Frequency, true)]);
        Assert.AreEqual(CommandState.RecoveryRequired, result.State);
    }

    private sealed class Api : IWindowsPowerSettingsApi
    {
        public static readonly Guid Main = Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e");
        public static readonly Guid Other = Guid.Parse("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
        public static readonly Guid Frequency = Guid.Parse("75b0ae3f-bce0-45a7-8c89-c9611c25e100");
        private static readonly Guid Boost = Guid.Parse("be337238-0d82-4146-a960-4f3749d470c7");
        public static readonly Guid Parking = Guid.Parse("0cc5b647-c1df-4637-891a-dec35c318583");
        public Dictionary<(Guid Scheme, Guid Setting, bool Ac), uint> Values { get; } = [];
        public Guid Active { get; private set; } = Main;
        public int FailAtWrite { get; init; }
        public Action? OnFailure { get; set; }
        public Action<Guid, bool, uint>? AfterWrite { get; set; }
        public bool IgnoreOriginalMainFrequency { get; set; }
        public bool PauseOnRestore { get; set; }
        public bool Allowed { get; set; } = true;
        public List<string> Calls { get; } = [];
        private int writes;
        private bool failed;
        public bool FailureTriggered => failed;
        public Api()
        {
            foreach (var scheme in new[] { Main, Other }) foreach (bool ac in new[] { true, false })
            {
                Values[(scheme, Frequency, ac)] = scheme == Main ? 4700u : ac ? 3900u : 3300u;
                Values[(scheme, Boost, ac)] = scheme == Main ? ac ? 2u : 4u : ac ? 3u : 1u;
                Values[(scheme, Parking, ac)] = scheme == Main ? ac ? 100u : 17u : ac ? 75u : 25u;
            }
        }
        public Guid ReadActiveScheme() { Calls.Add("active"); return Active; }
        public uint ReadIndex(Guid scheme, Guid setting, bool ac) { Calls.Add("read"); return Values[(scheme, setting, ac)]; }
        public void WriteIndex(Guid scheme, Guid setting, uint value, bool ac)
        {
            Calls.Add("write"); writes++;
            if (!(failed && IgnoreOriginalMainFrequency && scheme == Main && setting == Frequency && value == 4700)) Values[(scheme, setting, ac)] = value;
            AfterWrite?.Invoke(setting, ac, value);
            if (!failed && writes == FailAtWrite)
            { failed = true; OnFailure?.Invoke(); throw new IOException("simulatedPartialPowerWrite"); }
            if (failed && PauseOnRestore) { Allowed = false; Calls.Add("paused"); }
        }
        public void SetActiveScheme(Guid scheme) { Calls.Add("select"); Active = scheme; }
        public uint ReadFriendlyName(Guid scheme, byte[]? buffer, ref uint size)
        { size = 2; if (buffer is not null) { buffer[0] = 0; buffer[1] = 0; } return 0; }
    }
}
