using Jiaolong_ControlCenter.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Jiaolong_ControlCenter.Pages;

public sealed partial class PerformancePage : Page
{
    public PerformanceViewModel ViewModel { get; } = new();

    public PerformancePage()
    {
        InitializeComponent();
    }

    private void OnProfileClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button selected) return;

        foreach (var button in ProfileButtons.Children.OfType<Button>())
        {
            button.Background = (Brush)Application.Current.Resources["SurfaceRaisedBrush"];
            button.Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"];
        }

        selected.Background = (Brush)Application.Current.Resources["BrandBrush"];
        selected.Foreground = new SolidColorBrush(Microsoft.UI.Colors.White);
        CurrentProfileText.Text = selected.Content?.ToString() ?? "未命名";
        CurrentProfileBaselineText.Text = $"{selected.Tag} · 当前仅保存草稿";
    }

    private async void OnValidateClick(object sender, RoutedEventArgs e)
    {
        var summary = await ViewModel.ValidateAsync(ReadDraft(), CancellationToken.None);
        ValidationText.Text = summary.IsValid
            ? "草稿验证通过，仍在已核实范围内；硬件写入待服务接入。"
            : string.Join("；", summary.Errors);
    }

    private void OnApplyClick(object sender, RoutedEventArgs e)
    {
        ValidationText.Text = "硬件写入通道尚未接入，未修改当前电脑；草稿仍保留在页面中。";
    }

    private void OnRestoreClick(object sender, RoutedEventArgs e)
    {
        TemperatureBox.Value = 95;
        SplBox.Value = 45;
        SpptBox.Value = 65;
        FrequencyBox.Value = 4500;
        BoostToggle.IsOn = false;
        WindowsPowerSchemeBox.SelectedIndex = 0;
        ValidationText.Text = "已恢复当前档位的草稿值；尚未写入硬件。";
    }

    private PerformanceDraft ReadDraft() => new()
    {
        TemperatureLimitC = (int)TemperatureBox.Value,
        SplWatts = (int)SplBox.Value,
        SpptWatts = (int)SpptBox.Value,
        MaxFrequencyMhz = (int)FrequencyBox.Value,
        IsBoostEnabled = BoostToggle.IsOn
    };
}
