using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong.Service.Home;
using Jiaolong.Service.Ipc;

namespace Jiaolong.Service.Tests;

[TestClass]
public sealed class SessionRecoveryOwnershipTests
{
    [TestMethod]
    public async Task Core_and_interleaved_curve_writes_keep_the_full_original_and_latest_tables()
    {
        using var fixture = new Fixture();
        var handle = await fixture.Begin();
        var original = fixture.Provider.Offsets();
        var first = await fixture.Execute(new SetGpuCoreOffsetCommand(Guid.NewGuid(), 0, 1000, true) { ExpectedVfOffsetsKhz = original }, handle);
        AssertCurve(first, handle, original, fixture.Provider.Offsets());
        var changed = fixture.Provider.Offsets();
        changed[60] += 1000;
        var edit = await fixture.Execute(new SetGpuVfCurveCommand(Guid.NewGuid(), fixture.Provider.Offsets(), changed, true), handle);
        AssertCurve(edit, handle, original, changed);
        var final = await fixture.Execute(new SetGpuCoreOffsetCommand(Guid.NewGuid(), 1000, 2000, true)
            { ExpectedVfOffsetsKhz = changed }, handle);
        AssertCurve(final, handle, original, fixture.Provider.Offsets());
        var saved = await fixture.Runtime.ReadControlJournalAsync(CancellationToken.None);
        Assert.AreEqual(handle.SessionId, saved!.Current!.Fields["gpu.vfOffsetsKhz"].ClientSessionId);
    }

    [TestMethod]
    public async Task Lighting_hex_is_confirmed_and_returning_to_original_releases_its_ownership()
    {
        using var fixture = new Fixture();
        var handle = await fixture.Begin();
        var original = fixture.Provider.Light;
        var changed = await fixture.Execute(new SetKeyboardLightingCommand(Guid.NewGuid(), original with { Red = 18, Green = 52, Blue = 86 }), handle);
        Assert.AreEqual(CommandState.Applied, changed.State);
        var field = changed.ExecutionState!.Fields["lighting.hex"];
        Assert.AreEqual(ApplyState.Confirmed, field.WriteState);
        Assert.AreEqual("#CCFF00", field.Baseline!.Value!.Value.GetString());
        Assert.AreEqual("#123456", field.LastWritten!.Value!.Value.GetString());
        Assert.AreEqual(handle.SessionId, field.ClientSessionId);
        var restored = await fixture.Execute(new SetKeyboardLightingCommand(Guid.NewGuid(), original), handle);
        Assert.IsFalse(restored.ExecutionState!.Fields.ContainsKey("lighting.hex"));
    }

    [TestMethod]
    public async Task Native_mode_owner_is_persisted_and_missing_session_file_prevents_new_capture()
    {
        using var fixture = new Fixture();
        var handle = await fixture.Begin();
        Assert.AreEqual(CommandState.Applied,
            (await fixture.Execute(new SetPerformanceModeCommand(Guid.NewGuid(), PerformanceMode.Balanced), handle)).State);
        var saved = await fixture.Runtime.ReadControlJournalAsync(CancellationToken.None);
        Assert.AreEqual(handle.SessionId, saved!.Current!.NativeModeOwnerClientSessionId);
        Assert.AreEqual(0, saved.Current.Fields.Count);
        var snapshot = System.IO.Path.Combine(fixture.Root, "client-control-session.json");
        File.Move(snapshot, snapshot + ".retained");
        var restarted = new HomeServiceRuntime(fixture.Provider, journal: new(fixture.Root), requireClientSessions: true);
        Assert.AreEqual(CommandState.RecoveryRequired,
            (await restarted.BeginClientControlSessionAsync(Guid.NewGuid(), fixture.Owner, CancellationToken.None)).State);
        Assert.IsFalse(File.Exists(snapshot));
    }

