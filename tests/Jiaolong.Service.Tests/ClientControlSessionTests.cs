using System.Text.Json;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;
using Jiaolong.Contracts.Protocol;
using Jiaolong.Service.Home;
using Jiaolong.Service.Ipc;

namespace Jiaolong.Service.Tests;

[TestClass]
public sealed class ClientControlSessionTests
{
    [TestMethod]
    public async Task Begin_persists_fresh_detached_baseline_without_writing_hardware()
    {
        using var fixture = new Fixture();
        await fixture.Runtime.InitializeAsync(CancellationToken.None);
        fixture.Provider.Cores[0] = -40;
        var result = await fixture.Begin();
        Assert.AreEqual(CommandState.Applied, result.State);
        Assert.AreEqual(HardwareMutation.NoMutation, result.Mutation);
        Assert.AreEqual(fixture.Provider.Key, result.ClientSession!.DeviceKey);
        Assert.IsTrue(fixture.Provider.IncludedAdvanced);
        Assert.AreEqual(1, fixture.Provider.Reads);
        var original = await File.ReadAllTextAsync(fixture.Path);
        fixture.Provider.Cores[0] = -10;
        fixture.Provider.Nodes[1] = new(700, 1500, 1000);
        using var saved = JsonDocument.Parse(original);
        var baseline = saved.RootElement.GetProperty("baseline");
        Assert.AreEqual(-40, baseline.GetProperty("cpuTuning").GetProperty("perCoreCurveOptimizer").GetProperty("0").GetInt32());
        Assert.AreEqual(0, baseline.GetProperty("gpuVf").GetProperty("nodes")[1].GetProperty("offsetKhz").GetInt32());
        var retry = await fixture.Begin();
        Assert.AreEqual(result.ClientSession, retry.ClientSession);
        Assert.AreEqual(original, await File.ReadAllTextAsync(fixture.Path));
        Assert.AreEqual(0, fixture.Provider.Writes);
    }

    [TestMethod]
    public async Task Pipe_reconnect_reuses_session_but_other_process_or_identity_cannot_claim_it()
    {
        using var fixture = new Fixture();
        var dispatcher = new RequestDispatcher(homeRuntime: fixture.Runtime);
        RequestEnvelope Request(ushort minor = 1, object? payload = null) => new(new(1, minor), Guid.NewGuid(), DateTimeOffset.UtcNow,
            Guid.NewGuid(), "beginClientSession", DateTimeOffset.UtcNow.AddSeconds(10), JsonSerializer.SerializeToElement(payload ?? new { }));
        var response = await dispatcher.DispatchAsync(Request(), Owner(), CancellationToken.None);
        Assert.AreEqual(ResponseStatus.Success, response.Status);
        var first = response.Payload!.Value.Deserialize(ProtocolJsonContext.Default.CommandResult)!;
        Assert.AreEqual(CommandState.Applied, first.State);
        Assert.IsNotNull(first.ClientSession);
        var reconnected = await dispatcher.DispatchAsync(Request(), Owner() with { ConnectionId = Guid.NewGuid() }, CancellationToken.None);
        Assert.AreEqual(first.ClientSession, reconnected.Payload!.Value.Deserialize(ProtocolJsonContext.Default.CommandResult)!.ClientSession);
        var original = await File.ReadAllTextAsync(fixture.Path);
        foreach (var other in new[] { Owner() with { UserSid = "S-1-5-21-2" }, Owner() with { ProcessId = 43 },
            Owner() with { ProcessCreationFileTimeUtc = Owner().ProcessCreationFileTimeUtc + 1 }, Owner() with { SessionId = 2 } })
            Assert.AreEqual(ErrorCode.ConflictDetected, (await fixture.Begin(other)).Error!.Code);
        Assert.AreEqual(ResponseStatus.Error, (await dispatcher.DispatchAsync(Request(0), Owner(), CancellationToken.None)).Status);
        Assert.AreEqual(ResponseStatus.Error, (await dispatcher.DispatchAsync(Request(payload: new { processId = 42 }), Owner(), CancellationToken.None)).Status);
        Assert.AreEqual(1, fixture.Provider.Reads);
        Assert.AreEqual(original, await File.ReadAllTextAsync(fixture.Path));
        Assert.AreEqual(0, fixture.Provider.Writes);
    }

