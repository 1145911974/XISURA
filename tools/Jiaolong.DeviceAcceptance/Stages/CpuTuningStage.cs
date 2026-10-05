namespace Jiaolong.DeviceAcceptance.Stages;

public static class CpuTuningStage
{
    public static Task<int> RunAsync(CancellationToken cancellationToken) =>
        AcceptancePrompt.RunAsync("cpu-tuning", cancellationToken);
}
