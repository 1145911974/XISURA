using Jiaolong_ControlCenter.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Jiaolong_ControlCenter.Pages;

public sealed partial class FanPage : Page
{
    public FanViewModel ViewModel { get; } = new(readOnly: true);

    public FanPage() => InitializeComponent();

    private async void OnPreviewClick(object sender, RoutedEventArgs e)
    {
        var result = await ViewModel.PreviewAsync(ViewModel.Draft, CancellationToken.None);
        StatusText.Text = result.Summary;
    }

    private async void OnApplyClick(object sender, RoutedEventArgs e)
    {
        var result = await ViewModel.ApplyAsync(ViewModel.Draft, new RiskAcknowledgement(false, false), CancellationToken.None);
        StatusText.Text = result.Error is null ? "已应用，等待硬件回读" : "应用失败，已恢复原设置";
    }

    private async void OnReleaseClick(object sender, RoutedEventArgs e)
    {
        var result = await ViewModel.ReleaseAsync(CancellationToken.None);
        StatusText.Text = result.Error is null ? "已交还 EC 自动控制" : "正在释放风扇控制";
    }
}
