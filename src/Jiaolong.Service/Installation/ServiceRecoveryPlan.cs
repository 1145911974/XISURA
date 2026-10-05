namespace Jiaolong.Service.Installation;

public readonly record struct ServiceRecoveryAction(uint Type, uint DelayMilliseconds);

public static class ServiceRecoveryPlan
{
    public const string ServiceName = "JiaolongControlService";
    public static readonly uint ResetPeriodSeconds = 86400;
    public static readonly uint RestartActionType = 1;

    public static IReadOnlyList<ServiceRecoveryAction> Actions { get; } =
    [
        new(RestartActionType, 1000),
        new(RestartActionType, 5000),
        new(RestartActionType, 30000)
    ];
}
