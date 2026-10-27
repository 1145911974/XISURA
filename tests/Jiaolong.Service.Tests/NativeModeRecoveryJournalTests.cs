using System.Reflection;
using System.Text.Json;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong.Hardware.Abstractions.Compatibility;
using Jiaolong.Hardware.Mechrevo.Wmi;
using Jiaolong.Service.Home;

namespace Jiaolong.Service.Tests;

[TestClass]
public sealed class NativeModeRecoveryJournalTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly HardwareIdentity Identity = new("board", "bios", "cpu", "gpu");

    [TestMethod]
    public async Task Mode_native_write_has_durable_before_and_failed_ack_retains_original_parent_record()
    {
        foreach (bool empty in new[] { false, true })
        {
            using var fixture = new Fixture();
            await fixture.Seed();
            var bus = new Bus { EmptyResponse = empty };
            using var provider = fixture.Provider(bus);
            using var scope = fixture.Bind();
            bus.BeforeWrite = () =>
            {
                var attempt = new ControlExecutionJournal(fixture.Root).ReadAsync(CancellationToken.None).GetAwaiter().GetResult()
                    .Unresolved.Single().NativeModeAttempt;
                Assert.IsNotNull(attempt);
                Assert.AreEqual(fixture.Operation, attempt.StepId);
                Assert.AreEqual(PerformanceMode.Turbo, attempt.Before);
                Assert.AreEqual(PerformanceMode.Balanced, attempt.Target);
                Assert.IsNull(attempt.Observed);
                Assert.AreEqual(fixture.Application, attempt.ClientSessionId);
            };
            var result = fixture.Execute(provider);
            Assert.AreEqual(empty ? CommandState.RecoveryRequired : CommandState.Applied, result.State);
            var record = (await new ControlExecutionJournal(fixture.Root).ReadAsync(CancellationToken.None)).Unresolved.Single();
            Assert.IsNotNull(record.NativeModeAttempt);
            Assert.AreEqual(empty ? null : (PerformanceMode?)PerformanceMode.Balanced, record.NativeModeAttempt.Observed);
            Assert.AreEqual(1, bus.Writes);
            Assert.AreEqual(empty ? 1 : 2, bus.Reads, "Persistence must reuse existing MI reads.");
            await fixture.Journal.SaveAsync(Fixture.Pending, Identity, fixture.Operation, true, CancellationToken.None, "owner", "parent-progress");
            Assert.IsNotNull((await new ControlExecutionJournal(fixture.Root).ReadAsync(CancellationToken.None)).Unresolved.Single().NativeModeAttempt);
        }
    }

    [TestMethod]
    public async Task Mode_capture_failure_identity_or_epoch_mismatch_prevents_first_driver_write()
    {
        foreach (string failure in new[] { "missingScope", "owner", "epoch", "application", "storage", "deleted" })
        {
            using var fixture = new Fixture();
            await fixture.Seed();
            var bus = new Bus();
            using var provider = fixture.Provider(bus);
            using var scope = failure == "missingScope" ? null : fixture.Journal.BindNativeModeOperation(new(fixture.Operation,
                fixture.Service, failure == "epoch" ? 18 : 17, failure == "owner" ? "other" : "owner",
                failure == "application" ? Guid.Empty : fixture.Application));
            if (failure == "storage") { File.Delete(fixture.Path); Directory.CreateDirectory(fixture.Path); }
            if (failure == "deleted") File.Delete(fixture.Path);
            var result = fixture.Execute(provider);
            Assert.AreEqual(CommandState.Rejected, result.State, failure);
            Assert.AreEqual(HardwareMutation.NoMutation, result.Mutation, failure);
            Assert.AreEqual(0, bus.Writes, failure);
            Assert.AreEqual((byte)1, bus.Value, failure);
            if (failure == "deleted") Assert.IsFalse(File.Exists(fixture.Path));
        }
    }

    [TestMethod]
    public async Task Mode_retry_preserves_first_before_and_invalid_records_are_retained()
    {
        using var fixture = new Fixture();
        await fixture.Seed();
        using var scope = fixture.Bind();
        await fixture.Journal.RecordNativeModeAsync(fixture.Operation, Identity, PerformanceMode.Turbo, PerformanceMode.Balanced, null, CancellationToken.None);
        var next = Guid.NewGuid();
        await fixture.Journal.RecordNativeModeAsync(next, Identity, PerformanceMode.Balanced, PerformanceMode.Quiet, null, CancellationToken.None);
        var valid = await fixture.Journal.ReadAsync(CancellationToken.None);
        var entry = valid.Unresolved.Single();
        var attempt = entry.NativeModeAttempt!;
        Assert.AreEqual(PerformanceMode.Turbo, attempt.OriginalBefore);
        Assert.AreEqual(PerformanceMode.Balanced, attempt.Before);
        Assert.AreEqual(next, attempt.StepId);
        foreach (var invalid in new[] { attempt with { StepId = Guid.Empty }, attempt with { OwnerSid = "other" },
            attempt with { HardwareEpoch = 18 }, attempt with { ClientSessionId = Guid.Empty },
            attempt with { Before = (PerformanceMode)999 }, attempt with { Target = (PerformanceMode)999 },
            attempt with { Observed = (PerformanceMode)999 } })
        {
            string text = JsonSerializer.Serialize(valid with { Unresolved = [entry with { NativeModeAttempt = invalid }] }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            await File.WriteAllTextAsync(fixture.Path, text);
            try
            {
                await new ControlExecutionJournal(fixture.Root).ReadAsync(CancellationToken.None);
                Assert.Fail("Invalid mode recovery evidence was accepted.");
            }
            catch (Exception error) when (error is InvalidDataException or JsonException) { }
            Assert.AreEqual(text, await File.ReadAllTextAsync(fixture.Path));
        }
    }

    private sealed class Fixture : IDisposable
    {
        public readonly string Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "XisuraNativeModeJournal", Guid.NewGuid().ToString("N"));
        public readonly Guid Operation = Guid.NewGuid(), Service = Guid.NewGuid(), Application = Guid.NewGuid();
        public static ControlExecutionState Pending => new(1, 17, 0, ControlModeId.Gaming, PerformanceMode.Balanced, [], [])
            { NativeModeApplyState = ApplyState.Unknown };
        public ControlExecutionJournal Journal { get; }
        public string Path => System.IO.Path.Combine(Root, "control-execution.json");
        public Fixture() => Journal = new(Root);
        public Task Seed() => Journal.SaveAsync(Pending, Identity, Operation, true, CancellationToken.None, "owner", nameof(SetPerformanceModeCommand));
        public IDisposable Bind() => Journal.BindNativeModeOperation(new(Operation, Service, 17, "owner", Application));
        public WindowsHomeHardwareProvider Provider(Bus bus)
        {
            var provider = new WindowsHomeHardwareProvider(controlJournal: Journal);
            typeof(WindowsHomeHardwareProvider).GetField("identity", Private)!.SetValue(provider, Identity);
            typeof(WindowsHomeHardwareProvider).GetField("miInterface", Private)!.SetValue(provider, new MiCommonInterfaceClient(bus, bus));
            typeof(WindowsHomeHardwareProvider).GetField("compatibilityDecision", Private)!.SetValue(provider, new CompatibilityDecision(CompatibilityMode.Writable,
                new CapabilitySnapshot([new("performanceMode", CapabilityState.Available, null)]), new CompatibilityManifest
                { ManifestId = "fake", WmiProvider = new() { Namespace = "root\\WMI", Class = "MICommonInterface", InstanceName = "fake", ReadType = 250, WriteType = 251 } }, []));
            return provider;
        }
        public CommandResult Execute(WindowsHomeHardwareProvider provider) => (CommandResult)provider.GetType().GetMethod("ExecuteLocked", Private)!
            .Invoke(provider, [new SetPerformanceModeCommand(Operation, PerformanceMode.Balanced), CancellationToken.None])!;
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }

    private sealed class Bus : IMiReadTransport, IMiWriteTransport
    {
        public byte Value = 1;
        public bool EmptyResponse;
        public int Reads, Writes;
        public Action? BeforeWrite;
        public Task<byte[]?> ReadAsync(MiReadBinding binding, CancellationToken token)
        { Reads++; var bytes = new byte[32]; bytes[4] = Value; return Task.FromResult<byte[]?>(bytes); }
        public Task<byte[]?> WriteAsync(VerifiedWmiBinding binding, ReadOnlyMemory<byte> request, CancellationToken token)
        { BeforeWrite?.Invoke(); Writes++; Value = request.Span[4]; return Task.FromResult<byte[]?>(EmptyResponse ? null : []); }
    }
}