    [TestMethod]
    public async Task Prior_service_epoch_or_damaged_snapshot_requires_recovery_and_is_preserved()
    {
        using var fixture = new Fixture();
        Assert.AreEqual(CommandState.Applied, (await fixture.Begin()).State);
        var original = await File.ReadAllTextAsync(fixture.Path);
        var restarted = new HomeServiceRuntime(fixture.Provider, journal: new(fixture.Root));
        Assert.AreEqual(CommandState.RecoveryRequired, (await restarted.BeginClientControlSessionAsync(Guid.NewGuid(), Owner(), CancellationToken.None)).State);
        fixture.Runtime.Execution.InvalidateHardware();
        Assert.AreEqual(CommandState.RecoveryRequired, (await fixture.Begin()).State);
        Assert.AreEqual(original, await File.ReadAllTextAsync(fixture.Path));
        foreach (string damaged in new[] { "{", original.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 99", StringComparison.Ordinal) })
        {
            await File.WriteAllTextAsync(fixture.Path, damaged);
            Assert.AreEqual(CommandState.RecoveryRequired, (await fixture.Begin()).State);
            Assert.AreEqual(damaged, await File.ReadAllTextAsync(fixture.Path));
        }
        Assert.AreEqual(0, fixture.Provider.Writes);
    }

    [TestMethod]
    public async Task Missing_authority_identity_lifecycle_or_storage_never_produces_a_usable_session()
    {
        using var fixture = new Fixture();
        foreach (var invalid in new[] { ClientIdentity.Anonymous(), Owner() with { UserSid = "" }, Owner() with { ProcessId = 0 },
            Owner() with { ProcessCreationFileTimeUtc = 0 }, Owner() with { ProcessCreationFileTimeUtc = long.MaxValue } })
            Assert.AreEqual(ErrorCode.UnauthorizedClient, (await fixture.Begin(invalid)).Error!.Code);
        Assert.AreEqual(ErrorCode.ValidationFailed,
            (await fixture.Runtime.BeginClientControlSessionAsync(Guid.Empty, Owner(), CancellationToken.None)).Error!.Code);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.Runtime.BeginClientControlSessionAsync(Guid.NewGuid(), Owner(), cancelled.Token));
        fixture.Provider.Key = null;
        Assert.AreEqual(CommandState.Rejected, (await fixture.Begin()).State);
        Assert.IsFalse(File.Exists(fixture.Path));
        fixture.Provider.Key = new string('A', 64);
        fixture.Provider.ReadFailure = new IOException("read failure");
        Assert.AreEqual(CommandState.RecoveryRequired, (await fixture.Begin()).State);
        fixture.Provider.ReadFailure = null;
        fixture.Provider.AfterRead = fixture.Runtime.Execution.InvalidateHardware;
        Assert.AreEqual(CommandState.RecoveryRequired, (await fixture.Begin()).State);
        Assert.IsFalse(File.Exists(fixture.Path));
        fixture.Provider.AfterRead = null;
        Directory.CreateDirectory(fixture.Path);
        Assert.AreEqual(CommandState.RecoveryRequired, (await fixture.Begin()).State);
        Assert.AreEqual(0, fixture.Provider.Writes);
    }

    [TestMethod]
    public async Task Boot_proof_is_captured_twice_and_retries_never_replace_the_original_capture()
    {
        var boot = DateTimeOffset.UtcNow.AddHours(-2);
        int reads = 0;
        using var fixture = new Fixture(_ => { reads++; return Task.FromResult<DateTimeOffset?>(boot); });
        var first = await fixture.Begin();
        Assert.AreEqual(CommandState.Applied, first.State);
        string original = await File.ReadAllTextAsync(fixture.Path);
        using var document = JsonDocument.Parse(original);
        Assert.AreEqual(boot, document.RootElement.GetProperty("bootStartedAtUtc").GetDateTimeOffset());
        Assert.AreEqual(2, reads);
        Assert.AreEqual(first.ClientSession, (await fixture.Begin()).ClientSession);
        Assert.AreEqual(original, await File.ReadAllTextAsync(fixture.Path));
        Assert.AreEqual(1, fixture.Provider.Reads);
        boot = boot.AddMinutes(-1);
        Assert.AreEqual(CommandState.RecoveryRequired, (await fixture.Begin()).State);
        Assert.AreEqual(original, await File.ReadAllTextAsync(fixture.Path));
        Assert.AreEqual(0, fixture.Provider.Writes);
        foreach (var invalid in new[] { DateTimeOffset.UnixEpoch, DateTimeOffset.UtcNow.AddDays(1), DateTimeOffset.FromFileTime(Birth).AddSeconds(1) })
        {
            var json = System.Text.Json.Nodes.JsonNode.Parse(original)!;
            json["bootStartedAtUtc"] = invalid;
            string changed = json.ToJsonString();
            await File.WriteAllTextAsync(fixture.Path, changed);
            await Assert.ThrowsAsync<InvalidDataException>(() => new ClientControlSessionStore(fixture.Root).ReadAsync(CancellationToken.None));
            Assert.AreEqual(changed, await File.ReadAllTextAsync(fixture.Path));
        }
    }

    [TestMethod]
    public async Task Missing_changed_or_invalid_boot_proof_never_saves_a_usable_capture()
    {
        var boot = DateTimeOffset.UtcNow.AddHours(-2);
        foreach (var sequence in new DateTimeOffset?[][] { [null], [DateTimeOffset.UnixEpoch], [DateTimeOffset.UtcNow.AddDays(1)],
            [DateTimeOffset.UtcNow], [boot, null], [boot, boot.AddMinutes(1)] })
        {
            int reads = 0;
            using var fixture = new Fixture(_ => Task.FromResult(sequence[Math.Min(reads++, sequence.Length - 1)]));
            Directory.CreateDirectory(fixture.Root);
            Assert.AreEqual(CommandState.RecoveryRequired, (await fixture.Begin()).State);
            Assert.IsFalse(File.Exists(fixture.Path));
            Assert.AreEqual(0, fixture.Provider.Writes);
        }
    }

    [TestMethod]
    public void Boot_time_parser_keeps_explicit_offset_and_rejects_incomplete_or_invalid_dates()
    {
        Assert.AreEqual(new DateTimeOffset(2026, 10, 16, 14, 14, 44, TimeSpan.Zero).AddTicks(1234560),
            WindowsSystemBootProbe.ParseBootTimeUtc("20261016221444.123456+480"));
        Assert.AreEqual(WindowsSystemBootProbe.ParseBootTimeUtc("20261016221444.123456+480"),
            WindowsSystemBootProbe.ParseBootTimeUtc("20261016071444.123456-420"));
        foreach (var value in new string?[] { null, "", "20261016221444.******+480", "20260230221444.123456+480",
            "20261016221444.123456+999", "20261016221444.123456:480", "20261016221444.123456+480extra" })
            Assert.IsNull(WindowsSystemBootProbe.ParseBootTimeUtc(value));
    }

    [TestMethod]
    public async Task Windows_boot_query_is_read_only_stable_and_honors_precancelled_reads()
    {
        var probe = new WindowsSystemBootProbe();
        var first = await probe.ReadAsync(CancellationToken.None);
        var second = await probe.ReadAsync(CancellationToken.None);
        Assert.IsNotNull(first);
        Assert.AreEqual(first, second);
        Assert.IsTrue(first > DateTimeOffset.UnixEpoch && first < new DateTimeOffset(System.Diagnostics.Process.GetCurrentProcess().StartTime));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => probe.ReadAsync(cancelled.Token));
        Console.WriteLine($"Read-only Win32_OperatingSystem.LastBootUpTime={first:O}");
    }

