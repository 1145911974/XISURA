using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace Jiaolong_ControlCenter.Controls;

public sealed partial class Sparkline : UserControl
{
    private readonly Polyline line = new() { Stroke = new SolidColorBrush(Microsoft.UI.Colors.CornflowerBlue), StrokeThickness = 1.5 };

    public Sparkline()
    {
        InitializeComponent();
        Plot.Children.Add(line);
        SizeChanged += (_, _) => Redraw();
    }

    public IReadOnlyList<double> Values
    {
        get => (IReadOnlyList<double>)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public string Summary
    {
        get => (string)GetValue(SummaryProperty);
        set => SetValue(SummaryProperty, value);
    }

    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<double>), typeof(Sparkline), new PropertyMetadata(Array.Empty<double>(), OnValuesChanged));

    public static readonly DependencyProperty SummaryProperty = DependencyProperty.Register(
        nameof(Summary), typeof(string), typeof(Sparkline), new PropertyMetadata("暂无趋势"));

    private static void OnValuesChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) => ((Sparkline)sender).Redraw();

    private void Redraw()
    {
        SummaryText.Text = Summary;
        line.Points.Clear();
        var values = Values.TakeLast(60).ToArray();
        if (values.Length < 2 || ActualWidth <= 0 || ActualHeight <= 0) return;
        var min = values.Min();
        var range = Math.Max(values.Max() - min, 0.001);
        for (var index = 0; index < values.Length; index++)
        {
            var x = index * ActualWidth / (values.Length - 1);
            var y = (values[index] - min) / range * Math.Max(ActualHeight - 16, 1);
            line.Points.Add(new Point(x, ActualHeight - y - 8));
        }
    }
}
