namespace Jiaolong_ControlCenter.Services;

internal sealed class WorkAreaFitSizingSession
{
    private WindowSizePreference? initialSize;
    private bool sizingObserved;

    public void Begin(WindowSizePreference? fittedSize)
    {
        initialSize = fittedSize;
        sizingObserved = false;
    }

    public void RecordSizing() => sizingObserved = true;

    public bool Complete(WindowSizePreference finalSize)
    {
        var resized = sizingObserved && initialSize is { } initial &&
            WindowSizePersistence.IsUsable(finalSize) && finalSize != initial;
        initialSize = null;
        sizingObserved = false;
        return resized;
    }
}