    [TestMethod]
    public async Task Oem_configuration_is_captured_through_wrappers_once_for_active_and_preparing_sessions()
    {
        foreach (bool preparing in new[] { false, true })
        {
            using var fixture = new Fixture(wrapped: true, preparing: preparing);
            var expected = new OemCpuConfigurationSnapshot(DateTimeOffset.UtcNow, 0, 0, 0, 0, 1, 0);
            fixture.Provider.OemConfiguration = expected;
            IHomeHardwareProvider readOnly = new ReadOnlyHomeHardwareProvider(fixture.Provider);
            Assert.AreEqual(expected, await readOnly.ReadOemCpuConfigurationAsync(CancellationToken.None));
            var first = await fixture.Begin();
            Assert.AreEqual(CommandState.Applied, first.State);
            Assert.AreEqual(2, fixture.Provider.OemReads);
            string original = await File.ReadAllTextAsync(fixture.Path);
            using var document = JsonDocument.Parse(original);
            var raw = document.RootElement.GetProperty("baseline").GetProperty("oemCpuConfiguration");
            Assert.AreEqual(expected, raw.Deserialize<OemCpuConfigurationSnapshot>(new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            fixture.Provider.OemConfiguration = expected with { FpptWatts = 70 };
            Assert.AreEqual(first.ClientSession, (await fixture.Begin()).ClientSession);
            Assert.AreEqual(2, fixture.Provider.OemReads);
            Assert.AreEqual(original, await File.ReadAllTextAsync(fixture.Path));
            Assert.AreEqual(0, fixture.Provider.Writes);
        }
    }

    [TestMethod]
    public async Task Unknown_oem_configuration_stays_unknown_while_cancellation_and_epoch_changes_reject_capture()
    {
        foreach (Exception? failure in new Exception?[] { null, new IOException("readFailed"), new UnauthorizedAccessException() })
        {
            using var fixture = new Fixture();
            fixture.Provider.OemFailure = failure;
            Assert.AreEqual(CommandState.Applied, (await fixture.Begin()).State);
            Assert.AreEqual(1, fixture.Provider.OemReads);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(fixture.Path));
            Assert.AreEqual(JsonValueKind.Null, document.RootElement.GetProperty("baseline").GetProperty("oemCpuConfiguration").ValueKind);
            fixture.Provider.OemFailure = null;
            Assert.AreEqual(CommandState.Applied, (await fixture.Begin()).State);
            Assert.AreEqual(1, fixture.Provider.OemReads);
        }
        foreach (bool cancel in new[] { false, true })
        {
            using var fixture = new Fixture();
            using var cancellation = new CancellationTokenSource();
            fixture.Provider.AfterOemRead = () =>
            {
                if (cancel) cancellation.Cancel();
                else fixture.Runtime.Execution.InvalidateHardware();
            };
            var capture = fixture.Runtime.BeginClientControlSessionAsync(Guid.NewGuid(), Owner(), cancellation.Token);
            if (cancel) await Assert.ThrowsAsync<OperationCanceledException>(() => capture);
            else Assert.AreEqual(CommandState.RecoveryRequired, (await capture).State);
            Assert.AreEqual(1, fixture.Provider.OemReads);
            Assert.IsFalse(File.Exists(fixture.Path));
            Assert.AreEqual(0, fixture.Provider.Writes);
        }
    }

    [TestMethod]
    public async Task Oem_capture_timestamp_and_immutable_baseline_are_validated_without_rewriting_old_or_damaged_records()
    {
        using var fixture = new Fixture();
        fixture.Provider.OemConfiguration = new(DateTimeOffset.UtcNow, 0x81, 0, 255, 0, 1, 0);
        Assert.AreEqual(CommandState.Applied, (await fixture.Begin()).State);
        string original = await File.ReadAllTextAsync(fixture.Path);
        var store = new ClientControlSessionStore(fixture.Root);
        var saved = (await store.ReadAsync(CancellationToken.None))!;
        using (var document = JsonDocument.Parse(original))
            Assert.IsTrue(document.RootElement.GetProperty("baseline").TryGetProperty("oemCpuConfiguration", out _));
        var changed = System.Text.Json.Nodes.JsonNode.Parse(original)!;
        changed["baseline"]!["oemCpuConfiguration"]!["fpptWatts"] = 42;
        var changedCapture = changed.Deserialize<ClientControlSessionSnapshot>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        await Assert.ThrowsAsync<InvalidDataException>(() => store.WriteAsync(changedCapture, CancellationToken.None));
        Assert.AreEqual(original, await File.ReadAllTextAsync(fixture.Path));
        foreach (var invalidTime in new[] { DateTimeOffset.UnixEpoch, saved.Handle.CapturedAtUtc.AddDays(1) })
        {
            changed = System.Text.Json.Nodes.JsonNode.Parse(original)!;
            changed["baseline"]!["oemCpuConfiguration"]!["capturedAtUtc"] = invalidTime;
            string damaged = changed.ToJsonString();
            await File.WriteAllTextAsync(fixture.Path, damaged);
            await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadAsync(CancellationToken.None));
            Assert.AreEqual(damaged, await File.ReadAllTextAsync(fixture.Path));
        }
        foreach (string missing in new[] { "capturedAtUtc", "customModeByte", "splWatts", "fpptWatts", "temperatureLimitC", "itsm", "stsm" })
        {
            changed = System.Text.Json.Nodes.JsonNode.Parse(original)!;
            changed["baseline"]!["oemCpuConfiguration"]!.AsObject().Remove(missing);
            string incomplete = changed.ToJsonString();
            await File.WriteAllTextAsync(fixture.Path, incomplete);
            await Assert.ThrowsAsync<JsonException>(() => store.ReadAsync(CancellationToken.None));
            Assert.AreEqual(incomplete, await File.ReadAllTextAsync(fixture.Path));
        }
        changed = System.Text.Json.Nodes.JsonNode.Parse(original)!;
        changed["baseline"]!.AsObject().Remove("oemCpuConfiguration");
        string legacy = changed.ToJsonString();
        await File.WriteAllTextAsync(fixture.Path, legacy);
        Assert.IsNotNull(await store.ReadAsync(CancellationToken.None));
        Assert.AreEqual(legacy, await File.ReadAllTextAsync(fixture.Path));
    }

