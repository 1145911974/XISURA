using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Jiaolong_ControlCenter.Pages;

public sealed partial class GpuPage : Page
{
    public GpuViewModel ViewModel { get; } = new();

    public GpuPage() => InitializeComponent();

    private void OnMuxSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MuxOptions.SelectedItem is RadioButton { Tag: string tag } && Enum.TryParse<MuxMode>(tag, out var mode))
        {
            RestartBar.IsOpen = mode != MuxMode.Hybrid;
        }
    }

    private async void OnMuxApplyClick(object sender, RoutedEventArgs e)
    {
        if (MuxOptions.SelectedItem is not RadioButton { Tag: string tag } || !Enum.TryParse<MuxMode>(tag, out var mode)) return;
        var result = await ViewModel.ApplyMuxAsync(mode, impactConfirmed: true, CancellationToken.None);
        RestartBar.IsOpen = result.Error is null;
    }

    private void OnMuxCancelClick(object sender, RoutedEventArgs e)
    {
        MuxOptions.SelectedItem = null;
        RestartBar.IsOpen = false;
    }

    private void OnOpenRestartOptionsClick(object sender, RoutedEventArgs e) => ViewModel.OpenWindowsRestartOptions();
}
