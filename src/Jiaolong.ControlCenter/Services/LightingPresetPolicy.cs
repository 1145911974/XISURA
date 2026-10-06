using Jiaolong.Contracts.Models;

namespace Jiaolong_ControlCenter.Services;

public static class LightingPresetPolicy
{
    public static PresetKey? ResolveTarget(HomeControlState controls, IReadOnlyDictionary<string, int> slots,
        PresetKey? confirmedCustom, PresetKey? confirmedPerformance = null)
    {
        // CPU/adaptive targets identify the mode; each page remembers its own slot.
        ControlModeId? mode = controls.AdaptiveAutomation is { Enabled: true, Running: true, CurrentTarget: { } automatic }
            ? PresetKey.All.Contains(automatic) ? automatic.Mode : null
            : confirmedPerformance is { } confirmed && PresetKey.All.Contains(confirmed)
                ? confirmed.Mode
                : controls.PerformanceMode switch
        {
            PerformanceMode.Quiet => ControlModeId.Office,
            PerformanceMode.Balanced => ControlModeId.Gaming,
            PerformanceMode.Turbo => ControlModeId.Turbo,
            PerformanceMode.Custom when confirmedCustom is { Mode: ControlModeId.Custom1 or ControlModeId.Custom2 or ControlModeId.Custom3 }
                => confirmedCustom.Value.Mode,
            _ => null
        };
        if (mode is null) return null;
        int slot = slots.TryGetValue(mode.Value.ToString(), out int selected) && selected is >= 1 and <= 3 ? selected : 2;
        return PresetKey.Create(mode.Value, slot);
    }
    public static bool SameEffect(KeyboardLightingPlan expected, KeyboardLightingPlan? actual)
    {
        if (actual is null) return false;
        int Level(KeyboardLightingPlan plan) => plan.BrightnessLevel ?? (int)Math.Round(plan.Brightness * 3d / 100);
        if (Level(expected) != Level(actual)) return false;
        if (Level(expected) == 0) return true;
        string Effect(KeyboardLightingPlan plan) => plan.Effect == "Static" ? "Fixed" : plan.Effect;
        if (Effect(expected) != Effect(actual)) return false;
        if (expected.Effect == "Cycle") return true;
        return (expected.Red, expected.Green, expected.Blue) == (actual.Red, actual.Green, actual.Blue) &&
            (expected.Effect != "Gradient" || expected.Speed == actual.Speed);
    }

}
