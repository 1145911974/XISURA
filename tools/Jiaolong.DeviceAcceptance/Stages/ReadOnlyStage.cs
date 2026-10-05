namespace Jiaolong.DeviceAcceptance.Stages;

public static class ReadOnlyStage
{
    public static Task<int> RunAsync(CancellationToken cancellationToken) =>
        AcceptancePrompt.RunAsync("read-only", cancellationToken);
}
