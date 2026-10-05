using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Jiaolong_ControlCenter.Controls;

public sealed partial class QuickSettingButton : UserControl
{
    public QuickSettingButton() => InitializeComponent();

    public string SettingLabel { get => (string)GetValue(SettingLabelProperty); set => SetValue(SettingLabelProperty, value); }
    public string AccessibleText { get => (string)GetValue(AccessibleTextProperty); set => SetValue(AccessibleTextProperty, value); }
    public string SourceLabel { get => (string)GetValue(SourceLabelProperty); set => SetValue(SourceLabelProperty, value); }
    public event RoutedEventHandler? Invoked;

    public static readonly DependencyProperty SettingLabelProperty = DependencyProperty.Register(nameof(SettingLabel), typeof(string), typeof(QuickSettingButton), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty AccessibleTextProperty = DependencyProperty.Register(nameof(AccessibleText), typeof(string), typeof(QuickSettingButton), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty SourceLabelProperty = DependencyProperty.Register(nameof(SourceLabel), typeof(string), typeof(QuickSettingButton), new PropertyMetadata(string.Empty));

    private void OnClick(object sender, RoutedEventArgs e) => Invoked?.Invoke(this, e);
}
