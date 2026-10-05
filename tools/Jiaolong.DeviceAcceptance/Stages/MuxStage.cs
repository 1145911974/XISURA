namespace Jiaolong.DeviceAcceptance.Stages;

public static class MuxStage
{
    public static Task<int> RunAsync(CancellationToken cancellationToken) =>
        AcceptancePrompt.RunAsync("mux", cancellationToken);
}
