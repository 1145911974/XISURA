using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Jiaolong_ControlCenter.Controls;

public sealed partial class CapabilityGate : UserControl
{
    public CapabilityGate() => InitializeComponent();

    public bool IsAvailable { get => (bool)GetValue(IsAvailableProperty); set => SetValue(IsAvailableProperty, value); }
    public string Reason { get => (string)GetValue(ReasonProperty); set => SetValue(ReasonProperty, value); }
    public object? ChildContent { get => GetValue(ChildContentProperty); set => SetValue(ChildContentProperty, value); }

    public static readonly DependencyProperty IsAvailableProperty = DependencyProperty.Register(nameof(IsAvailable), typeof(bool), typeof(CapabilityGate), new PropertyMetadata(false));
    public static readonly DependencyProperty ReasonProperty = DependencyProperty.Register(nameof(Reason), typeof(string), typeof(CapabilityGate), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty ChildContentProperty = DependencyProperty.Register(nameof(ChildContent), typeof(object), typeof(CapabilityGate), new PropertyMetadata(null));
}
