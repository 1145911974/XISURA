using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jiaolong.Contracts.Models;

public sealed record CpuBoostPolicy(
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] int AcMode,
    [property: JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.Never)] int DcMode)
{
    [JsonIgnore] public bool IsValid => AcMode is >= 0 and <= 6 && DcMode is >= 0 and <= 6;
    [JsonIgnore] public bool IsEnabled => AcMode != 0 && DcMode != 0;
    public bool MatchesToggle(bool enabled) => IsValid && (enabled ? IsEnabled : AcMode == 0 && DcMode == 0);

    public static CpuBoostPolicy? FromState(CpuTuningState? state) =>
        state?.AcBoostMode is >= 0 and <= 6 && state.DcBoostMode is >= 0 and <= 6 &&
        state.BoostEnabled == (state.AcBoostMode != 0 && state.DcBoostMode != 0)
            ? new(state.AcBoostMode.Value, state.DcBoostMode.Value) : null;

    public static bool TryRead(JsonElement value, out CpuBoostPolicy? policy)
    {
        policy = null;
        if (value.ValueKind != JsonValueKind.Object) return false;
        try
        {
            var fields = value.EnumerateObject().ToDictionary(item => item.Name, item => item.Value, StringComparer.OrdinalIgnoreCase);
            if (fields.Count != 2 || !fields.TryGetValue("acMode", out var ac) || ac.ValueKind != JsonValueKind.Number || !ac.TryGetInt32(out int acMode) ||
                !fields.TryGetValue("dcMode", out var dc) || dc.ValueKind != JsonValueKind.Number || !dc.TryGetInt32(out int dcMode)) return false;
            var candidate = new CpuBoostPolicy(acMode, dcMode);
            if (!candidate.IsValid) return false;
            policy = candidate;
            return true;
        }
        catch (ArgumentException) { return false; }
    }
}
