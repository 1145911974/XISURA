using Jiaolong.Contracts.Models;

namespace Jiaolong.Service.Home;

public sealed partial class HomeServiceRuntime
{
    private sealed record PriorExitMode(ExitStep Step, NativeModeRecoveryAttempt Attempt);

    private static PriorExitMode? PriorExitModeAttempt(ClientControlSessionSnapshot session, ExitPlan plan, ControlJournalSnapshot saved)
    {
        if (saved.Unresolved is not [var pending] || pending.OperationId != plan.OperationId ||
            pending.CommandKind != "EndClientControlSession" || pending.OwnerSid != session.OwnerSid || pending.Identity != session.Identity ||
            pending.NativeModeAttempt is not { } attempt || attempt.StepId == Guid.Empty ||
            attempt.ServiceSessionId != session.ServiceSessionId || attempt.HardwareEpoch != session.Handle.HardwareEpoch ||
            attempt.ClientSessionId != session.Handle.SessionId || attempt.OwnerSid != session.OwnerSid || attempt.Identity != session.Identity)
            return null;
        var step = plan.Steps.SingleOrDefault(item => item.Key == "mode");
        if (step is null || !ExitSame(step.Expected, SettingRequest.Set(attempt.OriginalBefore)) ||
            !ExitSame(step.Target, SettingRequest.Set(attempt.Target)) ||
            attempt.Before != attempt.OriginalBefore && attempt.Before != attempt.Target ||
            attempt.Observed is PerformanceMode observed && observed != attempt.Before && observed != attempt.Target ||
            step.Status == "Verified" && attempt.Observed != attempt.Target)
            return null;
        return new(step, attempt);
    }

    private static bool ExactCpuExitCheckpoint(ExitStep step)
    {
        if (step.TargetDetails is not { } target) return true;
        if (step.ExpectedDetails is not { } expected) return false;
        return step.Key switch
        {
            "performance.negativeCurveOptimizer" => target.PerCoreCurveOptimizer is { Count: > 0 } cores &&
                expected.PerCoreCurveOptimizer is { } before && cores.Keys.Order().SequenceEqual(before.Keys.Order()),
            "performance.isBoostEnabled" => target.AcBoostMode is not null && target.DcBoostMode is not null &&
                expected.AcBoostMode is not null && expected.DcBoostMode is not null,
            _ => false
        };
    }
}
