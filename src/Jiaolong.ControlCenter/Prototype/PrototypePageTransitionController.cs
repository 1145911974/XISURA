namespace Jiaolong_ControlCenter.Prototype;

public sealed record PrototypePageTransitionPlan(
    long Version,
    string CurrentPage,
    string TargetPage,
    bool Snap,
    bool FadeThrough,
    TimeSpan ExitDuration,
    TimeSpan EnterDuration,
    double ExitOffset,
    double EnterOffset);

public sealed class PrototypePageTransitionController
{
    private long latestVersion;

    public PrototypePageTransitionPlan Begin(
        string currentPage,
        string targetPage,
        PrototypeMotionTokens motion,
        bool animate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentPage);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPage);
        ArgumentNullException.ThrowIfNull(motion);

        var snap = !animate || string.Equals(currentPage, targetPage, StringComparison.Ordinal) ||
                   motion.Page <= TimeSpan.Zero || motion.Translation <= 0;
        var version = ++latestVersion;
        return snap
            ? new(version, currentPage, targetPage, true, false, TimeSpan.Zero, TimeSpan.Zero, 0, 0)
            : new(version, currentPage, targetPage, false, true, motion.Page, motion.Page, 0, 0);
    }
}

public sealed class PrototypePageNavigationState
{
    public PrototypePageNavigationState(string initialPage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(initialPage);
        TargetPage = initialPage;
    }

    public string TargetPage { get; private set; }

    public bool TryRequest(string destination)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        if (string.Equals(TargetPage, destination, StringComparison.Ordinal)) return false;

        TargetPage = destination;
        return true;
    }
}
