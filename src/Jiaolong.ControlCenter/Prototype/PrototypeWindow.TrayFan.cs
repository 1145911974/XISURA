using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;

namespace Jiaolong_ControlCenter.Prototype;

public sealed partial class PrototypeWindow
{
    private bool trayFanCommandPending;

    private void RefreshTrayFanState(HomeStateSnapshot? snapshot)
    {
        if (trayQuickConsole is null) return;
        var plan = snapshot?.Controls.ActiveFanControlPlan;
        bool connected = snapshot is not null && homeSession.Status == Services.HomeSessionStatus.Connected;
        trayQuickConsole.ApplyFanState(connected,
            connected && snapshot!.Controls.FanAutomaticCeilingAvailable,
            connected && HasCapability(snapshot!, "fanControl"),
            plan?.Strategy == "Auto" ? plan.MaximumRpm : null,
            plan?.Strategy == "Fixed" ? plan.FixedRpm : null,
            snapshot?.Controls.StrongCooling, maximumRpm: 5800);
        trayQuickConsole.SetFanBusy(trayFanCommandPending || pendingQuickSettings.ShouldPreserve(QuickSettingKind.StrongCooling, null));
    }

    private async Task ApplyTrayFanAsync(int? automaticCeiling, int? fixedRpm = null)
    {
        if (trayFanCommandPending || allowClose || customActivationPending || isModeCommandPending)
        {
            RefreshTrayFanState(homeSession.State);
            trayQuickConsole?.ShowStatus("正在切换，请稍后调整风扇");
            return;
        }
        if (automaticCeiling is not null && automaticCeiling is < 1800 or > 5800 ||
            fixedRpm is not null && fixedRpm is < 1800 or > 5800) return;
        trayFanCommandPending = true;
        RefreshTrayFanState(homeSession.State);
        try
        {
            FanControlPlan? plan = automaticCeiling is null && fixedRpm is null ? null :
                new FanControlPlan([new FanPoint(40, 60), new FanPoint(95, 100)])
                { Strategy = fixedRpm.HasValue ? "Fixed" : "Auto", FixedRpm = fixedRpm, MaximumRpm = automaticCeiling };
            HardwareCommand command = plan is null
                ? new ReleaseFanControlCommand(Guid.NewGuid(), ReleaseReason.UserRequested)
                : new SetFanControlCommand(Guid.NewGuid(), plan, RiskConfirmed: true);
            var result = await homeSession.ExecuteAsync(command, lifetimeCancellation.Token);
            var actual = homeSession.State?.Controls.ActiveFanControlPlan;
            bool matches = plan is null ? actual is null : actual?.Strategy == plan.Strategy &&
                actual.FixedRpm == plan.FixedRpm && actual.MaximumRpm == plan.MaximumRpm;
            bool confirmed = result.State == CommandState.Applied && result.Error is null && matches;
            trayQuickConsole?.ShowStatus(confirmed ? "" : "风扇设置未确认，已保留实际状态");
        }
        catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested) { }
        catch (Exception error)
        {
            Services.AppRuntimeLog.Write($"[{DateTimeOffset.Now:O}] Tray fan: {error}\n");
            trayQuickConsole?.ShowStatus("风扇设置失败，请检查服务连接");
        }
        finally
        {
            trayFanCommandPending = false;
            RefreshTrayFanState(homeSession.State);
        }
    }
}
