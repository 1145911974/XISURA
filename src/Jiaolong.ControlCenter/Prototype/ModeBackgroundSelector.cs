namespace Jiaolong_ControlCenter.Prototype;

public sealed class ModeBackgroundSelector
{
    private readonly Func<int, int> chooseIndex;
    private readonly Dictionary<PrototypePerformanceMode, string> previous = [];

    public ModeBackgroundSelector(Func<int, int>? chooseIndex = null) =>
        this.chooseIndex = chooseIndex ?? Random.Shared.Next;

    public ModeVisualScene Select(PrototypePerformanceMode mode, string customProfile)
    {
        if (mode == PrototypePerformanceMode.Custom)
            return ModeVisualCatalog.ForCustomProfile(customProfile);

        var pool = ModeVisualCatalog.ForMode(mode);
        var selected = previous.TryGetValue(mode, out var key)
            ? pool.Single(scene => scene.Key != key)
            : pool[chooseIndex(pool.Count)];
        previous[mode] = selected.Key;
        return selected;
    }
}
