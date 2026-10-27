using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong.Hardware.Mechrevo.Controls;

namespace Jiaolong.Service.Home;

public sealed partial class HomeServiceRuntime
{
    private static bool ValidPendingCpuBatch(ControlJournalSnapshot saved)
    {
        if (saved.Current?.CpuWriteBatch is not { } batch) return true; // Legacy records still require exact endpoints.
        if (saved.Unresolved is not [var pending] || PendingCpuCommandKind(saved) is not (nameof(SetCpuTuningBatchCommand) or nameof(SetCpuTuningCommand)) ||
            batch.OperationId != pending.OperationId || batch.NativeMode is not null || CommandValidation.Validate(batch) is not null ||
            PendingCpuCommandKind(saved) == nameof(SetCpuTuningCommand) && batch.Plans.Length != 1 ||
            batch.Plans.Any(plan => CpuFields(plan).Any(field =>
                field is not ("negativeCurveOptimizer" or "windowsPowerSchemeId") && !IsDirectCpuScalar(field) && CpuWindowsKeys(field).Length == 0))) return false;
        var fields = PendingCpuOwnedFields(saved);
        if (batch.Plans.SelectMany(CpuFields).Distinct().Any(field => !fields.Any(pair => pair.Key == "performance." + field))) return false;
        foreach (var pair in fields.Where(pair => IsDirectCpuScalar(pair.Key[12..])))
        {
            var targets = batch.Plans.Select(plan => IntendedValues(new SetCpuTuningCommand(batch.OperationId, plan, true)).GetValueOrDefault(pair.Key[12..]))
                .Where(value => value is not null).ToArray();
            if (targets.Length == 0 || !ControlPlanCompiler.Same(pair.Value.LastWritten, targets[^1])) return false;
        }
        foreach (var pair in fields.Where(pair => pair.Key is "performance.negativeCurveOptimizer" or "performance.isBoostEnabled"))
        {
            if (pair.Key == "performance.isBoostEnabled" && CrossSchemeCpuBatch(saved)) continue;
            if (!SameRestoration(pair.Value.WriteTargetDetails,
                CpuWriteTarget(batch.Plans, pair.Key[12..], pair.Value.WriteBeforeDetails))) return false;
        }
        return true;
    }

    private static bool CrossSchemeCpuBatch(ControlJournalSnapshot saved) =>
        saved.Current?.CpuWriteBatch?.Plans.Any(plan => plan.WindowsPowerSchemeId is not null) == true;

    private static bool PendingCpuScalarMatches(ControlJournalSnapshot saved, string key, SettingRequest? actual)
    {
        var field = saved.Current!.Fields[key];
        if (actual?.Value is not { ValueKind: System.Text.Json.JsonValueKind.Number } number || !number.TryGetDouble(out double value) ||
            !double.IsFinite(value) || field.WriteBefore is null || field.LastWritten is null || field.Baseline is null) return false;
        var candidates = new List<SettingRequest?> { field.WriteBefore, field.LastWritten, field.Baseline };
        if (saved.Current.CpuWriteBatch is { } batch)
            candidates.AddRange(batch.Plans.Select(plan => IntendedValues(new SetCpuTuningCommand(batch.OperationId, plan, true)).GetValueOrDefault(key[12..])));
        return candidates.Any(candidate => ControlPlanCompiler.Same(actual, candidate));
    }

    private static IEnumerable<CpuFieldRestorationValue?> PendingCpuBatchDetails(ControlJournalSnapshot saved, string key)
    {
        if (saved.Current?.CpuWriteBatch is not { } batch) yield break;
        var before = saved.Current.Fields[key].WriteBeforeDetails;
        for (int length = 1; length < batch.Plans.Length; length++)
            yield return CpuWriteTarget(batch.Plans.Take(length), key[12..], before);
    }

    private static IEnumerable<int> PendingCpuBatchIndices(ControlJournalSnapshot saved, string key, WindowsCpuSetting index)
    {
        if (saved.Current?.CpuWriteBatch is not { } batch) yield break;
        if (index is WindowsCpuSetting.AcBoostMode or WindowsCpuSetting.DcBoostMode)
        {
            foreach (var details in PendingCpuBatchDetails(saved, key))
                if ((index == WindowsCpuSetting.AcBoostMode ? details?.AcBoostMode : details?.DcBoostMode) is int value) yield return value;
            yield break;
        }
        foreach (var plan in batch.Plans)
        {
            int? target = index switch
            {
                WindowsCpuSetting.AcMaxFrequencyMhz => plan.AcMaxFrequencyMhz ?? plan.MaxFrequencyMhz,
                WindowsCpuSetting.DcMaxFrequencyMhz => plan.DcMaxFrequencyMhz ?? plan.MaxFrequencyMhz,
                WindowsCpuSetting.AcMinActiveCoresPercent => plan.AcMinActiveCoresPercent,
                WindowsCpuSetting.DcMinActiveCoresPercent => plan.DcMinActiveCoresPercent,
                _ => null
            };
            if (target is int value) yield return value;
        }
    }
}
