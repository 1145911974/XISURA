namespace Jiaolong.DeviceAcceptance.Stages;

public static class LowRiskStage
{
    public static Task<int> RunAsync(CancellationToken cancellationToken) =>
        AcceptancePrompt.RunAsync("low-risk", cancellationToken);
}
