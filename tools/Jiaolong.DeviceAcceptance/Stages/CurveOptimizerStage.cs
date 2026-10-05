namespace Jiaolong.DeviceAcceptance.Stages;

public static class CurveOptimizerStage
{
    public static Task<int> RunAsync(CancellationToken cancellationToken) =>
        AcceptancePrompt.RunAsync("curve-optimizer", cancellationToken);
}
