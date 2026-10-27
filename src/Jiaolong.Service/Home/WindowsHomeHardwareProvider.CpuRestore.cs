using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;
using Jiaolong.Hardware.Mechrevo.Controls;
using Jiaolong.Hardware.Abstractions.Compatibility;

namespace Jiaolong.Service.Home;

public sealed partial class WindowsHomeHardwareProvider
{
    // Only the journal-backed runtime calls this; IPC values cannot request restoration bounds.
    public Task<CommandResult> RestoreCpuScalarAsync(Guid id, string field, SettingRequest baseline,
        SettingRequest expected, CancellationToken token) => Task.Run(() =>
    {
        lock (commandGate)
        lock (gate)
        {
            bool started = false;
            try
            {
                token.ThrowIfCancellationRequested();
                if (BackgroundWorkPaused || cpuTuningTransport is null || compatibilityDecision?.Mode != CompatibilityMode.Writable ||
                    field is not ("pptWatts" or "mp1TemperatureC" or "tdcCurrentMilliamps" or "edcCurrentMilliamps") || baseline.Intent != SettingIntent.Set || expected.Intent != SettingIntent.Set ||
                    baseline.Value is not { ValueKind: System.Text.Json.JsonValueKind.Number } original ||
                    !original.TryGetDouble(out double target) || !double.IsFinite(target) ||
                    (field == "pptWatts" ? target is < 1 or > 250 || Math.Abs(target * 1000 - Math.Round(target * 1000)) > 0.000001
                        : field is "tdcCurrentMilliamps" or "edcCurrentMilliamps" ? target is < 1000 or > 200000 || !original.TryGetInt32(out _)
                        : target is < 40 or > 100 || !original.TryGetInt32(out _)))
                    return Rejected(id, ErrorCode.CapabilityUnavailable) with { Mutation = HardwareMutation.NoMutation };
                smuLimitCacheAt = default;
                var currentCpu = ReadCpuTuningState(token, true);
                var capability = CreateFieldCapabilities(compatibilityDecision.Capabilities.Items, currentCpu).FirstOrDefault(item => item.Key == field);
                if (capability is not { Readable: true, Writable: true, Availability: CapabilityState.Available })
                    return Rejected(id, ErrorCode.CapabilityUnavailable) with { Mutation = HardwareMutation.NoMutation };
                var current = ControlPlanCompiler.Read(ControlPageId.Performance, new(null, null, []) { CpuTuning = currentCpu }).GetValueOrDefault(field);
                if (!ControlPlanCompiler.Same(current, expected))
                    return Rejected(id, ErrorCode.ReadBackMismatch) with { Mutation = HardwareMutation.NoMutation };
                if (BackgroundWorkPaused) throw new IOException("cpuHardwareAccessPaused");
                started = true;
                object value = field == "pptWatts" ? (object)target : original.GetInt32();
                cpuTuningTransport.RestoreFieldAsync(field switch { "pptWatts" => CpuTuningField.PptWatts,
                    "tdcCurrentMilliamps" => CpuTuningField.TdcCurrentMilliamps, "edcCurrentMilliamps" => CpuTuningField.EdcCurrentMilliamps, _ => CpuTuningField.Mp1TemperatureC }, value, token).GetAwaiter().GetResult();
                smuLimitCacheAt = default;
                var after = ControlPlanCompiler.Read(ControlPageId.Performance, new(null, null, []) { CpuTuning = ReadCpuTuningState(token, true) }).GetValueOrDefault(field);
                if (!ControlPlanCompiler.Same(after, baseline)) throw new IOException("capturedCpuBaselineReadbackMismatch");
                return new CommandResult(id, CommandState.Applied, null, RequiredUserAction.None, null, false) { Mutation = HardwareMutation.Changed };
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                return Rejected(id, ErrorCode.HardwareWriteFailed) with
                { State = started ? CommandState.RecoveryRequired : CommandState.Rejected,
                    Mutation = started ? HardwareMutation.Unknown : HardwareMutation.NoMutation };
            }
        }
    }, token);

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
