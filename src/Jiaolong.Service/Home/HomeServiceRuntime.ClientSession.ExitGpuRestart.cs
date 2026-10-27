using System.Text.Json;
using Jiaolong.Contracts.Models;

namespace Jiaolong.Service.Home;

public sealed partial class HomeServiceRuntime
{
    private sealed record PriorExitGpu(ExitStep Step, GpuRecoveryAttempt Attempt);

    private static PriorExitGpu? PriorExitGpuAttempt(ClientControlSessionSnapshot session, ExitPlan plan, ControlJournalSnapshot saved)
    {
        if (saved.Unresolved is not [var pending] || pending.OperationId != plan.OperationId ||
            pending.CommandKind != "EndClientControlSession" || pending.OwnerSid != session.OwnerSid || pending.Identity != session.Identity ||
            pending.GpuRecovery is not { } first || pending.GpuAttempt is not { } attempt ||
            pending.NativeModeAttempt is not null && PriorExitModeAttempt(session, plan, saved) is not { Step.Status: "Verified" } ||
            first.SessionId != session.ServiceSessionId || attempt.Before.SessionId != session.ServiceSessionId ||
            first.HardwareEpoch != session.Handle.HardwareEpoch || attempt.Before.HardwareEpoch != session.Handle.HardwareEpoch ||
            first.OwnerSid != session.OwnerSid || attempt.Before.OwnerSid != session.OwnerSid ||
            first.Identity != session.Identity || attempt.Before.Identity != session.Identity || first.Adapter != attempt.Before.Adapter ||
            attempt.StepId == Guid.Empty || session.Baseline.GpuVf is not { Nodes.Length: 127 } gpu)
            return null;
        string key = attempt.Kind switch { "core" => "gpu.coreOffsetKhz", "memory" => "gpu.memoryOffsetKhz", "vf" => "gpu.vfOffsetsKhz", _ => "" };
        var step = plan.Steps.SingleOrDefault(item => item.Key == key);
        if (step is null || !ExitMatches(step, new(null, null, []) { GpuVf = GpuAttemptBefore(gpu, attempt) }, false)) return null;
        bool targetMatches = attempt.Kind switch
        {
            "core" => attempt.Pstate.CoreOffsetKhz == 0 && ExitSame(step.IntermediateVf, SettingRequest.Set(attempt.VfOffsetsKhz)),
            "memory" => attempt.Pstate.MemoryOffsetKhz == 0,
            "vf" => ExitSame(step.Target, SettingRequest.Set(attempt.VfOffsetsKhz)),
            _ => false
        };
        return targetMatches ? new(step, attempt) : null;
    }

    private static GpuVfState GpuAttemptBefore(GpuVfState shape, GpuRecoveryAttempt attempt) => shape with
    {
        CoreOffsetKhz = attempt.Before.Pstate.CoreOffsetKhz,
        MemoryOffsetKhz = attempt.Before.Pstate.MemoryOffsetKhz,
        Nodes = shape.Nodes.Select((node, index) => node with { OffsetKhz = attempt.Before.VfOffsetsKhz[index] }).ToArray()
    };
}
