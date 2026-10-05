using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Jiaolong_ControlCenter.Controls;

public sealed partial class CommandStateIndicator : UserControl
{
    public CommandStateIndicator() => InitializeComponent();

    public string State { get => (string)GetValue(StateProperty); set => SetValue(StateProperty, value); }
    public string Details { get => (string)GetValue(DetailsProperty); set => SetValue(DetailsProperty, value); }
    public string AccessibleText { get => (string)GetValue(AccessibleTextProperty); set => SetValue(AccessibleTextProperty, value); }

    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(nameof(State), typeof(string), typeof(CommandStateIndicator), new PropertyMetadata("数据暂不可用"));
    public static readonly DependencyProperty DetailsProperty = DependencyProperty.Register(nameof(Details), typeof(string), typeof(CommandStateIndicator), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty AccessibleTextProperty = DependencyProperty.Register(nameof(AccessibleText), typeof(string), typeof(CommandStateIndicator), new PropertyMetadata(string.Empty));
}
