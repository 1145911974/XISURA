using System.Text.Json;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;
using Jiaolong.Service.Home;

namespace Jiaolong.Service.Tests;

[TestClass]
public sealed class ReleaseOnlyFanCapabilityTests
{
    [TestMethod]
    public async Task Legacy_fan_writes_honor_field_capabilities_while_firmware_release_remains_available()
    {
        var provider = new Provider();
        var runtime = new HomeServiceRuntime(provider);
        await runtime.InitializeAsync(CancellationToken.None);
        foreach (HardwareCommand command in new HardwareCommand[]
        {
            new SetFanControlCommand(Guid.NewGuid(), Plan("Fixed"), true),
            new SetFanControlCommand(Guid.NewGuid(), Plan("Curve"), true),
            new SetFanControlCommand(Guid.NewGuid(), Plan("Auto") with { MaximumRpm = 5000 }, true),
            new SetStrongCoolingCommand(Guid.NewGuid(), true)
        })
        {
            var result = await runtime.ExecuteAsync(command, CancellationToken.None);
            Assert.AreEqual(ErrorCode.CapabilityUnavailable, result.Error?.Code);
            Assert.AreEqual(HardwareMutation.NoMutation, result.Mutation);
            Assert.AreEqual(0, provider.Writes, command.GetType().Name);
        }
        var release = await runtime.ExecuteAsync(new ReleaseFanControlCommand(Guid.NewGuid(), ReleaseReason.UserRequested), CancellationToken.None);
        Assert.AreEqual(CommandState.Applied, release.State);
        Assert.AreEqual(1, provider.Writes);
        Assert.IsNull(provider.Fan);
        provider.AdvancedWritable = true;
        await runtime.ReinitializeAsync(CancellationToken.None);
        await runtime.ExecuteAsync(new SetFanControlCommand(Guid.NewGuid(), Plan("Fixed"), true), CancellationToken.None);
        Assert.AreEqual(2, provider.Writes, "A provider with verified advanced fields must retain its supported backend path.");
        Assert.AreEqual("Fixed", provider.Fan?.Strategy);
    }

    [TestMethod]
    public async Task Mixed_fan_request_cannot_inherit_unsupported_live_targets_before_changing_native_mode()
    {
        var provider = new Provider();
        var runtime = new HomeServiceRuntime(provider);
        await runtime.InitializeAsync(CancellationToken.None);
        var key = PresetKey.Create(ControlModeId.Office, 1);
        var inherited = (SetFanControlCommand)ControlPlanCompiler.Commands(ControlPageId.Fan,
            new() { ["strategy"] = SettingRequest.Set("Curve") }, await provider.ReadControlsAsync(CancellationToken.None)).Single();
        Assert.AreEqual("Curve", inherited.Plan.Strategy);
        CollectionAssert.AreEqual(provider.Fan!.Points, inherited.Plan.Points, "The curve inherits live points even when no point field is Set.");
        var preset = new PagePresetEnvelope(PagePresetEnvelope.CurrentSchemaVersion, ControlPageId.Fan, key, "mixed curve with official fields",
            JsonSerializer.SerializeToElement(new { profile = 0, isShared = true, cpu = new[] { new { temperature = 40, targetPercent = 60 }, new { temperature = 95, targetPercent = 100 } },
                gpu = new[] { new { temperature = 40, targetPercent = 60 }, new { temperature = 95, targetPercent = 100 } },
                shared = new[] { new { temperature = 40, targetPercent = 60 }, new { temperature = 95, targetPercent = 100 } }, strategy = "Curve", fixedRpm = 3000 }), DateTimeOffset.UtcNow)
            { Revision = 1, Settings = new() { ["strategy"] = SettingRequest.Set("Curve"),
                ["fixedRpm"] = SettingRequest.Official, ["cpu"] = SettingRequest.Official } };
        Assert.AreEqual(CommandState.Applied, (await runtime.ExecuteAsync(new PublishPresetCatalogCommand(Guid.NewGuid(), 0, [preset]), CancellationToken.None)).State);
        var operation = Guid.NewGuid();
        var result = await runtime.ExecuteAsync(new ApplyControlRequestCommand(operation, new(operation, 0, ControlOrigin.ManualMode,
            ControlModeId.Office, PerformanceMode.Quiet, [new(ControlPageId.Fan, true, key, 1)])), CancellationToken.None);
        Assert.AreEqual(CommandState.Rejected, result.State);
        Assert.AreEqual("Preflight", result.Error?.Details.GetValueOrDefault("stage"));
        Assert.AreEqual("compiledCommandFieldNotWritable", result.Error?.Details.GetValueOrDefault("cause"));
        Assert.AreEqual(0, provider.Writes);
        Assert.AreEqual(PerformanceMode.Balanced, provider.Mode);
    }

    private static FanControlPlan Plan(string strategy) => new([new(40, 60), new(95, 100)])
        { Strategy = strategy, FixedRpm = strategy == "Fixed" ? 3000 : null };

    private sealed class Provider : IHomeHardwareProvider
    {
        public int Writes;
        public bool AdvancedWritable;
        public FanControlPlan? Fan = Plan("Fixed");
        public PerformanceMode Mode = PerformanceMode.Balanced;
        private bool strongCooling;
        private HomeControlState Controls => new(Mode, strongCooling, []) { ActiveFanControlPlan = Fan };
        private HomeHardwareState State => new(new([
            new("performanceMode", CapabilityState.Available, null), new("fanControl", CapabilityState.Available, null),
            new("strongCooling", CapabilityState.Available, null)])
            { Fields = new[] { "strategy", "fixedRpm", "maximumRpm", "cpu", "gpu", "shared", "isShared", "strongCooling" }
                .Select(key => new FieldCapability(key, key == "strategy" || AdvancedWritable ? CapabilityState.Available : CapabilityState.ReadOnly,
                    true, key == "strategy" || AdvancedWritable, null, null, null, "", "fake", "PreservesSelectedMode", DateTimeOffset.UtcNow, 0)).ToArray() },
            DeviceSupportState.Ready, null, new("board", "bios", "cpu", "gpu"), null) { Controls = Controls };
        public Task<HomeHardwareState> DiagnoseAsync(CancellationToken token) => Task.FromResult(State);
        public Task<HomeHardwareState> ReinitializeAsync(CancellationToken token) => Task.FromResult(State);
        public Task<HardwareSnapshot> ReadTelemetryAsync(CancellationToken token) => Task.FromResult(new HardwareSnapshot(DateTimeOffset.UtcNow, "normal", 50, 20, 2400, 40));
        public Task<HomeControlState> ReadControlsAsync(CancellationToken token) => Task.FromResult(Controls);
        public Task<CommandResult> ExecuteAsync(HardwareCommand command, CancellationToken token)
        {
            Writes++;
            switch (command)
            {
                case ReleaseFanControlCommand: Fan = null; strongCooling = false; break;
                case SetFanControlCommand fan: Fan = fan.Plan; break;
                case SetStrongCoolingCommand cooling: strongCooling = cooling.Enabled; break;
                case SetPerformanceModeCommand mode: Mode = mode.Mode; break;
            }
            return Task.FromResult(new CommandResult(command.OperationId, CommandState.Applied, null, RequiredUserAction.None, null, false)
                { Mutation = HardwareMutation.Changed });
        }
    }
}
