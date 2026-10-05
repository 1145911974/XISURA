using Jiaolong_ControlCenter.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Jiaolong_ControlCenter.Pages;

public sealed partial class LightingPage : Page
{
    public LightingViewModel ViewModel { get; } = new();

    public LightingPage() => InitializeComponent();

    private void OnApplyClick(object sender, RoutedEventArgs e) =>
        StatusText.Text = "应用失败，已保持当前安全状态";

    private void OnColorPresetClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string hex)
        {
            try
            {
                var color = (Windows.UI.Color)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(typeof(Windows.UI.Color), hex);
                FixedColorPicker.Color = color;
                KeyboardColorPreview.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(color);
            }
            catch { }
        }
    }

    private void OnColorPickerChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (KeyboardColorPreview is not null)
        {
            KeyboardColorPreview.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(args.NewColor);
        }
    }
}
