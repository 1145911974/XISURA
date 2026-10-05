using Jiaolong.Contracts.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Jiaolong_ControlCenter.Controls;

public sealed partial class ModeRail : UserControl
{
    public ModeRail() => InitializeComponent();

    public event EventHandler<PerformanceMode>? ModeSelected;

    private void OnModeClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && Enum.TryParse<PerformanceMode>(tag, out var mode))
        {
            ModeSelected?.Invoke(this, mode);
        }
    }
}
