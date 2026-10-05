using Jiaolong.DeviceAcceptance.Stages;

if (args.Length != 2 || !string.Equals(args[0], "--stage", StringComparison.Ordinal))
{
    Console.Error.WriteLine("Usage: Jiaolong.DeviceAcceptance --stage read-only|low-risk|mux|fan|automation-recovery|cpu-tuning|gpu-limit|curve-optimizer");
    return 2;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

return args[1] switch
{
    "read-only" => await ReadOnlyStage.RunAsync(cancellation.Token),
    "low-risk" => await LowRiskStage.RunAsync(cancellation.Token),
    "mux" => await MuxStage.RunAsync(cancellation.Token),
    "fan" => await FanStage.RunAsync(cancellation.Token),
    "automation-recovery" => await AutomationRecoveryStage.RunAsync(cancellation.Token),
    "cpu-tuning" => await CpuTuningStage.RunAsync(cancellation.Token),
    "gpu-limit" => await GpuLimitStage.RunAsync(cancellation.Token),
    "curve-optimizer" => await CurveOptimizerStage.RunAsync(cancellation.Token),
    _ => 2
};
