using Jiaolong.Contracts.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;

namespace Jiaolong_ControlCenter.Prototype;

public sealed partial class TrayQuickConsoleWindow
{
    private Action<int?>? automaticFanRequested;
    private Action<int>? fixedFanRequested;
    private Action<bool>? fanStrongCoolingRequested;
    private bool fanAutomaticAvailable;
    private bool fanFixedAvailable;
    private bool fanConnected;
    private bool fanBusy;
    private bool? fanStrongCooling;
    private int? confirmedFanCeiling;
    private int? confirmedFixedFanRpm;
    private int? fanCeilingDraft;
    private int? fixedFanDraft;
    private int fanMaximumRpm = 5800;
    private Slider? ceilingSlider;
    private Slider? fixedSlider;
    private TextBlock? ceilingValue;
    private TextBlock? fixedValue;
    private Button? automaticFanButton;
    private Button? applyCeilingButton;
    private Button? applyFixedButton;
    private ToggleSwitch? fanStrongToggle;
    private bool suppressFanControlEvents;

    public void ConfigureFanActions(Action<int?> automaticRequested, Action<int> fixedRequested, Action<bool> strongCoolingRequested)
    {
        automaticFanRequested = automaticRequested ?? throw new ArgumentNullException(nameof(automaticRequested));
        fixedFanRequested = fixedRequested ?? throw new ArgumentNullException(nameof(fixedRequested));
        fanStrongCoolingRequested = strongCoolingRequested ?? throw new ArgumentNullException(nameof(strongCoolingRequested));
    }

    public void ApplyFanState(bool connected, bool automaticAvailable, bool fixedAvailable,
        int? ceilingRpm, int? fixedRpm, bool? strongCooling, int maximumRpm = 5800)
    {
        fanConnected = connected;
        fanAutomaticAvailable = automaticAvailable;
        fanFixedAvailable = fixedAvailable;
        fanMaximumRpm = Math.Max(1800, maximumRpm);
        confirmedFanCeiling = ceilingRpm is >= 1800 && ceilingRpm <= fanMaximumRpm ? ceilingRpm : null;
        confirmedFixedFanRpm = fixedRpm is >= 1800 && fixedRpm <= fanMaximumRpm ? fixedRpm : null;
        fanStrongCooling = connected ? strongCooling : null;
        if (selectorView == "fan" && SelectorLayer.Visibility == Visibility.Visible)
        {
            RefreshFanValues();
            RefreshFanAvailability();
        }
    }
    public void SetFanBusy(bool busy) { fanBusy = busy; RefreshFanAvailability(); }

