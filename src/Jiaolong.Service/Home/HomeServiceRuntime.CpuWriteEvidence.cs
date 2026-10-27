using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong.Hardware.Mechrevo.Controls;

namespace Jiaolong.Service.Home;

public sealed partial class HomeServiceRuntime
{
    private static CpuFieldRestorationValue? CpuWriteTarget(HardwareCommand command, string field, CpuTuningState? before)
    {
        CpuTuningPlan[] plans = command switch { SetCpuTuningCommand cpu => [cpu.Plan], SetCpuTuningBatchCommand batch => batch.Plans, _ => [] };
        if (field == "isBoostEnabled" && plans.Any(plan => plan.WindowsPowerSchemeId is Guid scheme && scheme != before?.WindowsPowerSchemeId)) return null;
        return CpuWriteTarget(plans, field, CaptureCpuRestoration(field, before));
    }

    private static CpuFieldRestorationValue? CpuWriteTarget(IEnumerable<CpuTuningPlan> plans, string field, CpuFieldRestorationValue? current)
    {
        if (current is null || field == "negativeCurveOptimizer" && current.PerCoreCurveOptimizer is not { Count: > 0 } ||
            field == "isBoostEnabled" && (current.AcBoostMode is null || current.DcBoostMode is null)) return null;
        foreach (var plan in plans)
        {
            if (field == "isBoostEnabled")
            {
                if (plan.BoostPolicy is { } policy) current = new(policy.AcMode, policy.DcMode, null);
                else if (plan.BoostEnabled is bool enabled) current = new(
                    WindowsPowerBoostMode.SelectTarget(current.AcBoostMode!.Value, enabled),
                    WindowsPowerBoostMode.SelectTarget(current.DcBoostMode!.Value, enabled), null);
            }
            else if (field == "negativeCurveOptimizer")
            {
                var cores = new Dictionary<int, int>(current.PerCoreCurveOptimizer!);
                if ((plan.NegativeCurveOptimizer ?? plan.Advanced?.CurveOptimizerAll) is int all)
                    foreach (int core in cores.Keys.ToArray()) cores[core] = all;
                if (plan.Advanced?.PerCoreCurveOptimizer is { } perCore)
                    foreach (var pair in perCore)
                    { if (!cores.ContainsKey(pair.Key)) return null; cores[pair.Key] = pair.Value; }
                current = new(null, null, cores);
            }
        }
        return current;
    }
}
