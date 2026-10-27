using Jiaolong.Contracts.Models;

namespace Jiaolong.Service.Home;

public sealed partial class HomeServiceRuntime
{
    private static bool CompletedCompoundGpu(ClientControlSessionSnapshot session, ControlJournalSnapshot saved)
    {
        if (saved.Unresolved is not [var pending]) return false;
        if (pending.GpuRecovery is null && pending.GpuAttempt is null) return true;
        if (CompoundStep(saved) is not { } step || CommandPage(step.Command) is not (ControlPageId.Fan or ControlPageId.Lighting) || pending.GpuRecovery is not { } first ||
            pending.GpuAttempt is not { } attempt || attempt.StepId == step.Command.OperationId ||
            first.SessionId != session.ServiceSessionId || attempt.Before.SessionId != session.ServiceSessionId ||
            first.HardwareEpoch != session.Handle.HardwareEpoch || attempt.Before.HardwareEpoch != session.Handle.HardwareEpoch ||
            first.OwnerSid != session.OwnerSid || attempt.Before.OwnerSid != session.OwnerSid ||
            first.Identity != session.Identity || attempt.Before.Identity != session.Identity || first.Adapter != attempt.Before.Adapter ||
            session.Baseline.GpuVf is null) return false;
        var expected = ControlPlanCompiler.Read(ControlPageId.Gpu, new(null, null, []) { GpuVf = session.Baseline.GpuVf });
        foreach (var pair in saved.Current!.Fields.Where(pair => pair.Key.StartsWith("gpu.")))
        {
            if (pair.Value.WriteState is not (ApplyState.Confirmed or ApplyState.SubmittedUnverified) || pair.Value.LastWritten is null) return false;
            expected[pair.Key[4..]] = pair.Value.LastWritten;
        }
        return ExitSame(expected.GetValueOrDefault("coreOffsetKhz"), SettingRequest.Set(attempt.Pstate.CoreOffsetKhz)) &&
            ExitSame(expected.GetValueOrDefault("memoryOffsetKhz"), SettingRequest.Set(attempt.Pstate.MemoryOffsetKhz)) &&
            ExitSame(expected.GetValueOrDefault("vfOffsetsKhz"), SettingRequest.Set(attempt.VfOffsetsKhz));
    }
}
