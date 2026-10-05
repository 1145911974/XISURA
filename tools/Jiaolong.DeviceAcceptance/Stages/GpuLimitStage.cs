namespace Jiaolong.DeviceAcceptance.Stages;

public static class GpuLimitStage
{
    public static Task<int> RunAsync(CancellationToken cancellationToken) =>
        AcceptancePrompt.RunAsync("gpu-limit", cancellationToken);
}
