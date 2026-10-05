namespace Jiaolong.DeviceAcceptance.Stages;

public static class AutomationRecoveryStage
{
    public static Task<int> RunAsync(CancellationToken cancellationToken) =>
        AcceptancePrompt.RunAsync("automation-recovery", cancellationToken);
}
