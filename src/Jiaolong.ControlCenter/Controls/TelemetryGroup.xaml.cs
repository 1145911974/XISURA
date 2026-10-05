using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Jiaolong_ControlCenter.Controls;

public sealed partial class TelemetryGroup : UserControl
{
    public TelemetryGroup() => InitializeComponent();

    public string Header
    {
        get => (string)GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public object? GroupContent
    {
        get => GetValue(GroupContentProperty);
        set => SetValue(GroupContentProperty, value);
    }

    public static readonly DependencyProperty HeaderProperty = DependencyProperty.Register(
        nameof(Header), typeof(string), typeof(TelemetryGroup), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty GroupContentProperty = DependencyProperty.Register(
        nameof(GroupContent), typeof(object), typeof(TelemetryGroup), new PropertyMetadata(null));
}
