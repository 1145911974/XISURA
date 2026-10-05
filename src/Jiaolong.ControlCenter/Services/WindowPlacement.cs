using Windows.Graphics;

namespace Jiaolong_ControlCenter.Services;

public readonly record struct WindowSizePreference(int WidthDip, int HeightDip);

public readonly record struct WindowWorkArea(int Left, int Top, int Width, int Height)
{
    public int Right => Left + Width;
    public int Bottom => Top + Height;
}

public static class WindowSizePersistence
{
    public static WindowSizePreference FromPixels(SizeInt32 pixels, uint dpi)
    {
        var scale = Math.Max(96u, dpi) / 96d;
        return new WindowSizePreference(
            Math.Max(1, (int)Math.Round(pixels.Width / scale)),
            Math.Max(1, (int)Math.Round(pixels.Height / scale)));
    }

    public static SizeInt32 ToPixels(WindowSizePreference preference, uint dpi)
    {
        var scale = Math.Max(96u, dpi) / 96d;
        return new SizeInt32(
            Math.Max(1, (int)Math.Round(preference.WidthDip * scale)),
            Math.Max(1, (int)Math.Round(preference.HeightDip * scale)));
    }

    public static bool IsUsable(WindowSizePreference preference) =>
        preference.WidthDip > 0 && preference.HeightDip > 0;
}

public static class WindowPlacement
{
    public static PointInt32 DockBottomRight(WindowWorkArea workArea, SizeInt32 size, int gap) =>
        new(Math.Max(workArea.Left, workArea.Right - size.Width - Math.Max(0, gap)),
            Math.Max(workArea.Top, workArea.Bottom - size.Height - Math.Max(0, gap)));

    public static PointInt32 CenterClient(
        WindowWorkArea workArea,
        SizeInt32 clientSize,
        int frameWidth,
        int frameHeight)
    {
        var outerWidth = clientSize.Width + Math.Max(0, frameWidth);
        var outerHeight = clientSize.Height + Math.Max(0, frameHeight);
        return new PointInt32(
            workArea.Left + Math.Max(0, (workArea.Width - outerWidth) / 2),
            workArea.Top + Math.Max(0, (workArea.Height - outerHeight) / 2));
    }
}
