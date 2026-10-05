namespace Jiaolong.Automation;

public sealed record AutomationTransition(
    AutomationState Previous,
    AutomationState Current,
    DateTimeOffset ObservedAtUtc,
    IReadOnlyList<PolicyReason> Reasons);

public sealed class AutomationEngine(PolicyArbiter arbiter)
{
    public PolicyDecision Decide(
        AutomationInputs inputs,
        AutomationState previous,
        DateTimeOffset now) => arbiter.Decide(inputs, previous, now);

    public Task ReleaseAsync(
        AutomationReleaseReason reason,
        Action<AutomationReleaseReason> release)
    {
        ArgumentNullException.ThrowIfNull(release);
        release(reason);
        return Task.CompletedTask;
    }
}