    private void RenderFanControls()
    {
        SelectorTitleText.Text = "快捷风扇控制";
        FanChoicesPanel.Children.Clear();
        suppressFanControlEvents = true;
        var autoSection = new StackPanel { Spacing = 5 };
        var autoHeader = new Grid { ColumnSpacing = 8 };
        autoHeader.ColumnDefinitions.Add(new ColumnDefinition());
        autoHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        automaticFanButton = SelectionButton(new TextBlock { Text = "EC 自动", FontSize = 11 }, "交还官方 EC 自动控制");
        automaticFanButton.Padding = new Thickness(8, 4, 8, 4);
        automaticFanButton.Click += (_, _) => RequestAutomaticFan(null);
        AutomationProperties.SetName(automaticFanButton, "交还官方 EC 自动控制");
        var help = new Button { Content = "?", Style = (Style)ConsoleRoot.Resources["TrayLinkStyle"], Width = 24, Height = 24 };
        Grid.SetColumn(help, 1);
        AutomationProperties.SetName(help, "风扇控制安全说明");
        ToolTipService.SetToolTip(help, "EC 自动可限制最大目标转速。固定转速由 CPU 与 GPU 两个风扇共同使用；温度达到安全阈值时交还官方风扇控制。目标值不代表当前实际 RPM，实际读数见监测带。强冷保持当前性能模式。");
        autoHeader.Children.Add(automaticFanButton); autoHeader.Children.Add(help);
        autoSection.Children.Add(autoHeader);
        var ceilingRow = FanValueRow("最大目标", out ceilingValue, out applyCeilingButton);
        AutomationProperties.SetName(applyCeilingButton, "应用 EC 自动最大目标转速");
        applyCeilingButton.Click += (_, _) => { if ((fanCeilingDraft ?? confirmedFanCeiling) is { } target) RequestAutomaticFan(target); };
        autoSection.Children.Add(ceilingRow);
        ceilingSlider = CreateFanSlider("EC 自动最大目标转速", fanCeilingDraft ?? confirmedFanCeiling);
        ceilingSlider.ValueChanged += (_, e) => { if (suppressFanControlEvents) return; fanCeilingDraft = (int)e.NewValue; RefreshFanValues(); RefreshFanAvailability(); };
        autoSection.Children.Add(ceilingSlider);
        FanChoicesPanel.Children.Add(autoSection);
        FanChoicesPanel.Children.Add(new Border { Height = 1, Background = TrayBrush("TrayStrokeBrush") });
        var fixedSection = new StackPanel { Spacing = 5 };
        fixedSection.Children.Add(new TextBlock { Text = "CPU · GPU 固定转速", FontSize = 11, Foreground = TrayBrush("TrayTextBrush") });
        var fixedRow = FanValueRow("共同目标", out fixedValue, out applyFixedButton);
        AutomationProperties.SetName(applyFixedButton, "应用 CPU 与 GPU 共同固定转速");
        applyFixedButton.Click += (_, _) =>
        {
            if ((fixedFanDraft ?? confirmedFixedFanRpm) is not { } target || fanBusy || !fanConnected || !fanFixedAvailable) return;
            SetFanBusy(true); fixedFanRequested?.Invoke(target);
        };
        fixedSection.Children.Add(fixedRow);
        fixedSlider = CreateFanSlider("CPU 与 GPU 共同固定目标转速", fixedFanDraft ?? confirmedFixedFanRpm);
        fixedSlider.ValueChanged += (_, e) => { if (suppressFanControlEvents) return; fixedFanDraft = (int)e.NewValue; RefreshFanValues(); RefreshFanAvailability(); };
        fixedSection.Children.Add(fixedSlider);
        FanChoicesPanel.Children.Add(fixedSection);
        var strongRow = new Grid();
        strongRow.ColumnDefinitions.Add(new ColumnDefinition()); strongRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        strongRow.Children.Add(new TextBlock { Text = "强冷", FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = TrayBrush("TrayTextBrush"), VerticalAlignment = VerticalAlignment.Center });
        fanStrongToggle = new ToggleSwitch { OnContent = "", OffContent = "", IsOn = fanStrongCooling == true, Style = (Style)Application.Current.Resources["PrototypeModeToggleSwitchStyle"] };
        Grid.SetColumn(fanStrongToggle, 1);
        AutomationProperties.SetName(fanStrongToggle, "快捷风扇强冷，不改变性能模式");
        fanStrongToggle.Toggled += (_, _) =>
        {
            if (suppressFanControlEvents || fanStrongCooling is null) return;
            bool requested = fanStrongToggle.IsOn;
            suppressFanControlEvents = true; fanStrongToggle.IsOn = fanStrongCooling.Value; suppressFanControlEvents = false;
            SetQuickSettingBusy(QuickSettingKind.StrongCooling, true);
            fanStrongCoolingRequested?.Invoke(requested);
        };
        strongRow.Children.Add(fanStrongToggle);
        FanChoicesPanel.Children.Add(strongRow);
        suppressFanControlEvents = false;
        RefreshFanValues(); RefreshFanAvailability();
    }
    private Grid FanValueRow(string label, out TextBlock value, out Button apply)
    {
        var row = new Grid { ColumnSpacing = 7 };
        row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new TextBlock { Text = label, FontSize = 10, Foreground = TrayBrush("TraySecondaryBrush"), VerticalAlignment = VerticalAlignment.Center });
        value = new TextBlock { Text = "-- RPM", FontSize = 10, Foreground = TrayBrush("TrayTextBrush"), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(value, 1); row.Children.Add(value);
        apply = new Button { Content = "应用", Style = (Style)ConsoleRoot.Resources["TrayLinkStyle"], MinWidth = 30, MinHeight = 22 };
        Grid.SetColumn(apply, 2); row.Children.Add(apply);
        return row;
    }
    private Slider CreateFanSlider(string name, int? target)
    {
        var slider = new Slider { Minimum = 1800, Maximum = fanMaximumRpm, StepFrequency = 100, Value = target ?? 1800, Height = 24 };
        AutomationProperties.SetName(slider, name);
        return slider;
    }
    private void RequestAutomaticFan(int? target)
    {
        if (fanBusy || !fanConnected || automaticFanRequested is null || target is not null && !fanAutomaticAvailable) return;
        SetFanBusy(true); automaticFanRequested(target);
    }
    private void RefreshFanValues()
    {
        suppressFanControlEvents = true;
        if (ceilingSlider is not null)
        {
            ceilingSlider.Maximum = fanMaximumRpm;
            if (fanCeilingDraft is null) ceilingSlider.Value = confirmedFanCeiling ?? ceilingSlider.Minimum;
        }
        if (fixedSlider is not null)
        {
            fixedSlider.Maximum = fanMaximumRpm;
            if (fixedFanDraft is null) fixedSlider.Value = confirmedFixedFanRpm ?? fixedSlider.Minimum;
        }
        if (ceilingValue is not null) ceilingValue.Text = (fanCeilingDraft ?? confirmedFanCeiling) is { } ceiling ? $"{ceiling} RPM" : "-- RPM";
        if (fixedValue is not null) fixedValue.Text = (fixedFanDraft ?? confirmedFixedFanRpm) is { } fixedRpm ? $"{fixedRpm} RPM" : "-- RPM";
        if (fanStrongToggle is not null) fanStrongToggle.IsOn = fanStrongCooling == true;
        suppressFanControlEvents = false;
    }
    private void RefreshFanAvailability()
    {
        bool automatic = fanConnected && !fanBusy && automaticFanRequested is not null;
        bool fixedAvailable = fanConnected && !fanBusy && fanFixedAvailable && fixedFanRequested is not null;
        if (automaticFanButton is not null) automaticFanButton.IsEnabled = automatic;
        if (ceilingSlider is not null) ceilingSlider.IsEnabled = automatic && fanAutomaticAvailable;
        if (applyCeilingButton is not null) applyCeilingButton.IsEnabled = automatic && fanAutomaticAvailable && (fanCeilingDraft ?? confirmedFanCeiling) is not null;
        if (fixedSlider is not null) fixedSlider.IsEnabled = fixedAvailable;
        if (applyFixedButton is not null) applyFixedButton.IsEnabled = fixedAvailable && (fixedFanDraft ?? confirmedFixedFanRpm) is not null;
        if (fanStrongToggle is not null) fanStrongToggle.IsEnabled = fanConnected && !fanBusy && fanStrongCooling is not null && !pendingQuickSettings.Contains(QuickSettingKind.StrongCooling) && fanStrongCoolingRequested is not null;
    }
}
