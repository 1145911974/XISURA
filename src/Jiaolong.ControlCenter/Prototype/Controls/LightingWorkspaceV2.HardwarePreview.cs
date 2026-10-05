using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Microsoft.UI.Xaml;

namespace Jiaolong_ControlCenter.Prototype.Controls;

public sealed partial class LightingWorkspaceV2
{
    private readonly SemaphoreSlim hardwarePreviewGate = new(1, 1);
    private readonly DispatcherTimer hardwarePreviewHeartbeat = new() { Interval = TimeSpan.FromSeconds(2) };
    private CancellationTokenSource? pendingHardwarePreview;
    private KeyboardLightingPlan? hardwarePreviewPlan;
    private bool hardwarePreviewOwned;

    private void InitializeHardwarePreview()
    {
        hardwarePreviewHeartbeat.Tick += async (_, _) =>
        {
            if (hardwarePreviewOwned && hardwarePreviewPlan is { } plan && pageActive && !applying && XamlRoot?.IsHostVisible == true)
                await ApplyHardwarePreviewAsync(plan, CancellationToken.None, heartbeat: true);
        };
    }

    private void CancelPendingHardwarePreview()
    {
        pendingHardwarePreview?.Cancel();
        pendingHardwarePreview?.Dispose();
        pendingHardwarePreview = null;
    }

    private void ScheduleHardwarePreview()
    {
        CancelPendingHardwarePreview();
        if (!pageActive || !lightingAvailable || followPreset && !hardwarePreviewAvailable || session is null || applying || !draft.IsValid()) return;
        pendingHardwarePreview = new();
        _ = PreviewAfterEditAsync(draft.ToPlan() with { LogoEnabled = null }, pendingHardwarePreview.Token);
    }

    private async Task PreviewAfterEditAsync(KeyboardLightingPlan plan, CancellationToken token)
    {
        try
        {
            await Task.Delay(150, token);
            if (followPreset) await ApplyHardwarePreviewAsync(plan, token);
            else await CommitIndependentLightingAsync(plan, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            Jiaolong_ControlCenter.Services.AppRuntimeLog.Write($"[{DateTimeOffset.Now:O}] Independent lighting: {error}\n");
            await PresetToolbar.ShowStatusAsync("独立灯效或保存未完成，请检查连接和存储");
        }
        finally
        {
            if (pendingHardwarePreview?.Token == token)
            { pendingHardwarePreview.Dispose(); pendingHardwarePreview = null; }
        }
    }

    private async Task CommitIndependentLightingAsync(KeyboardLightingPlan plan, CancellationToken token)
    {
        var result = await ApplySavedLightingAsync(plan, () => !followPreset && !followSettingPending, token);
        if (result is { State: CommandState.Applied, Error: null } && !followPreset)
        {
            preferences.Update(current => current with { IndependentLighting = plan });
            HardwareStatusText.Text = "独立灯效 · 已在设备上生效";
        }
        else await PresetToolbar.ShowStatusAsync("独立灯效未生效，请检查连接");
    }

    private async Task ApplyHardwarePreviewAsync(KeyboardLightingPlan plan, CancellationToken token, bool heartbeat = false)
    {
        await hardwarePreviewGate.WaitAsync(token);
        try
        {
            // A queued heartbeat must renew the latest preview, never replay its earlier captured plan.
            if (heartbeat)
            {
                if (!hardwarePreviewOwned || hardwarePreviewPlan is null) return;
                plan = hardwarePreviewPlan;
            }
            if (plan.Effect == "Cycle" && !nativeCycleAvailable || !followPreset || followSettingPending || !pageActive || !IsLoaded || !lightingAvailable || !hardwarePreviewAvailable || session is null || applying || XamlRoot?.IsHostVisible != true) return;
            hardwarePreviewOwned = true; // A lost reply still requires service-side lease recovery.
            var result = await session.ExecuteAsync(new SetKeyboardLightingCommand(Guid.NewGuid(), plan) { Preview = true }, token);
            if (result.State == CommandState.Applied && result.Error is null)
            {
                hardwarePreviewPlan = plan;
                hardwarePreviewHeartbeat.Start();
                HardwareStatusText.Text = "正在设备上预览 · 离开页面后恢复正在使用的灯效";
            }
            else
            {
                hardwarePreviewHeartbeat.Stop();
                await PresetToolbar.ShowStatusAsync("设备预览未生效，请检查服务或其他控制台");
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception)
        {
            hardwarePreviewHeartbeat.Stop();
            await PresetToolbar.ShowStatusAsync("设备预览通信失败，服务会恢复原灯效");
        }
        finally { hardwarePreviewGate.Release(); }
    }

    public async Task RestoreHardwarePreviewAsync()
    {
        if (!followPreset && pendingHardwarePreview is not null && independentDraft is { } independent)
        {
            CancelPendingHardwarePreview();
            try { await CommitIndependentLightingAsync(independent.ToPlan() with { LogoEnabled = null }, CancellationToken.None); }
            catch (Exception error) { Jiaolong_ControlCenter.Services.AppRuntimeLog.Write($"[{DateTimeOffset.Now:O}] Lighting leave flush: {error}\n"); }
        }
        else if (followPreset) CancelPendingHardwarePreview();
        hardwarePreviewHeartbeat.Stop();
        await hardwarePreviewGate.WaitAsync();
        try
        {
            if (!hardwarePreviewOwned || session is null) return;
            var result = await session.ExecuteAsync(new RestoreKeyboardLightingPreviewCommand(Guid.NewGuid()), CancellationToken.None);
            if (result.State == CommandState.Applied && result.Error is null)
            {
                hardwarePreviewOwned = false;
                hardwarePreviewPlan = null;
            }
        }
        catch (Exception) { /* The service expires the lease even if the window has gone. */ }
        finally { hardwarePreviewGate.Release(); }
    }

    private async Task<Jiaolong.Contracts.Commands.CommandResult> ApplySavedLightingAsync(KeyboardLightingPlan plan,
        Func<bool>? stillCurrent = null, CancellationToken token = default)
    {
        if (stillCurrent is null) CancelPendingHardwarePreview();
        hardwarePreviewHeartbeat.Stop();
        await hardwarePreviewGate.WaitAsync(token);
        try
        {
            if (stillCurrent is not null && !stillCurrent() || plan.Effect == "Cycle" && !nativeCycleAvailable) return RejectedLighting();
            var result = await session!.ExecuteAsync(new SetKeyboardLightingCommand(Guid.NewGuid(), plan), token);
            if (result.State == CommandState.Applied && result.Error is null)
            {
                hardwarePreviewOwned = false;
                hardwarePreviewPlan = null;
            }
            return result;
        }
        finally { hardwarePreviewGate.Release(); }
    }
}
