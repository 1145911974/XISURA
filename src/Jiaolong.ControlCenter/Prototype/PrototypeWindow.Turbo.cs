using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.Services;

namespace Jiaolong_ControlCenter.Prototype;

public sealed partial class PrototypeWindow
{
    private TurboBranchController? turboBranch;

    private async Task ExitTurboBranchAsync(CancellationToken token)
    {
        try { if (turboBranch is not null) await turboBranch.ReleaseAsync(token); }
        finally
        {
            GpuWorkspaceV2.ResumePresetFollowing();
            PageWorkspace.ResumeFanPresetFollowing();
            SetTurboTierVisual("Normal");
        }
    }

    private async Task ReleaseInvalidatedTurboBranchAsync()
    {
        isModeCommandPending = true;
        HomeModeBar.IsCommandPending = true;
        trayQuickConsole?.SetModeBusy(true);
        try
        {
            await ExitTurboBranchAsync(lifetimeCancellation.Token);
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
        if (isModeCommandPending) { queuedTurboTier = tier; return; }
        isModeCommandPending = true;
        HomeModeBar.IsCommandPending = true;
        trayQuickConsole?.SetModeBusy(true);
        bool normalReady = false;
        try
        {
            if (!await PauseAdaptiveForManualControlAsync()) return;
            homeSession.SupersedeAutomaticRestore();
            turboBranch ??= new TurboBranchController(() => homeSession.State, homeSession.ExecuteAsync);
            if (tier == "Normal")
            {
                await ExitTurboBranchAsync(lifetimeCancellation.Token);
                normalReady = true;
            }
            else
            {
                await Task.WhenAll(GpuWorkspaceV2.PausePresetFollowingAsync(lifetimeCancellation.Token),
                    PageWorkspace.PauseFanPresetFollowingAsync(lifetimeCancellation.Token));
                await turboBranch.ApplyAsync(tier, lifetimeCancellation.Token);
                state.Mode = PrototypePerformanceMode.Turbo;
                bool confirmed = turboBranch.LastResult is { VerifiedPerformanceMode: PerformanceMode.Turbo,
                    ExecutionState.NativeModeApplyState: Jiaolong.Contracts.Models.ApplyState.Confirmed };
                if (confirmed)
                {
                    appliedMode.Confirm(state.Mode);
                    PublishConfirmedMode(ControlModeId.Turbo, null, animate: true);
                }
                else if (!allowClose) _ = ShowCustomProfileStatusAsync("已提交，实际生效未确认", WindowSurface.XamlRoot);
                RequestModeVisuals(state.Mode, animate: true);
                SetTurboTierVisual(tier);
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
            if (turboBranch?.ActiveTier is null)
            {
                if (turboBranch?.HasOwnership == true)
                    try { await turboBranch.ReleaseAsync(CancellationToken.None); }
                    catch (Exception error) { AppRuntimeLog.Write($"[{DateTimeOffset.Now:O}] Turbo branch cleanup: {error}\n"); }
                GpuWorkspaceV2.ResumePresetFollowing();
                PageWorkspace.ResumeFanPresetFollowing();
            }
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
