using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;
using Jiaolong.Hardware.Mechrevo.Controls;

namespace Jiaolong.Service.Home;

public sealed partial class WindowsHomeHardwareProvider
{
    // Internal only: baseline values are never accepted from an IPC command.
    public Task<CommandResult> RestoreCpuFieldAsync(Guid id, string field, CpuFieldRestorationValue baseline,
        CpuFieldRestorationValue expected, CancellationToken token) => Task.Run(() =>
    {
        lock (commandGate)
        {
        bool started = false;
        try
        {
            lock (gate)
            {
                token.ThrowIfCancellationRequested();
                if (cpuTuningTransport is null || field is not ("isBoostEnabled" or "negativeCurveOptimizer"))
                    return Rejected(id, ErrorCode.CapabilityUnavailable) with { Mutation = HardwareMutation.NoMutation };
                var current = HomeServiceRuntime.CaptureCpuRestoration(field, ReadCpuTuningState(token, field == "negativeCurveOptimizer"));
                if (!HomeServiceRuntime.SameRestoration(current, expected))
                    return Rejected(id, ErrorCode.ReadBackMismatch) with { Mutation = HardwareMutation.NoMutation };
                started = true;
                if (field == "isBoostEnabled" && baseline.AcBoostMode is int ac && baseline.DcBoostMode is int dc)
                    cpuTuningTransport.RestoreCapturedBoostModesAsync(ac, dc, token).GetAwaiter().GetResult();
                else if (field == "negativeCurveOptimizer" && baseline.PerCoreCurveOptimizer is { Count: > 0 } cores)
                    cpuTuningTransport.RestoreFieldAsync(CpuTuningField.PerCoreCurveOptimizer, cores, token).GetAwaiter().GetResult();
                else throw new InvalidDataException("capturedCpuBaselineUnavailable");
                var after = HomeServiceRuntime.CaptureCpuRestoration(field, ReadCpuTuningState(token, field == "negativeCurveOptimizer"));
                if (!HomeServiceRuntime.SameRestoration(after, baseline)) throw new IOException("capturedCpuBaselineReadbackMismatch");
                return new(id, CommandState.Applied, null, RequiredUserAction.None, null, false) { Mutation = HardwareMutation.Changed };
            }

        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return Rejected(id, ErrorCode.HardwareWriteFailed) with
            { State = started ? CommandState.RecoveryRequired : CommandState.Rejected,
              Mutation = started ? HardwareMutation.Unknown : HardwareMutation.NoMutation };
        }
        }
    }, token);
}
