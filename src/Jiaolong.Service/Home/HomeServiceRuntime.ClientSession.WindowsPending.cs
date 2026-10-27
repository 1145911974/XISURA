using System.Security.Cryptography;
using System.Text.Json;
using Jiaolong.Contracts.Commands;
using Jiaolong.Hardware.Mechrevo.Controls;

namespace Jiaolong.Service.Home;

public sealed partial class HomeServiceRuntime
{
    private static string WindowsPendingProofHash(WindowsPendingWrite proof) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(proof)));

    private static void ApplyWindowsPlan(CpuTuningPlan plan, Func<WindowsCpuSetting, int> current, Action<WindowsCpuSetting, int> set)
    {
        if (plan.MaxFrequencyMhz is not null || plan.AcMaxFrequencyMhz is not null || plan.DcMaxFrequencyMhz is not null)
        {
            set(WindowsCpuSetting.AcMaxFrequencyMhz, plan.AcMaxFrequencyMhz ?? plan.MaxFrequencyMhz ?? current(WindowsCpuSetting.AcMaxFrequencyMhz));
            set(WindowsCpuSetting.DcMaxFrequencyMhz, plan.DcMaxFrequencyMhz ?? plan.MaxFrequencyMhz ?? current(WindowsCpuSetting.DcMaxFrequencyMhz));
        }
        if (plan.BoostPolicy is { } boost)
        { set(WindowsCpuSetting.AcBoostMode, boost.AcMode); set(WindowsCpuSetting.DcBoostMode, boost.DcMode); }
        else if (plan.BoostEnabled is bool enabled)
        {
            set(WindowsCpuSetting.AcBoostMode, WindowsPowerBoostMode.SelectTarget(current(WindowsCpuSetting.AcBoostMode), enabled));
            set(WindowsCpuSetting.DcBoostMode, WindowsPowerBoostMode.SelectTarget(current(WindowsCpuSetting.DcBoostMode), enabled));
        }
        if (plan.AcMinActiveCoresPercent is not null || plan.DcMinActiveCoresPercent is not null)
        {
            set(WindowsCpuSetting.AcMinActiveCoresPercent, plan.AcMinActiveCoresPercent ?? current(WindowsCpuSetting.AcMinActiveCoresPercent));
            set(WindowsCpuSetting.DcMinActiveCoresPercent, plan.DcMinActiveCoresPercent ?? current(WindowsCpuSetting.DcMinActiveCoresPercent));
        }
    }

    private static WindowsPendingWrite? VerifiedCpuWindowsProof(ControlJournalSnapshot saved, WindowsScopeSession? windows)
    {
        var proof = windows?.PendingWrite;
        if (proof is null || proof.InitialScheme == Guid.Empty || proof.Schemes is not { Count: > 0 and <= 21 } ||
            saved.Unresolved is not [var pending] || proof.OperationId != pending.OperationId || !ValidPendingCpuBatch(saved) ||
            saved.Current?.CpuWriteBatch is not { } batch ||
            saved.Current.WindowsWriteProofHash != WindowsPendingProofHash(proof) ||
            PendingCpuOwnedFields(saved).Any(pair =>
                saved.RecoveryScope?.Fields.GetValueOrDefault(pair.Key)?.WindowsPowerSchemeId != proof.InitialScheme)) return null;
        foreach (var scheme in proof.Schemes)
        {
            if (scheme.Value is null || windows!.Schemes.GetValueOrDefault(scheme.Key) is not { } owner ||
                !scheme.Value.Keys.Order().SequenceEqual(owner.Baseline.Keys.Order()) || scheme.Value.Any(pair => pair.Value is null ||
                    pair.Value.Before is not int before || !ValidWindowsIndex(pair.Key, before) || pair.Value.Targets is not { Length: <= 20 } ||
                    pair.Value.Targets.Any(value => !ValidWindowsIndex(pair.Key, value)))) return null;
        }
        var expected = proof.Schemes.ToDictionary(scheme => scheme.Key,
            scheme => scheme.Value.ToDictionary(pair => pair.Key, pair => pair.Value with { Targets = [] }));
        Guid selected = proof.InitialScheme;
        var visited = new HashSet<Guid>();
        try
        {
            foreach (var plan in batch.Plans)
            {
                selected = plan.WindowsPowerSchemeId ?? selected;
                if (!HasWindowsSettings(plan)) continue;
                if (!expected.TryGetValue(selected, out var indices)) return null;
                visited.Add(selected);
                int Current(WindowsCpuSetting key) => indices.TryGetValue(key, out var index)
                    ? index.Targets.LastOrDefault(index.Before!.Value) : throw new InvalidDataException("pendingWindowsIndexMissing");
                void Set(WindowsCpuSetting key, int target)
                {
                    if (!indices.TryGetValue(key, out var index) || !windows!.Schemes[selected].Writes.ContainsKey(key))
                        throw new InvalidDataException("pendingWindowsOwnershipMissing");
                    indices[key] = index with { Targets = [.. index.Targets, target] };
                }
                ApplyWindowsPlan(plan, Current, Set);
            }
        }
        catch (InvalidDataException) { return null; }
        if (!visited.SetEquals(proof.Schemes.Keys) || expected.Any(scheme => scheme.Value.Any(pair =>
            !pair.Value.Targets.SequenceEqual(proof.Schemes[scheme.Key][pair.Key].Targets)))) return null;
        return proof;
    }
}
