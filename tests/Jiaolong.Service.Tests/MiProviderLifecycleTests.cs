using System.Reflection;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong.Contracts.Errors;
using Jiaolong.Hardware.Abstractions.Compatibility;
using Jiaolong.Hardware.Mechrevo.Wmi;
using Jiaolong.Service.Home;

namespace Jiaolong.Service.Tests;

[TestClass]
public sealed partial class MiProviderLifecycleTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [TestMethod]
    public void Direct_mi_baseline_first_write_and_partial_pause_preserve_reliable_mutation_classification()
    {
        foreach (string stage in new[] { "baseline", "firstWrite", "partial" })
        {
            bool writesAllowed = true, readsAllowed = true;
            var bus = new FakeBus(kind =>
            {
                if (stage == "baseline" || stage == "partial" && kind == "write") readsAllowed = writesAllowed = false;
                if (stage == "firstWrite" && kind == "read") writesAllowed = false;
            });
            using var provider = new WindowsHomeHardwareProvider();
            typeof(WindowsHomeHardwareProvider).GetField("miInterface", Private)!.SetValue(provider,
                new MiCommonInterfaceClient(bus, bus, () => writesAllowed, () => readsAllowed));
            typeof(WindowsHomeHardwareProvider).GetField("compatibilityDecision", Private)!.SetValue(provider,
                new CompatibilityDecision(CompatibilityMode.Writable,
                    new CapabilitySnapshot([new("performanceMode", CapabilityState.Available, null)]),
                    new CompatibilityManifest { ManifestId = "fake", WmiProvider = new() { Namespace = "root\\WMI", Class = "MICommonInterface", InstanceName = "fake", ReadType = 250, WriteType = 251 } }, []));
            CommandResult? result = null;
            Exception? error = Capture(() => result = (CommandResult)typeof(WindowsHomeHardwareProvider).GetMethod("ExecuteLocked", Private)!
                .Invoke(provider, [new SetPerformanceModeCommand(Guid.NewGuid(), PerformanceMode.Balanced), CancellationToken.None])!);
            Assert.IsNull(error, stage);
            Assert.AreEqual(stage == "partial" ? CommandState.RecoveryRequired : CommandState.Rejected, result?.State, stage);
            Assert.AreEqual(stage == "partial" ? HardwareMutation.Unknown : HardwareMutation.NoMutation, result?.Mutation, stage);
            Assert.AreEqual(stage == "partial" ? 1 : 0, bus.Writes, "No recovery dispatch: " + stage);
            Assert.AreEqual((stage == "partial").ToString(), result!.Error!.Details["writeAttempted"]);
            Assert.IsNull(result.VerifiedPerformanceMode);
        }
    }

    [TestMethod]
    public void Paused_dispose_and_fan_release_keep_pending_snapshots_even_inside_recovery_read_scope()
    {
        foreach (bool recoveryScope in new[] { false, true })
        {
            var provider = new WindowsHomeHardwareProvider();
            var lease = (LightingPreviewLease)typeof(WindowsHomeHardwareProvider).GetField("lightingPreview", Private)!.GetValue(provider)!;
            lease.Begin(new("Fixed", 50), DateTimeOffset.UtcNow);
            var cooling = typeof(WindowsHomeHardwareProvider).GetField("strongCoolingActive", Private)!;
            cooling.SetValue(provider, true); // No EC transport, write ownership or OEM baseline is injected.
            IDisposable? scope = null;
            if (recoveryScope)
            {
                var lifecycle = new PowerLifecycleState(provider);
                scope = lifecycle.AllowRecoveryReads(lifecycle.Pause());
            }
            else provider.SetBackgroundWorkPaused(true);
            try
            {
                var error = Capture(() => typeof(WindowsHomeHardwareProvider).GetMethod("ReleaseFanLocked", Private)!.Invoke(provider, null));
                Assert.AreEqual("hardwareAccessPaused", error?.Message);
                Assert.AreEqual(true, cooling.GetValue(provider));
                provider.Dispose();
                Assert.AreEqual(true, cooling.GetValue(provider), "Dispose must not clear unconfirmed fan ownership.");
                Assert.IsNotNull(lease.Original);
                Assert.IsNull(typeof(WindowsHomeHardwareProvider).GetField("fanTimer", Private)!.GetValue(provider));
                Assert.IsNull(typeof(WindowsHomeHardwareProvider).GetField("lightingTimer", Private)!.GetValue(provider));
                provider.Dispose();
            }
            finally
            {
                scope?.Dispose();
                lease.Complete();
                cooling.SetValue(provider, false);
                provider.Dispose();
            }
        }
    }

    [TestMethod]
    public void Lighting_zero_write_cancellation_returns_no_mutation_but_written_cancellation_stays_distinct()
    {
        foreach (string stage in new[] { "baseline", "firstWrite", "partial" })
        {
            using var cancellation = new CancellationTokenSource();
            int reads = 0;
            var bus = new FakeBus(kind =>
            {
                if (kind == "read") reads++;
                if (stage == "baseline" && reads == 1 || stage == "firstWrite" && reads == 3 || stage == "partial" && kind == "write")
                    cancellation.Cancel();
            });
            using var provider = new WindowsHomeHardwareProvider(() => false);
            typeof(WindowsHomeHardwareProvider).GetField("miInterface", Private)!.SetValue(provider, new MiCommonInterfaceClient(bus, bus));
            typeof(WindowsHomeHardwareProvider).GetField("compatibilityDecision", Private)!.SetValue(provider,
                new CompatibilityDecision(CompatibilityMode.Writable,
                    new CapabilitySnapshot([new("keyboardLighting", CapabilityState.Available, null)]),
                    new CompatibilityManifest { ManifestId = "fake", WmiProvider = new() { Namespace = "root\\WMI", Class = "MICommonInterface", InstanceName = "fake", ReadType = 250, WriteType = 251 } }, []));
            CommandResult? result = null;
            var error = Capture(() => result = (CommandResult)typeof(WindowsHomeHardwareProvider).GetMethod("ExecuteLightingLocked", Private)!
                .Invoke(provider, [new SetKeyboardLightingCommand(Guid.NewGuid(), new("Fixed", 50)), cancellation.Token])!);
            if (stage == "partial")
            {
                Assert.IsInstanceOfType<OperationCanceledException>(error);
                Assert.AreEqual(true, error!.Data["keyboardLightingWriteAttempted"]);
                Assert.IsTrue(bus.Writes > 0);
            }
            else
            {
                Assert.IsNull(error, stage);
                Assert.AreEqual(CommandState.Rejected, result?.State, stage);
                Assert.AreEqual(HardwareMutation.NoMutation, result?.Mutation, stage);
                Assert.AreEqual(ErrorCode.CancelledBeforeApply, result?.Error?.Code, stage);
                Assert.AreEqual(0, bus.Writes, stage);
            }
        }
    }

    [TestMethod]
    public void Strong_cooling_restore_preserves_external_flags_and_retries_readback_failures()
    {
        foreach (string stage in new[] { "external", "invalid", "already", "restore", "lostReadback", "ignoredWrite" })
        {
            using var provider = new WindowsHomeHardwareProvider();
            var bus = new FakeBus(_ => { })
            {
                Value = stage == "external" ? (byte)1 : stage == "invalid" ? (byte)2 : (byte)0,
                IgnoreWrites = stage == "ignoredWrite", FailReadAfterWrite = stage == "lostReadback"
            };
            var type = typeof(WindowsHomeHardwareProvider);
            type.GetField("miInterface", Private)!.SetValue(provider, new MiCommonInterfaceClient(bus, bus));
            type.GetField("compatibilityDecision", Private)!.SetValue(provider,
                new CompatibilityDecision(CompatibilityMode.Writable,
                    new CapabilitySnapshot([new("strongCooling", CapabilityState.Available, null)]),
                    new CompatibilityManifest { ManifestId = "fake", WmiProvider = new() { Namespace = "root\\WMI", Class = "MICommonInterface",
                        InstanceName = "fake", ReadType = 250, WriteType = 251 } }, []));
            var saved = type.GetField("fanPreviousStrongCooling", Private)!;
            byte target = stage is "external" or "invalid" or "already" ? (byte)0 : (byte)1;
            saved.SetValue(provider, target);
            type.GetField("fanExpectedStrongCooling", Private)!.SetValue(provider, (byte)0);
            Action restore = () => type.GetMethod("RestoreStrongCoolingLocked", Private)!.Invoke(provider, null);
            try
            {
                var error = Capture(restore);
                if (stage is "already" or "restore")
                {
                    Assert.IsNull(error, stage);
                    Assert.IsNull(saved.GetValue(provider));
                    Assert.AreEqual(target, bus.Value);
                }
                else
                {
                    Assert.IsInstanceOfType<IOException>(error, stage);
                    Assert.AreEqual(target, saved.GetValue(provider));
                    Assert.AreEqual(stage is "external" or "invalid" ? 0 : 1, bus.Writes);
                    if (stage is "external" or "invalid") bus.Value = target;
                    bus.ReadUnavailable = bus.FailReadAfterWrite = bus.IgnoreWrites = false;
                    Assert.IsNull(Capture(restore), stage);
                    Assert.IsNull(saved.GetValue(provider));
                }
                int writes = bus.Writes;
                Assert.IsNull(Capture(restore));
                Assert.AreEqual(writes, bus.Writes);
            }
            finally { saved.SetValue(provider, null); }
        }
    }

    [TestMethod]
    public void Disabling_strong_cooling_keeps_original_firmware_flag_until_release_and_readback_retry()
    {
        foreach (byte original in new byte[] { 0, 1 })
        {
            using var provider = new WindowsHomeHardwareProvider();
            var bus = new FakeBus(_ => { }) { Value = original };
            var type = typeof(WindowsHomeHardwareProvider);
            type.GetField("identity", Private)!.SetValue(provider, new HardwareIdentity("MRID6-23", "MRID6_23_P_V35", "cpu", "gpu"));
            type.GetField("miInterface", Private)!.SetValue(provider, new MiCommonInterfaceClient(bus, bus));
            type.GetField("compatibilityDecision", Private)!.SetValue(provider,
                new CompatibilityDecision(CompatibilityMode.Writable,
                    new CapabilitySnapshot([new("strongCooling", CapabilityState.Available, null)]),
                    new CompatibilityManifest { ManifestId = "fake", WmiProvider = new() { Namespace = "root\\WMI", Class = "MICommonInterface",
                        InstanceName = "fake", ReadType = 250, WriteType = 251 } }, []));
            var saved = type.GetField("fanPreviousStrongCooling", Private)!;
            try
            {
                var result = (CommandResult)type.GetMethod("ExecuteStrongCoolingLocked", Private)!.Invoke(provider,
                    [new SetStrongCoolingCommand(Guid.NewGuid(), false), CancellationToken.None])!;
                Assert.AreEqual(CommandState.Applied, result.State);
                Assert.AreEqual((byte)0, bus.Value);
                Assert.AreEqual(original == 0 ? null : (object)original, saved.GetValue(provider));
                Assert.IsNull(type.GetField("fanTransport", Private)!.GetValue(provider));
                Action release = () => type.GetMethod("ReleaseFanLocked", Private)!.Invoke(provider, null);
                if (original == 1)
                {
                    bus.FailReadAfterWrite = true;
                    Assert.IsInstanceOfType<IOException>(Capture(release));
                    Assert.AreEqual(original, saved.GetValue(provider));
                    bus.FailReadAfterWrite = bus.ReadUnavailable = false;
                }
                int writes = bus.Writes;
                Assert.IsNull(Capture(release));
                Assert.AreEqual(original, bus.Value);
                Assert.IsNull(saved.GetValue(provider));
                Assert.AreEqual(writes, bus.Writes);
                Assert.IsNull(type.GetField("fanTransport", Private)!.GetValue(provider));
            }
            finally
            {
                saved.SetValue(provider, null);
                (type.GetField("fanTimer", Private)!.GetValue(provider) as Timer)?.Change(Timeout.Infinite, Timeout.Infinite);
            }
        }
    }

    [TestMethod]
    public void Mode_empty_write_response_stays_pending_and_confirmed_rollback_carries_actual_mode()
    {
        foreach (bool emptyResponse in new[] { false, true })
        {
            using var provider = new WindowsHomeHardwareProvider();
            var bus = new FakeBus(_ => { }) { EmptyWriteResponse = emptyResponse, IgnoreWrites = !emptyResponse };
            var type = typeof(WindowsHomeHardwareProvider);
            type.GetField("miInterface", Private)!.SetValue(provider, new MiCommonInterfaceClient(bus, bus));
            type.GetField("compatibilityDecision", Private)!.SetValue(provider,
                new CompatibilityDecision(CompatibilityMode.Writable,
                    new CapabilitySnapshot([new("performanceMode", CapabilityState.Available, null)]),
                    new CompatibilityManifest { ManifestId = "fake", WmiProvider = new() { Namespace = "root\\WMI", Class = "MICommonInterface",
                        InstanceName = "fake", ReadType = 250, WriteType = 251 } }, []));
            var id = Guid.NewGuid();
            var result = (CommandResult)type.GetMethod("ExecuteLocked", Private)!.Invoke(provider,
                [new SetPerformanceModeCommand(id, PerformanceMode.Balanced), CancellationToken.None])!;
            Assert.AreEqual(emptyResponse ? CommandState.RecoveryRequired : CommandState.RolledBack, result.State);
            Assert.AreEqual(HardwareMutation.Unknown, result.Mutation);
            Assert.IsNull(result.VerifiedPerformanceMode);
            Assert.AreEqual(emptyResponse ? 1 : 2, bus.Writes);
            Assert.AreEqual(id, result.OperationId);
            if (emptyResponse) Assert.AreEqual(bool.TrueString, result.Error!.Details["writeAttempted"]);
            else Assert.AreEqual(PerformanceMode.Turbo, result.ModeEvidence?.ParsedMode);
        }
    }

    private static Exception? Capture(Action action)
    {
        try { action(); return null; }
        catch (TargetInvocationException error) { return error.InnerException; }
        catch (Exception error) { return error; }
    }

    [TestMethod]
    public async Task Firmware_flag_is_journaled_before_mi_write_and_verified_restore_retains_original_scope()
    {
        foreach (byte original in new byte[] { 0, 1 })
        {
            using var fixture = await FirmwareFixture.Create(original);
            int checkpoints = 0;
            fixture.Bus.BeforeWrite = () =>
            {
                using var disk = System.Text.Json.JsonDocument.Parse(File.ReadAllText(fixture.Path));
                var checkpoint = disk.RootElement.GetProperty("fanFirmwareRecovery");
                Assert.AreEqual(fixture.Scope.ClientSessionId, checkpoint.GetProperty("scope").GetProperty("clientSessionId").GetGuid());
                Assert.AreEqual(fixture.Bus.Value, checkpoint.GetProperty("before").GetByte());
                Assert.AreEqual(System.Text.Json.JsonValueKind.Null, checkpoint.GetProperty("observed").ValueKind);
                checkpoints++;
            };
            fixture.Prepare();
            fixture.Restore();
            Assert.AreEqual(original, fixture.Bus.Value);
            Assert.AreEqual(original == 0 ? 0 : 2, checkpoints);
            using var saved = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(fixture.Path));
            var record = saved.RootElement.GetProperty("fanFirmwareRecovery");
            if (original == 1)
            {
                Assert.AreEqual(1, record.GetProperty("originalBefore").GetByte());
                Assert.AreEqual(1, record.GetProperty("observed").GetByte());
                Assert.IsTrue(record.GetProperty("restoring").GetBoolean());
            }
            else Assert.AreEqual(System.Text.Json.JsonValueKind.Null, record.ValueKind);
        }
    }

    [TestMethod]
    public async Task Failed_firmware_readback_keeps_pending_scope_and_foreign_capture_cannot_retry_a_write()
    {
        using var fixture = await FirmwareFixture.Create(1);
        fixture.Bus.FailReadAfterWrite = true;
        Assert.ThrowsExactly<IOException>(fixture.Prepare);
        using (var disk = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(fixture.Path)))
            Assert.AreEqual(System.Text.Json.JsonValueKind.Null, disk.RootElement.GetProperty("fanFirmwareRecovery").GetProperty("observed").ValueKind);
        Assert.AreEqual((byte)0, fixture.Bus.Value);
        fixture.Bus.ReadUnavailable = fixture.Bus.FailReadAfterWrite = false;
        string capturePath = System.IO.Path.Combine(fixture.Root, "client-control-session.json");
        string original = await File.ReadAllTextAsync(capturePath);
        var foreign = System.Text.Json.Nodes.JsonNode.Parse(original)!;
        foreign["ownerSid"] = "other";
        await File.WriteAllTextAsync(capturePath, foreign.ToJsonString());
        Assert.ThrowsExactly<InvalidDataException>(fixture.Restore);
        Assert.AreEqual(1, fixture.Bus.Writes);
        await File.WriteAllTextAsync(capturePath, original);
        fixture.Restore();
        Assert.AreEqual((byte)1, fixture.Bus.Value);
        Assert.AreEqual(2, fixture.Bus.Writes);
        string verified = await File.ReadAllTextAsync(fixture.Path);
        var invalid = System.Text.Json.Nodes.JsonNode.Parse(verified)!;
        invalid["fanFirmwareRecovery"]!["observed"] = 2;
        string corrupt = invalid.ToJsonString();
        await File.WriteAllTextAsync(fixture.Path, corrupt);
        await Assert.ThrowsAsync<InvalidDataException>(() => new ControlExecutionJournal(fixture.Root).ReadAsync(CancellationToken.None));
        Assert.AreEqual(corrupt, await File.ReadAllTextAsync(fixture.Path));
    }

    [TestMethod]
    public async Task Unapplied_firmware_write_reconciles_original_value_without_another_mi_write()
    {
        using var fixture = await FirmwareFixture.Create(1);
        fixture.Bus.IgnoreWrites = true;
        Assert.ThrowsExactly<IOException>(fixture.Prepare);
        fixture.Bus.IgnoreWrites = false;
        fixture.Restore();
        Assert.AreEqual(1, fixture.Bus.Writes);
        using var disk = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(fixture.Path));
        var record = disk.RootElement.GetProperty("fanFirmwareRecovery");
        Assert.AreEqual(1, record.GetProperty("observed").GetByte());
        Assert.IsTrue(record.GetProperty("restoring").GetBoolean());
    }

    [TestMethod]
    public async Task Missing_or_changed_firmware_journal_prevents_the_first_mi_write()
    {
        using var fixture = await FirmwareFixture.Create(1);
        string original = await File.ReadAllTextAsync(fixture.Path);
        await File.WriteAllTextAsync(fixture.Path, "{}");
        Assert.ThrowsExactly<InvalidDataException>(fixture.Prepare);
        Assert.AreEqual(0, fixture.Bus.Writes);
        Assert.AreEqual((byte)1, fixture.Bus.Value);
        Assert.AreEqual("{}", await File.ReadAllTextAsync(fixture.Path));
        await File.WriteAllTextAsync(fixture.Path, original);
        fixture.Restore();
        Assert.AreEqual(0, fixture.Bus.Writes);
    }

    private sealed class FirmwareFixture : IDisposable
    {
        public string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "XisuraFirmwareCheckpointTests", Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(Root, "control-execution.json");
        public FanEcRecoveryScope Scope { get; } = new(Guid.NewGuid(), 17, Guid.NewGuid(), "user-a", new("MRID6-23", "MRID6_23_P_V35", "cpu", "gpu"));
        public ControlExecutionJournal Journal { get; private set; } = null!;
        public WindowsHomeHardwareProvider Provider { get; private set; } = null!;
        public FakeBus Bus { get; private set; } = null!;
        private bool injectedFanTransport;
        public static async Task<FirmwareFixture> Create(byte original,
            Func<Func<bool>, Func<bool>, Action<FanEcRecoveryCheckpoint>?, BldingFanEcTransport>? fanFactory = null,
            Func<FirmwareFixture, Task>? seed = null)
        {
            var fixture = new FirmwareFixture();
            fixture.Journal = new(fixture.Root);
            var operation = Guid.NewGuid();
            await fixture.Journal.SaveAsync(new(1, 17, 0, ControlModeId.Office, PerformanceMode.Quiet, [],
                new() { ["fan.strongCooling"] = new(operation, ControlOrigin.ManualLive, SettingRequest.Set(original == 1), SettingRequest.Set(true), ApplyState.Unknown)
                    { ClientSessionId = fixture.Scope.ClientSessionId } }), fixture.Scope.Identity, operation, true, CancellationToken.None, fixture.Scope.OwnerSid,
                recoveryScope: new(fixture.Scope.ServiceSessionId, new() { ["fan.strongCooling"] = new(fixture.Scope.OwnerSid, 17, null, 1) }, null, 0, fixture.Scope.ClientSessionId));
            await new ClientControlSessionStore(fixture.Root).WriteAsync(new(1, fixture.Scope.ServiceSessionId,
                new(fixture.Scope.ClientSessionId, 17, new string('A', 64), DateTimeOffset.UtcNow), fixture.Scope.OwnerSid, 1, 42,
                DateTime.UtcNow.AddMinutes(-1).ToFileTimeUtc(), fixture.Scope.Identity, new(null, null, null, null, original == 1, null))
                { Phase = ClientControlSessionPhase.Active }, CancellationToken.None);
            if (seed is not null) { await seed(fixture); fixture.Journal = new(fixture.Root); }
            fixture.Bus = new(_ => { }) { Value = original };
            fixture.injectedFanTransport = fanFactory is not null;
            fixture.Provider = fanFactory is null ? new(controlJournal: fixture.Journal) : new(fixture.Journal, fanFactory, () => false);
            var type = typeof(WindowsHomeHardwareProvider);
            type.GetField("identity", Private)!.SetValue(fixture.Provider, fixture.Scope.Identity);
            type.GetField("miInterface", Private)!.SetValue(fixture.Provider, new MiCommonInterfaceClient(fixture.Bus, fixture.Bus));
            type.GetField("compatibilityDecision", Private)!.SetValue(fixture.Provider,
                new CompatibilityDecision(CompatibilityMode.Writable, new CapabilitySnapshot([new("strongCooling", CapabilityState.Available, null), new("fanControl", CapabilityState.Available, null)]),
                    new CompatibilityManifest { ManifestId = "fake", WmiProvider = new() { Namespace = "root\\WMI", Class = "MICommonInterface", InstanceName = "fake", ReadType = 250, WriteType = 251 } }, []));
            return fixture;
        }
        public void Prepare() => Invoke("PrepareFanStrongCoolingLocked", [CancellationToken.None, true]);
        public void Restore() => Invoke("RestoreStrongCoolingLocked", null);
        private void Invoke(string method, object?[]? args)
        {
            try { typeof(WindowsHomeHardwareProvider).GetMethod(method, Private)!.Invoke(Provider, args); }
            catch (TargetInvocationException error) when (error.InnerException is not null)
            { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); }
        }
        public void Dispose()
        {
            if (injectedFanTransport)
            {
                typeof(WindowsHomeHardwareProvider).GetField("fanTransport", Private)!.SetValue(Provider, null);
                typeof(WindowsHomeHardwareProvider).GetField("fanTouched", Private)!.SetValue(Provider, false);
            }
            typeof(WindowsHomeHardwareProvider).GetField("fanPreviousStrongCooling", Private)!.SetValue(Provider, null);
            Provider.Dispose();
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }

    private sealed class FakeBus(Action<string> afterCall) : IMiReadTransport, IMiWriteTransport
    {
        public byte Value { get; set; } = 1;
        public bool IgnoreWrites, ReadUnavailable, FailReadAfterWrite;
        public bool EmptyWriteResponse;
        private readonly Dictionary<byte, byte[]> lighting = new() { [16] = [2], [17] = [10, 20, 30], [18] = [1] };
        public int Writes { get; private set; }
        public int Reads { get; private set; }
        public Action? BeforeWrite;
        public Task<byte[]?> ReadAsync(MiReadBinding binding, CancellationToken token)
        {
            Reads++;
            byte[] response = new byte[32];
            response[4] = Value;
            if (lighting.TryGetValue(binding.MethodName, out var bytes)) bytes.CopyTo(response, 4);
            afterCall("read");
            return Task.FromResult<byte[]?>(ReadUnavailable ? null : response);
        }
        public Task<byte[]?> WriteAsync(VerifiedWmiBinding binding, ReadOnlyMemory<byte> request, CancellationToken token)
        {
            BeforeWrite?.Invoke();
            Writes++;
            if (!IgnoreWrites) Value = request.Span[4];
            if (lighting.ContainsKey(binding.MethodName)) lighting[binding.MethodName] = request.Slice(4, binding.MethodName == 17 ? 3 : 1).ToArray();
            afterCall("write");
            if (FailReadAfterWrite) ReadUnavailable = true;
            return Task.FromResult<byte[]?>(EmptyWriteResponse ? null : []);
        }
    }
}
