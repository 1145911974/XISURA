using System.Text.Json;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jiaolong.Service.Home;

public sealed class NativeModeLightingFollowWorker(AdaptiveAutomationStateStore store, HomeServiceRuntime runtime,
    AdaptiveAutomationHardwareProvider hardware, ILogger<NativeModeLightingFollowWorker> logger) : BackgroundService
{
    private bool initialized;
    private bool configuredFollow;
    private bool pendingEnable;
    private long epoch;
    private string? owner;
    private PerformanceMode? lastMode;
    private PerformanceMode? candidate;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        do
        {
            try { await TickAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or JsonException)
            {
                logger.LogWarning(error, "Native mode lighting follow could not evaluate the current target");
                await Task.Delay(1000, stoppingToken);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task TickAsync(CancellationToken token)
    {
        if (runtime.IsSuspended) { initialized = false; return; }
        if (runtime.State.SupportState != DeviceSupportState.Ready) return;
        var saved = await store.ReadAsync(token);
        if (saved.Recovery is not null || !runtime.CanRunClientAutomation(saved.OwnerSid))
        { initialized = configuredFollow = pendingEnable = false; candidate = null; return; }
        bool enabled = NativeModeLightingFollowPolicy.IsEnabled(saved);
        if (initialized && !enabled) { configuredFollow = pendingEnable = false; candidate = null; return; }
        var observed = (await hardware.ReadNativeModeEvidenceAsync(token))?.ParsedMode;
        if (observed is not (PerformanceMode.Quiet or PerformanceMode.Balanced or PerformanceMode.Turbo)) return;
        long currentEpoch = runtime.Execution.HardwareEpoch;
        if (!initialized || epoch != currentEpoch || owner != saved.OwnerSid)
        {
            // Service startup, wake and ownership changes establish a baseline without replaying a preset.
            initialized = true; epoch = currentEpoch; owner = saved.OwnerSid; lastMode = observed;
            configuredFollow = enabled; pendingEnable = false; candidate = null; return;
        }
        if (!configuredFollow) pendingEnable = true;
        configuredFollow = true;
        if (observed == lastMode && !pendingEnable) { candidate = null; return; }
        if (candidate != observed)
        {
            candidate = observed;
            // Establish the runtime's external-mode observation before admitting a lighting intent.
            await runtime.GetHomeStateAsync(token);
            return;
        }
        if (!runtime.Execution.IsIdle) return;

        var snapshot = await runtime.GetHomeStateAsync(token);
        if (snapshot.Controls.ModeEvidence?.ParsedMode != observed) { candidate = null; return; }
        saved = await store.ReadAsync(token);
        var target = NativeModeLightingFollowPolicy.Resolve(saved, observed.Value);
        if (target is null || saved.OwnerSid != owner) return;
        lastMode = observed; candidate = null; pendingEnable = false;
        if (snapshot.Controls.ExecutionState is { NativeModeApplyState: ApplyState.Confirmed } executed &&
            executed.NativeMode == observed && executed.Pages.Any(page => page.Page == ControlPageId.Lighting && page.Key == target.Selection.Key)) return;
        if (AlreadyMatches(target.Envelope, snapshot.Controls)) return;
        var result = await hardware.ExecuteModeLightingFollowAsync(runtime, observed.Value, saved.Revision, token);
        if (result.State != CommandState.Applied || result.Error is not null)
            logger.LogWarning("Native mode lighting follow ended with {state}, {error}", result.State, result.Error?.Code);
    }

    private static bool AlreadyMatches(PagePresetEnvelope preset, HomeControlState controls)
    {
        var actual = ControlPlanCompiler.Read(ControlPageId.Lighting, controls);
        foreach (var (field, setting) in preset.Settings.Where(pair => !pair.Key.Equals("logo", StringComparison.OrdinalIgnoreCase)))
        {
            if (setting.Intent == SettingIntent.Keep) continue;
            if (setting.Intent != SettingIntent.Set) return false;
            var value = actual.GetValueOrDefault(field);
            if (field == "effect" && setting.Value is { ValueKind: JsonValueKind.String } expected && value?.Value is { ValueKind: JsonValueKind.String } current)
            {
                string? Normalize(string? effect) => effect == "Fixed" ? "Static" : effect;
                if (Normalize(expected.GetString()) != Normalize(current.GetString())) return false;
            }
            else if (!ControlPlanCompiler.Same(setting, value)) return false;
        }
        return true;
    }
}

internal sealed record NativeModeLightingTarget(PageSelection Selection, PagePresetEnvelope Envelope);

internal static class NativeModeLightingFollowPolicy
{
    internal static bool IsEnabled(AdaptiveAutomationStoreSnapshot saved) =>
        saved.Recovery is null && !string.IsNullOrWhiteSpace(saved.OwnerSid) && saved.Configuration is { Enabled: false } configuration &&
        configuration.Presets.Any(preset => preset.Selections?.Any(page => page.Page == ControlPageId.Lighting && page.Follow) == true);

    internal static NativeModeLightingTarget? Resolve(AdaptiveAutomationStoreSnapshot saved, PerformanceMode observed)
    {
        if (!IsEnabled(saved)) return null;
        ControlModeId? mode = observed switch
        {
            PerformanceMode.Quiet => ControlModeId.Office, PerformanceMode.Balanced => ControlModeId.Gaming,
            PerformanceMode.Turbo => ControlModeId.Turbo, _ => null
        };
        if (mode is null) return null;
        var preset = saved.Configuration!.Presets.SingleOrDefault(item => item.Key == PresetKey.Create(mode.Value, 1));
        var selection = preset?.Selections?.SingleOrDefault(item => item.Page == ControlPageId.Lighting && item.Follow && item.Key.Mode == mode);
        if (selection is null || selection.Revision <= 0) return null;
        var envelope = preset!.Pages?.SingleOrDefault(item => item.Page == ControlPageId.Lighting && item.Key == selection.Key && item.Revision == selection.Revision);
        if (envelope is null || envelope.SchemaVersion > PagePresetEnvelope.CurrentSchemaVersion ||
            !PresetValidation.Validate(envelope).IsValid || envelope.Payload.ValueKind != JsonValueKind.Object) return null;
        var effect = envelope.Payload.EnumerateObject().FirstOrDefault(item => item.Name.Equals("effect", StringComparison.OrdinalIgnoreCase)).Value;
        if (effect.ValueKind != JsonValueKind.String || effect.GetString() is not ("Static" or "Fixed" or "Gradient")) return null;
        return new(selection, envelope);
    }
}
