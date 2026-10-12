using Jiaolong.Contracts.Models;

namespace Jiaolong.Service.Home;

public sealed record GpuAdapterIdentity(uint DeviceId, uint SubsystemId, uint RevisionId,
    uint ExternalDeviceId, uint BusId, uint BusSlotId);
public sealed record GpuPstateBaseline(int MemoryOffsetKhz, int MemoryMinimumKhz, int MemoryMaximumKhz,
    int CoreOffsetKhz, int CoreVoltageDeltaUv, int StateCount, int ClockCount);
public sealed record GpuRecoveryBaseline(DateTimeOffset CapturedAtUtc, Guid SessionId, long HardwareEpoch,
    string OwnerSid, HardwareIdentity Identity, GpuAdapterIdentity Adapter, int[] VfOffsetsKhz,
    GpuPstateBaseline Pstate);

public sealed partial class ControlExecutionJournal
{
    internal sealed record GpuOperation(Guid OperationId, Guid SessionId, long HardwareEpoch, string OwnerSid);
    private readonly AsyncLocal<GpuScope?> gpuOperation = new();
    internal GpuOperation? CurrentGpuOperation => gpuOperation.Value is { IsActive: true } scope ? scope.Operation : null;
    internal IDisposable BindGpuOperation(GpuOperation operation)
    {
        var scope = new GpuScope(this, operation, gpuOperation.Value);
        gpuOperation.Value = scope;
        return scope;
    }
    private sealed class GpuScope(ControlExecutionJournal owner, GpuOperation operation, GpuScope? prior) : IDisposable
    {
        private int active = 1;
        internal GpuOperation Operation => operation;
        internal bool IsActive => Volatile.Read(ref active) != 0;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref active, 0) != 0) owner.gpuOperation.Value = prior;
        }
    }

    internal async Task CheckGpuAdmissionAsync(GpuOperation operation, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try { CheckGpuAdmission(await LoadAsync(token), operation); }
        finally { gate.Release(); }
    }

    private static UnresolvedControlOperation CheckGpuAdmission(ControlJournalSnapshot saved, GpuOperation operation)
    {
        if (operation.OperationId == Guid.Empty || operation.SessionId == Guid.Empty || operation.HardwareEpoch <= 0 ||
            string.IsNullOrWhiteSpace(operation.OwnerSid)) throw new InvalidDataException("gpuRecoveryAdmissionMissing");
        foreach (var item in saved.Unresolved)
        {
            if (item.GpuRecovery is { } baseline && (item.OperationId != operation.OperationId ||
                baseline.SessionId != operation.SessionId || baseline.HardwareEpoch != operation.HardwareEpoch ||
                baseline.OwnerSid != operation.OwnerSid)) throw new IOException("gpuPersistedRecoveryRequired");
            if (item.OperationId != operation.OperationId && HasPendingGpuRecovery(item))
                throw new IOException("gpuPersistedRecoveryRequired");
        }
        var pending = saved.Unresolved.SingleOrDefault(item => item.OperationId == operation.OperationId);
        if (pending is null || pending.OwnerSid != operation.OwnerSid || pending.State.HardwareEpoch != operation.HardwareEpoch)
            throw new InvalidDataException("gpuRecoveryAdmissionMismatch");
        return pending;
    }

    internal async Task RecordGpuRecoveryAsync(Guid operationId, GpuRecoveryBaseline baseline, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            var saved = await LoadAsync(token);
            var pending = CheckGpuAdmission(saved, new(operationId, baseline.SessionId, baseline.HardwareEpoch, baseline.OwnerSid));
            if (!ValidGpuRecovery(baseline, pending)) throw new InvalidDataException("gpuRecoveryBaselineInvalid");
            if (pending.GpuRecovery is { } first)
            {
                // A parent can dispatch several GPU steps; retain its first pre-write state.
                if (first.Adapter != baseline.Adapter || first.Identity != baseline.Identity)
                    throw new InvalidDataException("gpuRecoveryIdentityChanged");
                baseline = first;
            }
            var next = Clone(saved with { Unresolved = saved.Unresolved.Select(item => item.OperationId == operationId
                ? item with { GpuRecovery = baseline } : item).ToArray() });
            await WriteSnapshotAsync(next, token);
            snapshot = next;
        }
        finally { gate.Release(); }
    }

    internal static bool ValidGpuAdapter(GpuAdapterIdentity? adapter) => adapter is { DeviceId: > 0 and < uint.MaxValue };
    internal static bool HasPendingGpuRecovery(UnresolvedControlOperation item) => item.GpuRecovery is not null ||
        item.CommandKind is nameof(Jiaolong.Contracts.Commands.SetGpuVfCurveCommand) or
            nameof(Jiaolong.Contracts.Commands.SetGpuCoreOffsetCommand) or nameof(Jiaolong.Contracts.Commands.SetGpuMemoryOffsetCommand) or
            nameof(Jiaolong.Contracts.Commands.SetGpuFrequencyLimitCommand) or nameof(Jiaolong.Contracts.Commands.SetGpuPowerLimitCommand) or
            nameof(Jiaolong.Contracts.Commands.SetGpuPowerPolicyCommand) ||
        item.State.Pages.Any(page => page.Page == ControlPageId.Gpu && page.State == ApplyState.Unknown) ||
        item.State.Fields.Any(field => field.Key.StartsWith("gpu.", StringComparison.OrdinalIgnoreCase) &&
            field.Value.WriteState == ApplyState.Unknown);
    internal static bool RequiresGpuRecoveryCheck(Jiaolong.Contracts.Commands.HardwareCommand command) => command is
        Jiaolong.Contracts.Commands.SetGpuVfCurveCommand or Jiaolong.Contracts.Commands.SetGpuCoreOffsetCommand or
        Jiaolong.Contracts.Commands.SetGpuMemoryOffsetCommand or Jiaolong.Contracts.Commands.SetGpuFrequencyLimitCommand or
        Jiaolong.Contracts.Commands.SetGpuPowerLimitCommand or Jiaolong.Contracts.Commands.SetGpuPowerPolicyCommand or
        Jiaolong.Contracts.Commands.ApplyTurboBranchCommand or Jiaolong.Contracts.Commands.ReleaseTurboBranchCommand ||
        command is Jiaolong.Contracts.Commands.ApplyControlRequestCommand request &&
            request.Request?.Pages?.Any(page => page is { Page: ControlPageId.Gpu, Follow: true }) == true;
    private static bool ValidGpuRecovery(GpuRecoveryBaseline value, UnresolvedControlOperation operation) =>
        value.SessionId != Guid.Empty && value.HardwareEpoch > 0 && value.HardwareEpoch == operation.State.HardwareEpoch &&
        value.OwnerSid == operation.OwnerSid && !string.IsNullOrWhiteSpace(value.OwnerSid) && value.OwnerSid.Length <= 256 &&
        value.Identity is { } identity && identity == operation.Identity &&
        new[] { identity.BoardProduct, identity.BiosVersion, identity.CpuModel, identity.GpuName }
            .All(text => !string.IsNullOrWhiteSpace(text) && text.Length <= 256 && text != "未知") &&
        value.CapturedAtUtc != DateTimeOffset.MinValue && value.CapturedAtUtc <= DateTimeOffset.UtcNow.AddMinutes(1) &&
        ValidGpuAdapter(value.Adapter) && value.VfOffsetsKhz is { Length: 127 } &&
        value.Pstate is { StateCount: >= 1 and <= 16, ClockCount: >= 1 and <= 8 } pstate &&
        pstate.MemoryMinimumKhz <= pstate.MemoryOffsetKhz && pstate.MemoryOffsetKhz <= pstate.MemoryMaximumKhz;
}