    private static readonly long Birth = DateTime.UtcNow.AddMinutes(-1).ToFileTimeUtc();
    private static ClientIdentity Owner() => ClientIdentity.LocalAdministrator("S-1-5-21-1", 1) with
        { ProcessId = 42, ProcessCreationFileTimeUtc = Birth, ConnectionId = Guid.NewGuid() };

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "XisuraClientSessionTests", Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(Root, "client-control-session.json");
        public Provider Provider { get; } = new();
        public HomeServiceRuntime Runtime { get; }
        public Fixture(Func<CancellationToken, Task<DateTimeOffset?>>? readBootTime = null, bool wrapped = false, bool preparing = false)
        {
            var store = new AdaptiveAutomationStateStore();
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            typeof(AdaptiveAutomationStateStore).GetField("loaded", flags)!.SetValue(store, true);
            typeof(AdaptiveAutomationStateStore).GetField("jsonStore", flags)!.SetValue(store, new Jiaolong.Service.Storage.AtomicJsonStore(Root, "adaptive-test.json"));
            Runtime = new(wrapped ? new AdaptiveAutomationHardwareProvider(Provider, store) : Provider,
                journal: new(Root), readBootTime: readBootTime, requireClientSessions: preparing);
        }
        public Task<CommandResult> Begin(ClientIdentity? client = null) => Runtime.BeginClientControlSessionAsync(Guid.NewGuid(), client ?? Owner(), CancellationToken.None);
        public void Dispose() => Directory.Delete(Root, true);
    }

    private sealed class Provider : IHomeHardwareProvider
    {
        public string? Key { get; set; } = new('A', 64);
        public Dictionary<int, int> Cores { get; } = new() { [0] = -50 };
        public GpuVfNode[] Nodes { get; } = Enumerable.Range(0, 127).Select(i => new GpuVfNode(600 + i * 5, 1200 + i * 15, 0)).ToArray();
        public int Reads { get; private set; }
        public int Writes { get; private set; }
        public bool IncludedAdvanced { get; private set; }
        public Action? AfterRead { get; set; }
        public Exception? ReadFailure { get; set; }
        public OemCpuConfigurationSnapshot? OemConfiguration { get; set; }
        public Exception? OemFailure { get; set; }
        public Action? AfterOemRead { get; set; }
        public int OemReads { get; private set; }
        public Task<OemCpuConfigurationSnapshot?> ReadOemCpuConfigurationAsync(CancellationToken token)
        {
            OemReads++;
            if (OemFailure is not null) throw OemFailure;
            AfterOemRead?.Invoke();
            return Task.FromResult(OemConfiguration);
        }
        private static readonly HardwareIdentity Identity = new("board", "bios", "cpu", "gpu");
        public Task<HomeHardwareState> DiagnoseAsync(CancellationToken token) => Task.FromResult(new HomeHardwareState(new([]), DeviceSupportState.Ready, null, Identity, null));
        public Task<HomeHardwareState> ReinitializeAsync(CancellationToken token) => DiagnoseAsync(token);
        public Task<HardwareSnapshot> ReadTelemetryAsync(CancellationToken token) => throw new InvalidOperationException("Unexpected telemetry read");
        public Task<HomeControlState> ReadControlsAsync(CancellationToken token) => throw new InvalidOperationException("Use a fresh baseline");
        public Task<string?> ReadDeviceRecoveryKeyAsync(HardwareIdentity identity, CancellationToken token) => Task.FromResult(Key);
        public Task<HomeControlState> ReadControlBaselineAsync(CancellationToken token, bool includeCpuAdvanced = false)
        {
            Reads++;
            IncludedAdvanced = includeCpuAdvanced;
            if (ReadFailure is not null) throw ReadFailure;
            var controls = new HomeControlState(PerformanceMode.Turbo, false, [])
            {
                CpuTuning = new(null, null, null, null, null, 1, null, -50, "readback") { PerCoreCurveOptimizer = Cores },
                GpuVf = new(Nodes, -200000, 200000, null),
                KeyboardLighting = new("Fixed", 100) { BrightnessLevel = 3, Red = 63, Green = 0, Blue = 255 }
            };
            AfterRead?.Invoke();
            return Task.FromResult(controls);
        }
        public Task<CommandResult> ExecuteAsync(HardwareCommand command, CancellationToken token)
        { Writes++; throw new InvalidOperationException("Session capture cannot write hardware"); }
    }
}
