using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Jiaolong_ControlCenter.Controls;

public sealed partial class MetricRow : UserControl
{
    public MetricRow() => InitializeComponent();

    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public string Value { get => (string)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public string Unit { get => (string)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
    public string AccessibleText { get => (string)GetValue(AccessibleTextProperty); set => SetValue(AccessibleTextProperty, value); }

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label), typeof(string), typeof(MetricRow), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(string), typeof(MetricRow), new PropertyMetadata("未知"));
    public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(nameof(Unit), typeof(string), typeof(MetricRow), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty AccessibleTextProperty = DependencyProperty.Register(nameof(AccessibleText), typeof(string), typeof(MetricRow), new PropertyMetadata(string.Empty));
}
