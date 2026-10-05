namespace Jiaolong.DeviceAcceptance.Stages;

public static class FanStage
{
    public static Task<int> RunAsync(CancellationToken cancellationToken) =>
        AcceptancePrompt.RunAsync("fan", cancellationToken);
}
