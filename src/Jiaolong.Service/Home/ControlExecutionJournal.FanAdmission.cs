namespace Jiaolong.Service.Home;

public sealed partial class ControlExecutionJournal
{
    internal sealed record FanRecoveryOperation(Guid OperationId, FanEcRecoveryScope Scope);
    private readonly AsyncLocal<FanRecoveryAdmission?> fanRecoveryAdmission = new();
    internal FanRecoveryOperation? CurrentFanRecoveryOperation => fanRecoveryAdmission.Value is { Active: true } value ? value.Operation : null;
    internal IDisposable BindFanRecovery(FanRecoveryOperation operation)
    {
        var value = new FanRecoveryAdmission(this, operation, fanRecoveryAdmission.Value);
        fanRecoveryAdmission.Value = value;
        return value;
    }
    private sealed class FanRecoveryAdmission(ControlExecutionJournal owner, FanRecoveryOperation operation, FanRecoveryAdmission? prior) : IDisposable
    {
        private int active = 1;
        public bool Active => Volatile.Read(ref active) != 0;
        public FanRecoveryOperation Operation { get; } = operation;
        public void Dispose() { if (Interlocked.Exchange(ref active, 0) != 0) owner.fanRecoveryAdmission.Value = prior; }
    }
}
