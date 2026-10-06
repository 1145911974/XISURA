using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.Services;

namespace Jiaolong_ControlCenter.Prototype;

public sealed partial class PrototypeWindow
{
    private TurboBranchController? turboBranch;

    private async Task ExitTurboBranchAsync(CancellationToken token)
    {
        if (turboBranch is not null) await turboBranch.ReleaseAsync(token);
        SetTurboTierVisual("Normal");
    }

    private async Task ReleaseInvalidatedTurboBranchAsync()
    {
        isModeCommandPending = true;
        HomeModeBar.IsCommandPending = true;
        trayQuickConsole?.SetModeBusy(true);
        try
        {
            if (turboBranch is not null) await turboBranch.ReleaseAsync(lifetimeCancellation.Token);
        }
        catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested) { }
        catch (Exception error)
        {
            AppRuntimeLog.Write($"[{DateTimeOffset.Now:O}] External mode change branch release: {error}\n");
            if (!allowClose)
                await ShowCustomProfileStatusAsync("模式已变更；原分支附属控制恢复未确认", WindowSurface.XamlRoot);
        }
        finally
        {
            isModeCommandPending = false;
            HomeModeBar.IsCommandPending = false;
            trayQuickConsole?.SetModeBusy(false);
            if (!allowClose && homeSession.State is { } current) ApplyHomeState(current);
            ResumeQueuedModeRequest();
        }
    }

    private void SetTurboTierVisual(string tier)
    {
        turboTier = tier;
        HomeModeBar.SetConfirmedTurboTier(tier);
        ModeAmbientBackdrop.SetTurboTier(state.Mode == PrototypePerformanceMode.Turbo, tier,
            motion.Translation > 0 ? TimeSpan.FromMilliseconds(220) : TimeSpan.Zero);
        Hero.ApplyPerformanceProfile(PerformanceRadarProfile.ForMode(state.Mode, tier, customProfile),
            motion.Translation > 0 ? TimeSpan.FromMilliseconds(220) : TimeSpan.Zero);
    }

    private async Task ApplyTurboTierAsync(string tier)
    {
        if (tier is not ("Normal" or "Quiet" or "Extreme")) return;
        if (!await PauseAdaptiveForManualControlAsync()) return;
        isModeCommandPending = true;
        HomeModeBar.IsCommandPending = true;
        trayQuickConsole?.SetModeBusy(true);
        bool normalReady = false;
        try
        {
            homeSession.SupersedeAutomaticRestore();
            turboBranch ??= new TurboBranchController(() => homeSession.State, homeSession.ExecuteAsync);
            if (tier == "Normal")
            {
                await ExitTurboBranchAsync(lifetimeCancellation.Token);
                normalReady = true;
            }
            else
            {
                await turboBranch.ApplyAsync(tier, lifetimeCancellation.Token);
                state.Mode = PrototypePerformanceMode.Turbo;
                appliedMode.Confirm(state.Mode);
                PublishConfirmedMode(ControlModeId.Turbo, null, animate: true);
                RequestModeVisuals(state.Mode, animate: true);
                SetTurboTierVisual(tier);
                PageWorkspace.ResetAutomaticModeTracking();
            }
        }
        catch (Exception error)
        {
            AppRuntimeLog.Write($"[{DateTimeOffset.Now:O}] Turbo branch {tier}: {error}\n");
            SetTurboTierVisual(turboBranch?.ActiveTier ?? "Normal");
            if (!allowClose)
                await ShowCustomProfileStatusAsync("狂飙分支未完整应用；请查看硬件回读与性能页反馈", WindowSurface.XamlRoot);
        }
        finally
        {
            isModeCommandPending = false;
            HomeModeBar.IsCommandPending = false;
            trayQuickConsole?.SetModeBusy(false);
            if (homeSession.State is { } current) ApplyHomeState(current);
            RestoreConfirmedModeVisuals();
            if (normalReady && queuedModeRequest is null && queuedTurboTier is null && !allowClose) queuedModeRequest = PrototypePerformanceMode.Turbo;
            ResumeQueuedModeRequest();
        }
    }
}
