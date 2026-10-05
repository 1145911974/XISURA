using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Jiaolong_ControlCenter.Prototype.Controls;

public sealed partial class PrototypePageWorkspace
{
    private string appliedLogoStyle = "explore";
    private bool synchronizingLogoStyle;

    private void RefreshLogoStyleSetting() => RenderLogoStyleSetting(preferences.Load().LogoStyle);

    private void RenderLogoStyleSetting(string style)
    {
        appliedLogoStyle = style;
        synchronizingLogoStyle = true;
        try
        {
            SettingsLogoStyleSelector.SelectedIndex = style == "classic" ? 1 : 0;
        }
        finally
        {
            synchronizingLogoStyle = false;
        }
    }

    private void OnSettingsLogoStyleChanged(object sender, SelectionChangedEventArgs args)
    {
        if (synchronizingLogoStyle || SettingsLogoStyleSelector.SelectedItem is not ComboBoxItem { Tag: string style } || style is not ("explore" or "classic")) return;
        if (style == appliedLogoStyle)
        {
            RenderLogoStyleSetting(appliedLogoStyle);
            return;
        }
        string savedStyle;
        try
        {
            savedStyle = preferences.Update(current => current with { LogoStyle = style }).LogoStyle;
        }
        catch (Exception)
        {
            RenderLogoStyleSetting(appliedLogoStyle);
            SettingsStatusText.Text = "保存 Logo 样式失败，请重试";
            SettingsStatusText.Visibility = Visibility.Visible;
            return;
        }
        RenderLogoStyleSetting(savedStyle);
        if (SettingsStatusText.Text == "保存 Logo 样式失败，请重试") SettingsStatusText.Visibility = Visibility.Collapsed;
        LogoStyleChanged?.Invoke(savedStyle);
    }
}
