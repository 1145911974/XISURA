using System.Numerics;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.UI;

namespace Jiaolong_ControlCenter.Prototype.Controls;

public sealed class LightingColorWheel : UserControl
{
    private readonly CanvasControl surface = new() { ClearColor = Colors.Transparent };
    private double hue = 184, saturation = 1, value = .91;
    private int drag;
    public event EventHandler<LightingColor>? ColorChanged;
    public event EventHandler<bool>? InteractionChanged;
    public LightingColorWheel()
    {
        Content = surface;
        surface.Draw += Draw;
        surface.PointerPressed += (_, e) =>
        {
            var p = Position(e);
            double radius = Math.Sqrt(Math.Pow(p.X - 110, 2) + Math.Pow(p.Y - 110, 2));
            drag = radius is >= 86 and <= 109 ? 1 : p.X is >= 53 and <= 167 && p.Y is >= 53 and <= 167 ? 2 : 0;
            if (drag == 0) return;
            InteractionChanged?.Invoke(this, true);
            surface.CapturePointer(e.Pointer); Update(p); e.Handled = true;
        };
        surface.PointerMoved += (_, e) => { if (drag != 0) { Update(Position(e)); e.Handled = true; } };
        surface.PointerReleased += (_, _) => { drag = 0; surface.ReleasePointerCaptures(); InteractionChanged?.Invoke(this, false); };
        surface.PointerCaptureLost += (_, _) => { drag = 0; InteractionChanged?.Invoke(this, false); };
    }
    public void SetColor(LightingColor color)
    {
        var hsv = color.ToHsv();
        if (hsv.S > 0 && hsv.V > 0) hue = hsv.H;
        saturation = hsv.S; value = hsv.V; surface.Invalidate();
    }
    private Point Position(PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(surface).Position;
        double scale = Math.Min(surface.ActualWidth, surface.ActualHeight) / 220;
        return new((p.X - (surface.ActualWidth - 220 * scale) / 2) / scale, (p.Y - (surface.ActualHeight - 220 * scale) / 2) / scale);
    }
    private void Update(Point p)
    {
        if (drag == 1) hue = (Math.Atan2(p.Y - 110, p.X - 110) * 180 / Math.PI + 360) % 360;
        else { saturation = Math.Clamp((p.X - 53) / 114, 0, 1); value = 1 - Math.Clamp((p.Y - 53) / 114, 0, 1); }
        surface.Invalidate(); ColorChanged?.Invoke(this, LightingColor.FromHsv(hue, saturation, value));
    }
    private static Color Native(LightingColor c) => Color.FromArgb(255, c.R, c.G, c.B);
    private void Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        float scale = (float)Math.Min(sender.ActualWidth, sender.ActualHeight) / 220;
        ds.Transform = Matrix3x2.CreateScale(scale) * Matrix3x2.CreateTranslation(((float)sender.ActualWidth - 220 * scale) / 2, ((float)sender.ActualHeight - 220 * scale) / 2);
        for (int angle = 0; angle < 720; angle++)
        {
            double a = angle * Math.PI / 360;
            ds.DrawLine(new Vector2(110 + 88 * (float)Math.Cos(a), 110 + 88 * (float)Math.Sin(a)), new Vector2(110 + 106 * (float)Math.Cos(a), 110 + 106 * (float)Math.Sin(a)), Native(LightingColor.FromHsv(angle / 2d, 1, 1)), 1.2f);
        }
        using var saturationBrush = new CanvasLinearGradientBrush(sender, Colors.White, Native(LightingColor.FromHsv(hue, 1, 1))) { StartPoint = new(53, 53), EndPoint = new(167, 53) };
        using var valueBrush = new CanvasLinearGradientBrush(sender, Color.FromArgb(0, 0, 0, 0), Colors.Black) { StartPoint = new(53, 53), EndPoint = new(53, 167) };
        ds.FillRoundedRectangle(53, 53, 114, 114, 3, 3, saturationBrush);
        ds.FillRoundedRectangle(53, 53, 114, 114, 3, 3, valueBrush);
        ds.DrawRoundedRectangle(53, 53, 114, 114, 3, 3, Color.FromArgb(90, 255, 255, 255), 1);
        float hx = 110 + 97 * (float)Math.Cos(hue * Math.PI / 180), hy = 110 + 97 * (float)Math.Sin(hue * Math.PI / 180);
        ds.FillCircle(hx, hy, 9, Native(LightingColor.FromHsv(hue, 1, 1)));
        ds.DrawCircle(hx, hy, 10, Colors.Black, 4); ds.DrawCircle(hx, hy, 10, Colors.White, 2);
        float sx = 53 + (float)saturation * 114, sy = 53 + (1 - (float)value) * 114;
        ds.DrawCircle(sx, sy, 5, Colors.Black, 3); ds.DrawCircle(sx, sy, 5, Colors.White, 1.5f);
    }
}
