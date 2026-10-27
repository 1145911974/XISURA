using System.Reflection;
using System.Text.Json;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;
using Jiaolong.Service.Home;
using Jiaolong.Service.Storage;

namespace Jiaolong.Service.Tests;

[TestClass]
public sealed class AdaptiveAutomationConfigurationIntentTests
{
    [TestMethod]
    public async Task Enable_intent_validates_both_effective_battery_targets_before_persisting()
    {
        using var fixture = new Fixture();
        var safe = Configuration(false);
        await fixture.Store.SetConfigurationAsync(safe, CancellationToken.None, ownerSid: "original-owner");
        var before = await fixture.CaptureAsync();
        foreach (var map in new[]
        {
            safe.TargetMap with { DcOffice = safe.TargetMap.AcTurbo },
            safe.TargetMap with { DcGame = safe.TargetMap.AcTurbo }
        })
        {
            await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => fixture.Store.SetConfigurationAsync(
                safe with { TargetMap = map }, CancellationToken.None, before.Snapshot.Revision,
                AutomationConfigurationIntent.Enable, "replacement-owner"));
            await fixture.AssertUnchangedAsync(before);
        }
    }

    [TestMethod]
    public async Task Update_intent_preserves_enabled_state_for_validation_without_changing_the_prior_record()
    {
        using var fixture = new Fixture();
        var safe = Configuration(true);
        await fixture.Store.SetConfigurationAsync(safe, CancellationToken.None, ownerSid: "original-owner");
        var before = await fixture.CaptureAsync();
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(() => fixture.Store.SetConfigurationAsync(null!, CancellationToken.None));
        await fixture.AssertUnchangedAsync(before);
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => fixture.Store.SetConfigurationAsync(
            safe with { Enabled = false, TargetMap = safe.TargetMap with { DcGame = safe.TargetMap.AcTurbo } },
            CancellationToken.None, before.Snapshot.Revision, AutomationConfigurationIntent.Update, "replacement-owner"));
        await fixture.AssertUnchangedAsync(before);
    }

    [TestMethod]
    public async Task Disabled_effective_state_allows_inactive_battery_maps_without_reenabling_automation()
    {
        foreach (var intent in new[] { AutomationConfigurationIntent.Disable, AutomationConfigurationIntent.Update })
        {
            using var fixture = new Fixture();
            var safe = Configuration(intent == AutomationConfigurationIntent.Disable);
            await fixture.Store.SetConfigurationAsync(safe, CancellationToken.None);
            long revision = (await fixture.Store.ReadAsync(CancellationToken.None)).Revision;
            await fixture.Store.SetConfigurationAsync(safe with
            {
                Enabled = true,
                TargetMap = safe.TargetMap with { DcGame = safe.TargetMap.AcTurbo }
            }, CancellationToken.None, revision, intent);
            var saved = await fixture.Store.ReadAsync(CancellationToken.None);
            Assert.IsFalse(saved.Configuration!.Enabled);
            Assert.AreEqual(revision + 1, saved.Revision);
            fixture.AssertPersistedEnabled(false);
        }
    }

    [TestMethod]
    public async Task Valid_explicit_intents_and_revision_conflicts_keep_the_persisted_authority_consistent()
    {
        using var fixture = new Fixture();
        var safe = Configuration(false);
        await fixture.Store.SetConfigurationAsync(safe, CancellationToken.None);
        long revision = (await fixture.Store.ReadAsync(CancellationToken.None)).Revision;
        foreach (var intent in new[] { AutomationConfigurationIntent.Enable, AutomationConfigurationIntent.Update, AutomationConfigurationIntent.Disable })
        {
            await fixture.Store.SetConfigurationAsync(safe with { Enabled = intent == AutomationConfigurationIntent.Disable },
                CancellationToken.None, revision, intent, "current-owner");
            var current = await fixture.Store.ReadAsync(CancellationToken.None);
            bool enabled = intent != AutomationConfigurationIntent.Disable;
            Assert.AreEqual(enabled, current.Configuration!.Enabled);
            Assert.AreEqual(++revision, current.Revision);
            Assert.AreEqual("current-owner", current.OwnerSid);
            fixture.AssertPersistedEnabled(enabled);
        }
        var before = await fixture.CaptureAsync();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fixture.Store.SetConfigurationAsync(safe,
            CancellationToken.None, revision - 1, AutomationConfigurationIntent.Enable, "stale-owner"));
        await fixture.AssertUnchangedAsync(before);
    }

    [TestMethod]
    public async Task Failed_publication_restores_owner_revision_and_manual_takeover_without_replacing_the_file()
    {
        using var fixture = new Fixture();
        var safe = Configuration(true);
        await fixture.Store.SetConfigurationAsync(safe, CancellationToken.None, ownerSid: "original-owner");
        await fixture.Store.MarkManualModeChangeAsync(DateTimeOffset.UtcNow, true, CancellationToken.None);
        var before = await fixture.CaptureAsync();
        Assert.IsTrue(before.Snapshot.ManualModeChanged);
        Assert.AreEqual(DateTimeOffset.MaxValue, before.Snapshot.ManualOverrideUntilUtc);
        fixture.FailPublication = true;
        await Assert.ThrowsExactlyAsync<IOException>(() => fixture.Store.SetConfigurationAsync(safe with { Enabled = false },
            CancellationToken.None, before.Snapshot.Revision, AutomationConfigurationIntent.Enable, "replacement-owner"));
        await fixture.AssertUnchangedAsync(before);
    }

    [TestMethod]
    public async Task Coordinated_and_legacy_command_paths_reject_the_invalid_enable_intent_without_hardware_calls()
    {
        using var fixture = new Fixture();
        var safe = Configuration(false);
        await fixture.Store.SetConfigurationAsync(safe, CancellationToken.None, ownerSid: "original-owner");
        var before = await fixture.CaptureAsync();
        var provider = DispatchProxy.Create<IHomeHardwareProvider, NoHardwareProxy>();
        var bridge = new AdaptiveAutomationHardwareProvider(provider, fixture.Store);
        foreach (bool coordinated in new[] { true, false })
        {
            var command = new SetAdaptiveAutomationConfigurationCommand(Guid.NewGuid(), safe with
                { TargetMap = safe.TargetMap with { DcGame = safe.TargetMap.AcTurbo } })
                { ExpectedRevision = before.Snapshot.Revision, Intent = AutomationConfigurationIntent.Enable };
            var result = coordinated
                ? await bridge.ExecuteCoordinatedAsync(command, CancellationToken.None, "replacement-owner")
                : await bridge.ExecuteAsync(command, CancellationToken.None);
            Assert.AreEqual(CommandState.Rejected, result.State);
            Assert.AreEqual(ErrorCode.ValidationFailed, result.Error!.Code);
            Assert.AreEqual(0, ((NoHardwareProxy)(object)provider).Calls);
            await fixture.AssertUnchangedAsync(before);
            var stale = command with { Configuration = safe, ExpectedRevision = before.Snapshot.Revision - 1 };
            var staleResult = coordinated
                ? await bridge.ExecuteCoordinatedAsync(stale, CancellationToken.None)
                : await bridge.ExecuteAsync(stale, CancellationToken.None);
            Assert.AreEqual(CommandState.Rejected, staleResult.State);
            Assert.AreEqual(ErrorCode.IdempotencyConflict, staleResult.Error!.Code);
            await fixture.AssertUnchangedAsync(before);
        }
    }

    [TestMethod]
    public async Task Follow_preparation_preserves_automation_configuration_and_manual_override()
    {
        using var fixture = new Fixture();
        await fixture.Store.SetConfigurationAsync(Configuration(true), CancellationToken.None, ownerSid: "original-owner");
        var before = await fixture.CaptureAsync();
        var provider = DispatchProxy.Create<IHomeHardwareProvider, NoHardwareProxy>();
        var bridge = new AdaptiveAutomationHardwareProvider(provider, fixture.Store);
        var id = Guid.NewGuid();
        var key = PresetKey.Create(ControlModeId.Gaming, 1);
        await bridge.PrepareManualCommandAsync(new ApplyControlRequestCommand(id,
            new(id, 0, ControlOrigin.ModeFollow, ControlModeId.Gaming, PerformanceMode.Balanced,
                [new(ControlPageId.Gpu, true, key, 1)])), CancellationToken.None);
        await fixture.AssertUnchangedAsync(before);
        Assert.AreEqual(0, ((NoHardwareProxy)(object)provider).Calls);
    }

    [TestMethod]
    public async Task Baseline_slots_accept_captured_native_modes_without_hardware_writes()
    {
        foreach (var native in new[] { PerformanceMode.Quiet, PerformanceMode.Balanced, PerformanceMode.Turbo })
        foreach (bool coordinated in new[] { true, false })
        {
            using var fixture = new Fixture();
            var original = Configuration(false);
            await fixture.Store.SetConfigurationAsync(original, CancellationToken.None, ownerSid: "original-owner");
            var before = await fixture.CaptureAsync();
            var battery = PresetKey.Create(ControlModeId.Office, 1);
            var candidate = original with
            {
                Enabled = true,
                TargetMap = original.TargetMap with { DcOffice = battery, DcGame = battery },
                Presets = [.. original.Presets.Select(preset => preset with { PerformanceMode = native }),
                    new(battery, PerformanceMode.Quiet, null)]
            };
            var provider = DispatchProxy.Create<IHomeHardwareProvider, NoHardwareProxy>();
            var bridge = new AdaptiveAutomationHardwareProvider(provider, fixture.Store);
            var command = new SetAdaptiveAutomationConfigurationCommand(Guid.NewGuid(), candidate)
            { ExpectedRevision = before.Snapshot.Revision, Intent = AutomationConfigurationIntent.Enable };
            var result = coordinated
                ? await bridge.ExecuteCoordinatedAsync(command, CancellationToken.None, "original-owner")
                : await bridge.ExecuteAsync(command, CancellationToken.None);
            Assert.AreEqual(CommandState.Applied, result.State, $"{native}, coordinated={coordinated}");
            Assert.IsNull(result.Error);
            var actual = await fixture.Store.ReadAsync(CancellationToken.None);
            Assert.IsTrue(actual.Configuration!.Enabled);
            Assert.AreEqual(before.Snapshot.Revision + 1, actual.Revision);
            Assert.IsTrue(actual.Configuration.Presets.Where(preset => preset.Key.Slot == 2)
                .All(preset => preset.PerformanceMode == native));
            fixture.AssertPersistedEnabled(true);
            Assert.AreEqual(0, ((NoHardwareProxy)(object)provider).Calls);
        }
    }

    [TestMethod]
    public void Nonbaseline_slots_still_require_their_native_mode_and_baselines_reject_custom()
    {
        var configuration = Configuration(true);
        foreach (int slot in new[] { 1, 3 })
        {
            var wrong = new AdaptiveAutomationPreset(PresetKey.Create(ControlModeId.Office, slot), PerformanceMode.Balanced, null);
            Assert.ThrowsExactly<ArgumentException>(() => AdaptiveAutomationConfigurationValidator.Validate(
                configuration with { Presets = [.. configuration.Presets, wrong] }));
        }
        foreach (var invalid in new[] { PerformanceMode.Custom, (PerformanceMode)99 })
            Assert.ThrowsExactly<ArgumentException>(() => AdaptiveAutomationConfigurationValidator.Validate(configuration with
            {
                Presets = configuration.Presets.Select(preset => preset with { PerformanceMode = invalid }).ToArray()
            }));
    }

    [TestMethod]
    public async Task Captured_turbo_baseline_remains_forbidden_for_either_battery_target()
    {
        using var fixture = new Fixture();
        var original = Configuration(false);
        await fixture.Store.SetConfigurationAsync(original, CancellationToken.None, ownerSid: "original-owner");
        var before = await fixture.CaptureAsync();
        foreach (bool office in new[] { true, false })
        {
            var target = office ? original.TargetMap.DcOffice : original.TargetMap.DcGame;
            var candidate = original with
            {
                Enabled = true,
                Presets = original.Presets.Select(preset => preset.Key == target
                    ? preset with { PerformanceMode = PerformanceMode.Turbo } : preset).ToArray()
            };
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => AdaptiveAutomationConfigurationValidator.Validate(candidate));
            await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => fixture.Store.SetConfigurationAsync(
                candidate, CancellationToken.None, before.Snapshot.Revision, AutomationConfigurationIntent.Enable, "replacement-owner"));
            await fixture.AssertUnchangedAsync(before);
        }
    }

    private static AdaptiveAutomationConfiguration Configuration(bool enabled)
    {
        var office = PresetKey.Create(ControlModeId.Office, 2);
        var game = PresetKey.Create(ControlModeId.Gaming, 2);
        var turbo = PresetKey.Create(ControlModeId.Turbo, 2);
        return new(enabled, AdaptiveAutomationStrategyId.BalancedAdaptive,
            new(40, 35, 8, true, 85, 90, 30, 20, 15, 120, 20, 25, true, 85, 85, 15, 30, 2, false, 300, []),
            new(office, game, turbo, office, game),
            [new(office, PerformanceMode.Quiet, null), new(game, PerformanceMode.Balanced, null), new(turbo, PerformanceMode.Turbo, null)]);
    }

    public class NoHardwareProxy : DispatchProxy
    {
        public int Calls { get; private set; }
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            Calls++;
            throw new InvalidOperationException("Hardware access is forbidden in this configuration-only test.");
        }
    }

    private sealed record RecordedState(AdaptiveAutomationStoreSnapshot Snapshot, string SnapshotJson, string CurrentFile);

    private sealed class Fixture : IDisposable
    {
        private const string Temp = @"C:\Users\Administrator\AppData\Local\Temp";
        public string Root { get; } = Path.Combine(Temp, "XisuraAutomationIntent-" + Guid.NewGuid().ToString("N"));
        public AdaptiveAutomationStateStore Store { get; } = new();
        public bool FailPublication { get; set; }
        private string FilePath => Path.Combine(Root, "adaptive.json");
        public Fixture()
        {
            var jsonStore = new AtomicJsonStore(Root, "adaptive.json", () =>
            {
                if (FailPublication) throw new IOException("Injected configuration publication failure.");
            });
            // Existing test seam: skip machine loading and bind persistence to this C: fixture only.
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(AdaptiveAutomationStateStore).GetField("jsonStore", flags)!.SetValue(Store, jsonStore);
            typeof(AdaptiveAutomationStateStore).GetField("loaded", flags)!.SetValue(Store, true);
        }
        public async Task<RecordedState> CaptureAsync()
        {
            var snapshot = await Store.ReadAsync(CancellationToken.None);
            return new(snapshot, JsonSerializer.Serialize(snapshot), await File.ReadAllTextAsync(FilePath));
        }
        public async Task AssertUnchangedAsync(RecordedState before)
        {
            var after = await CaptureAsync();
            Assert.AreEqual(before.SnapshotJson, after.SnapshotJson);
            Assert.AreEqual(before.CurrentFile, after.CurrentFile);
            Assert.AreEqual(0, Directory.GetFiles(Root, "*.tmp").Length);
        }
        public void AssertPersistedEnabled(bool expected)
        {
            using var json = JsonDocument.Parse(File.ReadAllText(FilePath));
            Assert.AreEqual(expected, json.RootElement.GetProperty("configuration").GetProperty("enabled").GetBoolean());
        }
        public void Dispose()
        {
            string resolved = Path.GetFullPath(Root);
            if (!string.Equals(Path.GetDirectoryName(resolved), Temp, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(resolved).StartsWith("XisuraAutomationIntent-", StringComparison.Ordinal) ||
                (File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Unexpected fixture cleanup path.");
            foreach (string file in Directory.GetFiles(resolved))
            {
                string name = Path.GetFileName(file);
                if (!name.StartsWith("adaptive.json", StringComparison.Ordinal) &&
                    !(name.StartsWith(".adaptive.json.", StringComparison.Ordinal) && name.EndsWith(".tmp", StringComparison.Ordinal)))
                    throw new IOException("Unexpected fixture file; retained for inspection.");
                File.Delete(file);
            }
            Directory.Delete(resolved);
        }
    }
}
