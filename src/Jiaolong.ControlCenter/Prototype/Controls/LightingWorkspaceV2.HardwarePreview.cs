using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.Services;

namespace Jiaolong_ControlCenter.Prototype.Controls;

public sealed partial class LightingWorkspaceV2
{
    private readonly SemaphoreSlim hardwarePreviewGate = new(1, 1);
    private CancellationTokenSource? pendingHardwarePreview;
    private KeyboardLightingPlan? pendingRealPlan;

    private void CancelPendingHardwarePreview()
    {
        pendingHardwarePreview?.Cancel();
        pendingHardwarePreview?.Dispose();
        pendingHardwarePreview = null;
        pendingRealPlan = null;
    }

    private void ScheduleHardwarePreview()
    {
        CancelPendingHardwarePreview();
        if (PresetToolbar.IsEditingPreset || !pageActive || !lightingAvailable || session is null || applying || !draft.IsValid() || lightingSliderDragging || colorDragging) return;
        pendingHardwarePreview = new();
        pendingRealPlan = draft.ToPlan() with { LogoEnabled = null };
        _ = PreviewAfterEditAsync(pendingRealPlan, pendingHardwarePreview.Token);
    }

    private async Task PreviewAfterEditAsync(KeyboardLightingPlan plan, CancellationToken token)
    {
        try
        {
            await Task.Delay(150, token);
            await CommitIndependentLightingAsync(plan, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            AppRuntimeLog.Write($"[{DateTimeOffset.Now:O}] Current lighting adjustment: {error}\n");
            await PresetToolbar.ShowStatusAsync("灯光调节或当前设置记录未完成，请检查连接和存储");
        }
        finally
        {
            if (pendingHardwarePreview?.Token == token)
            { pendingHardwarePreview.Dispose(); pendingHardwarePreview = null; pendingRealPlan = null; }
        }
    }

    private async Task CommitIndependentLightingAsync(KeyboardLightingPlan plan, CancellationToken token)
    {
        var result = await ApplySavedLightingAsync(plan, () => !followSettingPending, token);
        if (result is { State: CommandState.Applied, Error: null })
        {
            independentDraft = LightingDraft.FromPlan(plan);
            preferences.Update(current => current with { IndependentLighting = plan });
            followStatus = null;
            PresetToolbar.SetCurrentSettingsModified();
            HardwareStatusText.Text = "当前灯光设置已调整";
            // Manual adjustments stay in effect until the next mode/target change.
            if (session?.State is { } current)
                lastAutomaticTarget = LightingPresetPolicy.ResolveTarget(current.Controls, lightingSlots, confirmedCustomLighting, confirmedPerformanceLighting);
        }
        else
        {
            independentDraft = LightingDraft.FromPlan(appliedLightingPlan);
            if (!PresetToolbar.IsEditingPreset && independentDraft is { } actual) { draft = actual; Render(); }
            await PresetToolbar.ShowStatusAsync("灯光调节未生效，已保留设备实际设置");
        }
    }

    // Existing lifetime callers now flush live edits; management never writes device previews.
    public async Task RestoreHardwarePreviewAsync()
    {
        if (pendingHardwarePreview is null) return;
        var current = pendingRealPlan;
        CancelPendingHardwarePreview();
        if (current is null) return;
        try { await CommitIndependentLightingAsync(current, CancellationToken.None); }
        catch (Exception error) { AppRuntimeLog.Write($"[{DateTimeOffset.Now:O}] Lighting leave flush: {error}\n"); }
    }

    private async Task<CommandResult> ApplySavedLightingAsync(KeyboardLightingPlan plan,
        Func<bool>? stillCurrent = null, CancellationToken token = default)
    {
        if (stillCurrent is null) CancelPendingHardwarePreview();
        await hardwarePreviewGate.WaitAsync(token);
        try
        {
            if (stillCurrent is not null && !stillCurrent() || plan.Effect == "Cycle" && !nativeCycleAvailable || session is null) return RejectedLighting();
            var result = await session.ExecuteAsync(new SetKeyboardLightingCommand(Guid.NewGuid(), plan), token);
            if (result.State == CommandState.Applied && result.Error is null) appliedLightingPlan = plan;
            return result;
        }
        finally { hardwarePreviewGate.Release(); }
    }
}
