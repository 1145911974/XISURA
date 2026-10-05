using System.Numerics;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using Windows.UI;

namespace Jiaolong_ControlCenter.Prototype.Controls;

public sealed class LightingKeyboardPreview : UserControl
{
    private readonly CanvasControl surface = new() { ClearColor = Colors.Transparent };
    private readonly CanvasControl labels = new() { ClearColor = Colors.Transparent, IsHitTestVisible = false };
    private LightingColor color = new(0, 215, 232);
    private int brightness = 2;
    public LightingKeyboardPreview()
    {
        var layers = new Grid(); layers.Children.Add(surface); layers.Children.Add(labels); Content = layers;
        surface.Draw += (sender, args) => Draw(sender, args, false);
        labels.Draw += (sender, args) => Draw(sender, args, true);
    }
    public void SetLight(LightingColor light, int level) { color = light; brightness = level; surface.Invalidate(); }
    private void Draw(CanvasControl sender, CanvasDrawEventArgs args, bool labelsOnly)
    {
        var ds = args.DrawingSession;
        float scale = (float)Math.Min(sender.ActualWidth / 1090, sender.ActualHeight / 412);
        ds.Transform = Matrix3x2.CreateScale(scale) * Matrix3x2.CreateTranslation(((float)sender.ActualWidth - 1090 * scale) / 2, ((float)sender.ActualHeight - 412 * scale) / 2);
        byte alpha = (byte)(brightness == 0 ? 0 : 80 + brightness * 50);
        var edge = brightness == 0 ? Color.FromArgb(255, 65, 71, 75) : Color.FromArgb(alpha, color.R, color.G, color.B);
        using var text = new CanvasTextFormat { FontFamily = "Segoe UI", FontSize = 14, HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center };
        void Key(string label, float x, float y, float width = 53, float height = 53, bool outlined = false, bool space = false)
        {
            if (labelsOnly)
            {
                text.FontSize = label.Length > 8 ? 10 : label.Length > 3 ? 12 : 14;
                ds.DrawText(label, new Rect(x, y, width, height), Color.FromArgb(255, 220, 232, 234), text);
                return;
            }
            using var path = new CanvasPathBuilder(sender);
            CanvasGeometry? geometry = null;
            if (space)
            {
                path.BeginFigure(x + 4, y); path.AddLine(x + width - 4, y); path.AddLine(x + width, y + 4); path.AddLine(x + width, y + height - 4);
                path.AddLine(x + width - 4, y + height); path.AddLine(x + 95, y + height); path.AddLine(x + 80, y + height + 11);
                path.AddLine(x + 22, y + height + 11); path.AddLine(x, y + height - 7); path.AddLine(x, y + 4); path.EndFigure(CanvasFigureLoop.Closed);
                geometry = CanvasGeometry.CreatePath(path);
            }
            if (brightness > 0)
            {
                var glow = Color.FromArgb((byte)(alpha / 13), color.R, color.G, color.B);
                if (geometry is not null) ds.DrawGeometry(geometry, glow, 7);
                else ds.DrawRoundedRectangle(x, y, width, height, 4, 4, glow, 7);
            }
            var fill = Color.FromArgb(255, 15, 19, 22);
            if (geometry is not null) { ds.FillGeometry(geometry, fill); ds.DrawGeometry(geometry, edge, 1.3f); geometry.Dispose(); }
            else { ds.FillRoundedRectangle(x, y, width, height, 4, 4, fill); ds.DrawRoundedRectangle(x, y, width, height, 4, 4, edge, 1.3f); }
            if (outlined) ds.DrawRoundedRectangle(x + 2, y + 2, width - 4, height - 4, 2, 2, Color.FromArgb(225, 223, 233, 234), 1.3f);
        }
        float x = 18;
        var function = new[] { "Esc", "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12", "Insert\nPrtSc" };
        for (int i = 0; i < function.Length; i++) Key(function[i], 18 + i * (864f / 14), 14, 864f / 14 - 7, 43);
        void Row(string[] labels, float y, float[] widths)
        {
            x = 18;
            for (int i = 0; i < labels.Length; i++) { Key(labels[i], x, y, widths[i], 53, labels[i] is "W" or "A" or "S" or "D"); x += widths[i] + 7; }
        }
        Row(["~\n`", "!\n1", "@\n2", "#\n3", "$\n4", "%\n5", "^\n6", "&\n7", "*\n8", "(\n9", ")\n0", "_\n-", "+\n=", "Backspace"], 65, [53,53,53,53,53,53,53,53,53,53,53,53,53,77]);
        Row(["Tab", "Q", "W", "E", "R", "T", "Y", "U", "I", "O", "P", "{\n[", "}\n]", "|\n\\"], 125, [77,53,53,53,53,53,53,53,53,53,53,53,53,53]);
        Row(["Caps Lock", "A", "S", "D", "F", "G", "H", "J", "K", "L", ":\n;", "\"\n'", "Enter"], 185, [88,53,53,53,53,53,53,53,53,53,53,53,102]);
        Row(["Shift", "Z", "X", "C", "V", "B", "N", "M", "<\n,", ">\n.", "?\n/", "Shift"], 245, [123,53,53,53,53,53,53,53,53,53,53,127]);
        x = 18;
        foreach (var key in new[] { ("Ctrl", 65f), ("Fn", 47f), ("⊞", 47f), ("Alt", 53f) }) { Key(key.Item1, x, 305, key.Item2); x += key.Item2 + 7; }
        Key("", x, 305, 245, 53, space: true); x += 252;
        Key("Alt", x, 305, 53); x += 60; Key("Ctrl", x, 305, 65);
        Key("↑", 762, 305, 53, 42, true); Key("←", 702, 354, 53, 42, true); Key("↓", 762, 354, 53, 42, true); Key("→", 822, 354, 53, 42, true);
        string[][] pad = [["*", "/", "Del"], ["Num\nLock", "−", "+"], ["7\nHome", "8\n↑", "9\nPgUp"], ["4\n←", "5", "6\n→"], ["1\nEnd", "2\n↓", "3\nPgDn"], ["0\nIns", ".\nDel", "Enter"]];
        for (int r = 0; r < 6; r++) for (int c = 0; c < 3; c++) Key(pad[r][c], 898 + c * 60, r == 0 ? 14 : 65 + (r - 1) * 60, 53, r == 0 ? 43 : 53);
    }
}
