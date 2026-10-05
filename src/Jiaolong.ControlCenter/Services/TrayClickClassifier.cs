namespace Jiaolong_ControlCenter.Services;

public enum TrayClickResult
{
    None,
    ShowQuickConsole,
    OpenMainWindow
}

public sealed class TrayClickClassifier
{
    private readonly TimeSpan doubleClickWindow;
    private DateTimeOffset? pendingClick;

    public TrayClickClassifier(TimeSpan doubleClickWindow)
    {
        if (doubleClickWindow <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(doubleClickWindow));

        this.doubleClickWindow = doubleClickWindow;
    }

    public TrayClickResult RegisterClick(DateTimeOffset now)
    {
        if (pendingClick is DateTimeOffset first && now - first <= doubleClickWindow)
        {
            pendingClick = null;
            return TrayClickResult.OpenMainWindow;
        }

        pendingClick = now;
        return TrayClickResult.None;
    }

    public TrayClickResult Flush(DateTimeOffset now)
    {
        if (pendingClick is not DateTimeOffset first || now - first < doubleClickWindow)
            return TrayClickResult.None;

        pendingClick = null;
        return TrayClickResult.ShowQuickConsole;
    }

    public void Reset() => pendingClick = null;
}
