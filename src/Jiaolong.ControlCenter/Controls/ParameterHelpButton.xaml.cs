using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Jiaolong_ControlCenter.Controls;

public sealed record ParameterHelpContent(
    string Effect,
    string CurrentValue,
    string RecommendedRange,
    string SafeRange,
    string ExtremeLimit,
    string Risk);

public sealed partial class ParameterHelpButton : UserControl
{
    public ParameterHelpButton()
    {
        InitializeComponent();
        UpdateContent(Help);
    }

    public string ParameterName
    {
        get => (string)GetValue(ParameterNameProperty);
        set => SetValue(ParameterNameProperty, value);
    }

    public static readonly DependencyProperty ParameterNameProperty =
        DependencyProperty.Register(
            nameof(ParameterName),
            typeof(string),
            typeof(ParameterHelpButton),
            new PropertyMetadata(string.Empty, OnHelpTextChanged));

    public ParameterHelpContent Help
    {
        get => (ParameterHelpContent)GetValue(HelpProperty);
        set => SetValue(HelpProperty, value);
    }

    public static readonly DependencyProperty HelpProperty =
        DependencyProperty.Register(
            nameof(Help),
            typeof(ParameterHelpContent),
            typeof(ParameterHelpButton),
            new PropertyMetadata(new ParameterHelpContent(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty), OnHelpTextChanged));

    private static void OnHelpTextChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        var button = (ParameterHelpButton)dependencyObject;
        button.UpdateContent(button.Help);
    }

    private void UpdateContent(ParameterHelpContent content)
    {
        ParameterNameText.Text = ParameterName;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(HelpButton, $"{ParameterName}说明");
        SetSection(EffectText, "作用与影响", content.Effect);
        SetSection(CurrentValueText, "当前值", content.CurrentValue);
        SetSection(RecommendedRangeText, "推荐", content.RecommendedRange);
        SetSection(SafeRangeText, "保守建议", content.SafeRange);
        SetSection(ExtremeLimitText, "极限与边界", content.ExtremeLimit);
        SetSection(RiskText, "风险", content.Risk);
    }

    private static void SetSection(TextBlock section, string label, string value)
    {
        bool hasValue = !string.IsNullOrWhiteSpace(value);
        section.Text = hasValue ? $"{label}：{value}" : string.Empty;
        section.Visibility = hasValue ? Visibility.Visible : Visibility.Collapsed;
    }

}
