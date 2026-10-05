using Jiaolong.Contracts.Models;

namespace Jiaolong_ControlCenter.ViewModels;

public sealed class AutomationViewModel
{
    public AutomationProfile? CurrentProfile { get; private set; }
    public PerformanceMode? ManualOverrideMode { get; private set; }
    public DateTimeOffset? ManualOverrideExpiresAtUtc { get; private set; }
    public IReadOnlyList<string> PriorityTrace => AutomationPriority.DisplayOrder;
    public string ForegroundStatus { get; private set; } = "前台应用触发暂不可用";

    public Task SetPolicyAsync(AutomationProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        cancellationToken.ThrowIfCancellationRequested();
        CurrentProfile = profile;
        return Task.CompletedTask;
    }

    public Task SetManualOverrideAsync(PerformanceMode mode, TimeSpan duration, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromHours(24)) throw new ArgumentOutOfRangeException(nameof(duration));
        ManualOverrideMode = mode;
        ManualOverrideExpiresAtUtc = DateTimeOffset.UtcNow.Add(duration);
        return Task.CompletedTask;
    }
}
