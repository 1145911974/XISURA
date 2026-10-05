using Windows.Graphics;

namespace Jiaolong_ControlCenter.Prototype;

public enum DwmWindowCornerPreference : uint
{
    Default = 0,
    DoNotRound = 1,
    Round = 2,
    RoundSmall = 3,
}

public sealed record DwmWindowChromeAttributes(
    int CornerPreferenceAttribute,
    DwmWindowCornerPreference CornerPreference,
    int BorderColorAttribute,
    uint BorderColor);

public sealed record DwmChromeApplyResult(
    bool Succeeded,
    string? Diagnostic);

public enum WindowSizingEdge
{
    Left = 1,
    Right = 2,
    Top = 3,
    TopLeft = 4,
    TopRight = 5,
    Bottom = 6,
    BottomLeft = 7,
    BottomRight = 8,
}

public readonly record struct WindowSizingRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

public sealed class WindowChromeSubscription
{
    public bool IsAttached { get; private set; }

    public bool TryAttach()
    {
        if (IsAttached) return false;
        IsAttached = true;
        return true;
    }

    public bool TryDetach()
    {
        if (!IsAttached) return false;
        IsAttached = false;
        return true;
    }
}

public static class PrototypeWindowChrome
{
    public const uint ReferenceDpi = 144;
    public const int DesignWidth = 1672;
    public const int DesignHeight = 1045;
    public static double DefaultOccupancy { get; } = .72;
    public static SizeInt32 DefaultClientSize { get; } = new(1966, 1229);
    public static SizeInt32 FinalDesignClientSize { get; } = new(DesignWidth, DesignHeight);
    public const double MinimumScale = .60;
    public const int GwlpWndProc = -4;
    public const uint WmSizing = 0x0214;
    public const uint WmSysCommand = 0x0112;
    public const uint SysCommandMask = 0xFFF0;
    public const uint ScMaximize = 0xF030;
    public const int DwmwaWindowCornerPreference = 33;
    public const int DwmwaBorderColor = 34;
    // Neutral 1px DWM outline; the native overlapped frame supplies the outer shadow.
    public const uint DwmDarkBorderColor = 0x00242424;

    public static DwmWindowChromeAttributes RequiredDwmAttributes { get; } = new(
        DwmwaWindowCornerPreference,
        // DWM alone owns top-level clipping, border, and shadow.
        DwmWindowCornerPreference.Round,
        DwmwaBorderColor,
        DwmDarkBorderColor);

    public static SizeInt32 ConstrainClientSize(int width, int height)
    {
        var scale = Math.Min((double)width / DesignWidth, (double)height / DesignHeight);
        scale = Math.Max(scale, 1d / DesignWidth);
        return new SizeInt32(
            (int)Math.Round(DesignWidth * scale),
            (int)Math.Round(DesignHeight * scale));
    }

    public static SizeInt32 FitClientSize(int workWidth, int workHeight, double occupancy = .86)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(occupancy, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(occupancy, 1);
        return ConstrainClientSize(
            (int)Math.Round(workWidth * occupancy),
            (int)Math.Round(workHeight * occupancy));
    }

    public static SizeInt32 FitClientSizeForWorkArea(
        int workWidth,
        int workHeight,
        int frameWidth,
        int frameHeight,
        double occupancy = .86) =>
        FitClientSize(
            Math.Max(1, workWidth - Math.Max(0, frameWidth)),
            Math.Max(1, workHeight - Math.Max(0, frameHeight)),
            occupancy);

    public static WindowSizingRect ConstrainSizingRect(WindowSizingRect proposed, WindowSizingEdge edge, int frameWidth, int frameHeight)
    {
        var minimumWidth = (int)Math.Round(DesignWidth * MinimumScale);
        var minimumHeight = (int)Math.Round(DesignHeight * MinimumScale);
        var rawProposedWidth = proposed.Width - frameWidth;
        var rawProposedHeight = proposed.Height - frameHeight;

        int clientWidth;
        int clientHeight;

        switch (edge)
        {
            case WindowSizingEdge.Left:
            case WindowSizingEdge.Right:
                clientWidth = Math.Max(minimumWidth, rawProposedWidth);
                clientHeight = (int)Math.Round(clientWidth / 1.6);
                break;

            case WindowSizingEdge.Top:
            case WindowSizingEdge.Bottom:
                clientHeight = Math.Max(minimumHeight, rawProposedHeight);
                clientWidth = (int)Math.Round(clientHeight * 1.6);
                break;

            default:
                var scale = Math.Max((double)rawProposedWidth / DesignWidth, (double)rawProposedHeight / DesignHeight);
                scale = Math.Max(scale, MinimumScale);
                clientWidth = (int)Math.Round(DesignWidth * scale);
                clientHeight = (int)Math.Round(DesignHeight * scale);
                break;
        }

        var width = clientWidth + frameWidth;
        var height = clientHeight + frameHeight;

        var left = edge is WindowSizingEdge.Left or WindowSizingEdge.TopLeft or WindowSizingEdge.BottomLeft
            ? proposed.Right - width
            : proposed.Left;

        var top = edge is WindowSizingEdge.Top or WindowSizingEdge.TopLeft or WindowSizingEdge.TopRight
            ? proposed.Bottom - height
            : proposed.Top;

        return new WindowSizingRect(left, top, left + width, top + height);
    }

    public static DwmChromeApplyResult EvaluateDwmResults(int borderColorHResult, int cornerPreferenceHResult)
    {
        var borderFailed = borderColorHResult < 0;
        var cornerFailed = cornerPreferenceHResult < 0;
        if (!borderFailed && !cornerFailed) return new(true, null);

        var failures = new List<string>(2);
        if (borderFailed) failures.Add("border-color");
        if (cornerFailed) failures.Add("corner-preference");
        var failedAttributes = string.Join(", ", failures);
        return new(false, $"DWM {failedAttributes} update failed.");
    }

    public static DwmChromeApplyResult NoWindowHandle() =>
        new(false, "DWM window handle was unavailable.");
}
