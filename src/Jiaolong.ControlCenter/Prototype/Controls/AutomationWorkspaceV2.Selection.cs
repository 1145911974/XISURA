using Jiaolong_ControlCenter.Services;

namespace Jiaolong_ControlCenter.Prototype.Controls;

public sealed partial class AutomationWorkspaceV2
{
    public Task<bool> SelectSavedStrategyAsync(AdaptiveStrategyId strategy)
    {
        if (!Enum.IsDefined(strategy))
        {
            ReportStatus("策略未切换：策略身份无效。");
            return Task.FromResult(false);
        }
        return ConfirmAutomationSelectionAsync(strategy, LoadMap(strategy), LoadPolicy(strategy), autoEnabled, changeEnabled: false);
    }

    public Task<bool> ConfirmAutomationEnabledAsync(bool enabled) =>
        ConfirmAutomationSelectionAsync(selection.Active, activeMap, activePolicy, enabled, changeEnabled: true);

    private async Task WaitForServiceReadyAsync(CancellationToken cancellationToken)
    {
        while (automationService!.Status != HomeSessionStatus.Connected || !automationService.ConfigurationRestorationSettled)
            await Task.Delay(100, cancellationToken);
    }

    private async Task<bool> ConfirmAutomationSelectionAsync(AdaptiveStrategyId strategy,
        AdaptiveTargetMap map, AdaptiveTriggerPolicy policy, bool enabled, bool changeEnabled)
    {
        string action = changeEnabled ? "自适应状态" : "策略";
        if (automationService is null || servicePresetReader is null || serviceClientLifetime.IsCancellationRequested)
        {
            ReportStatus($"{action}未切换：系统服务或已保存预设尚未就绪。");
            return false;
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(serviceClientLifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        bool acquired = false;
        bool submitted = false;
        int revision = configurationRevision;
        var previousEditing = selection.Editing;
        try
        {
            await WaitForServiceReadyAsync(deadline.Token);
            await serviceConfigurationGate.WaitAsync(deadline.Token);
            acquired = true;
            revision = ++configurationRevision;
            servicePublishBusy = true;
            RenderSelection();
            var configuration = await BuildServiceConfigurationAsync(strategy, policy, map, enabled, deadline.Token);
            if (revision != configurationRevision) throw new InvalidOperationException("调度设置已更新，请重试");
            submitted = true;
            await ConfirmServiceConfigurationAsync(configuration, deadline.Token);
            if (revision != configurationRevision) throw new InvalidOperationException("调度设置已更新，请重试");
            preferences.Update(current => changeEnabled
                ? current with { AdaptiveModeEnabled = enabled }
                : current with { ActiveAdaptiveStrategy = strategy });
            if (changeEnabled)
            {
                autoEnabled = enabled;
            }
            else
            {
                selection.Choose(strategy);
                selection.UseEditing();
                selection.Choose(previousEditing);
                activeMap = map;
                activePolicy = policy;
            }
            servicePublishPending = false;
            serviceSubmissionError = null;
            RenderReason();
            RefreshTelemetry();
            ReportStatus(changeEnabled ? "自适应状态已读回确认。" : "已确认调度策略；自适应启用状态保持不变。");
            return true;
        }
        catch (Exception exception)
        {
            bool restored = !submitted;
            if (submitted && revision == configurationRevision)
            {
                try
                {
                    using var recovery = CancellationTokenSource.CreateLinkedTokenSource(serviceClientLifetime.Token);
                    recovery.CancelAfter(TimeSpan.FromSeconds(20));
                    var original = await BuildServiceConfigurationAsync(selection.Active, activePolicy, activeMap, autoEnabled, recovery.Token);
                    if (revision == configurationRevision)
                    {
                        await ConfirmServiceConfigurationAsync(original, recovery.Token);
                        restored = true;
                    }
                }
                catch (Exception) { servicePublishPending = true; }
            }
            serviceSubmissionError = $"{action}未切换：{exception.Message}" + (restored ? "" : "；正在重新同步当前策略");
            if (!restored) servicePublishPending = true;
            serviceRetryAfter = DateTimeOffset.MinValue;
            ReportStatus(serviceSubmissionError);
            return false;
        }
        finally
        {
            if (acquired)
            {
                servicePublishBusy = false;
                serviceConfigurationGate.Release();
            }
            RenderSelection();
            RefreshServiceStatus();
            RefreshServiceConnection();
        }
    }
}