    private static void AssertCurve(CommandResult result, ClientControlSessionHandle handle, int[] original, int[] expected)
    {
        Assert.AreEqual(CommandState.Applied, result.State);
        var field = result.ExecutionState!.Fields["gpu.vfOffsetsKhz"];
        Assert.AreEqual(ApplyState.Confirmed, field.WriteState);
        Assert.AreEqual(handle.SessionId, field.ClientSessionId);
        int[] Values(SettingRequest value) => value.Value!.Value.EnumerateArray().Select(item => item.GetInt32()).ToArray();
        CollectionAssert.AreEqual(original, Values(field.Baseline!));
        CollectionAssert.AreEqual(expected, Values(field.LastWritten!));
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "XisuraRecoveryOwnershipTests", Guid.NewGuid().ToString("N"));
        public Provider Provider { get; } = new();
        public HomeServiceRuntime Runtime { get; }
        public ClientIdentity Owner { get; } = ClientIdentity.LocalAdministrator("S-1-5-21-1", 1) with
            { ProcessId = 42, ProcessCreationFileTimeUtc = DateTime.UtcNow.AddMinutes(-1).ToFileTimeUtc(), ConnectionId = Guid.NewGuid() };
        public Fixture() => Runtime = new(Provider, journal: new(Root), requireClientSessions: true);
        public async Task<ClientControlSessionHandle> Begin()
        {
            var handle = (await Runtime.BeginClientControlSessionAsync(Guid.NewGuid(), Owner, CancellationToken.None)).ClientSession!;
            Assert.AreEqual(CommandState.Applied, (await Runtime.ConfigureClientSessionExitAsync(Guid.NewGuid(), new(handle.SessionId, null), Owner, CancellationToken.None)).State);
            return handle;
        }
        public Task<CommandResult> Execute(HardwareCommand command, ClientControlSessionHandle handle) => Runtime.ExecuteAsync(
            command with { ClientSessionId = handle.SessionId }, CancellationToken.None, Owner.ConnectionId, Owner.UserSid, Owner);
        public void Dispose() => Directory.Delete(Root, true);
    }

    private sealed class Provider : IHomeHardwareProvider
    {
        private static readonly HardwareIdentity Identity = new("board", "bios", "cpu", "gpu");
        private GpuVfNode[] nodes = Enumerable.Range(0, 127).Select(index => new GpuVfNode(600 + 5 * index, 1200 + 15 * index, index == 60 ? 75000 : 0)).ToArray();
        private int core;
        private PerformanceMode mode = PerformanceMode.Turbo;
        public KeyboardLightingPlan Light { get; private set; } = new("Fixed", 100) { BrightnessLevel = 3, Red = 204, Green = 255, Blue = 0, LogoEnabled = true };
        public int[] Offsets() => nodes.Select(node => node.OffsetKhz).ToArray();
        public Task<HomeHardwareState> DiagnoseAsync(CancellationToken token)
        {
            HardwareCommand[] commands = [new SetGpuCoreOffsetCommand(Guid.NewGuid(), 0, 0, true), new SetGpuVfCurveCommand(Guid.NewGuid(), [], [], true),
                new SetKeyboardLightingCommand(Guid.NewGuid(), Light), new SetPerformanceModeCommand(Guid.NewGuid(), mode)];
            var fields = new[] { "coreOffsetKhz", "vfOffsetsKhz", "effect", "brightness", "hex", "speed", "logo" }.Select(key =>
                new FieldCapability(key, CapabilityState.Available, true, true, key == "coreOffsetKhz" ? -1000000 : null,
                    key == "coreOffsetKhz" ? 1000000 : null, null, "fake", "fake", "PreservesSelectedMode", DateTimeOffset.UtcNow, 0)).ToArray();
            return Task.FromResult(new HomeHardwareState(new(commands.Select(command => new CapabilityDescriptor(
                HomeCapabilityCatalog.RequiredCapability(command), CapabilityState.Available, null)).DistinctBy(item => item.Key).ToArray())
                { Fields = fields, IsOnAcPower = true }, DeviceSupportState.Ready, null, Identity, null));
        }
        public Task<HomeHardwareState> ReinitializeAsync(CancellationToken token) => DiagnoseAsync(token);
        public Task<string?> ReadDeviceRecoveryKeyAsync(HardwareIdentity identity, CancellationToken token) => Task.FromResult<string?>(new string('A', 64));
        public Task<HardwareSnapshot> ReadTelemetryAsync(CancellationToken token) => Task.FromResult(new HardwareSnapshot(DateTimeOffset.UtcNow, "fake", null, null, null, null));
        public Task<HomeControlState> ReadControlsAsync(CancellationToken token) => Task.FromResult(new HomeControlState(mode, false, [])
            { KeyboardLighting = Light, GpuVf = new(nodes.ToArray(), -200000, 200000, null)
                { CoreOffsetKhz = core, MemoryOffsetKhz = 0, CoreMinimumOffsetKhz = -1000000, CoreMaximumOffsetKhz = 1000000 } });
        public Task<CommandResult> ExecuteAsync(HardwareCommand command, CancellationToken token)
        {
            switch (command)
            {
                case SetGpuCoreOffsetCommand value:
                    Assert.AreEqual(value.ExpectedOffsetKhz, core);
                    if (value.ExpectedVfOffsetsKhz is not null) CollectionAssert.AreEqual(value.ExpectedVfOffsetsKhz, Offsets());
                    nodes = nodes.Select((node, index) => index == 0 ? node : node with { OffsetKhz = node.OffsetKhz + value.OffsetKhz - core }).ToArray();
                    core = value.OffsetKhz; break;
                case SetGpuVfCurveCommand value:
                    CollectionAssert.AreEqual(value.ExpectedOffsetsKhz, Offsets());
                    nodes = nodes.Select((node, index) => node with { OffsetKhz = value.OffsetsKhz[index] }).ToArray(); break;
                case SetKeyboardLightingCommand value: Light = value.Plan; break;
                case SetPerformanceModeCommand value: mode = value.Mode; break;
                default: throw new InvalidOperationException("Unexpected fake command");
            }
            return Task.FromResult(new CommandResult(command.OperationId, CommandState.Applied, null, RequiredUserAction.None, null, false)
                { Mutation = HardwareMutation.Changed, VerifiedPerformanceMode = command is SetPerformanceModeCommand ? mode : null });
        }
    }
}
