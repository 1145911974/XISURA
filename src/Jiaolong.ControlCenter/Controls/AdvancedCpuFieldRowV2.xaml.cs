using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Jiaolong_ControlCenter.Controls;

public sealed partial class AdvancedCpuFieldRowV2 : UserControl
{
    private bool configured;
    private bool synchronizing;
    private bool hasPendingValue;
    private double? pendingValue;
    private string? hardwareValue;
    private double? hardwareRawValue;
    private double? draftValue;
    private string synchronizedText = string.Empty;

    public AdvancedCpuFieldRowV2()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    public string Label { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public double Minimum { get; set; }
    public double Maximum { get; set; } = 100d;
    public double RecommendedValue { get; set; }
    public double LimitValue { get; set; } = 100d;
    public double DisplayScale { get; set; } = 1d;
    public double StepFrequency { get; set; } = 1d;
    public string Unit { get; set; } = string.Empty;

    public double? Value => hasPendingValue ? pendingValue : draftValue;

    public event EventHandler<double?>? ValueChanged;
    private bool allowUnknownDraft;

    public void SetHardwareValue(double? value)
    {
        hardwareRawValue = value;
        hardwareValue = value.HasValue ? $"硬件 {Format(value.Value)} {Unit}" : "当前值不可读";
        if (configured)
        {
            DescriptionText.Text = hardwareValue;
            if (draftValue is null) SetValue(null);
        }
    }

    public void EnableDraftEditing()
    {
        allowUnknownDraft = true;
        ValueBox.IsEnabled = true;
    }

    public void SetValue(double? value)
    {
        if (!configured)
        {
            pendingValue = value;
            hasPendingValue = true;
            return;
        }

        synchronizing = true;
        draftValue = value;
        // A movable seed is not a submitted target until the user edits this row.
        double? displayedValue = value ?? hardwareRawValue;
        Rail.SetValue(displayedValue ?? (allowUnknownDraft ? RecommendedValue : null));
        ValueBox.IsEnabled = value.HasValue || allowUnknownDraft;
        SetSynchronizedText(displayedValue is double actual ? Format(actual) : string.Empty);
        synchronizing = false;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (configured)
            return;

        configured = true;
        LabelText.Text = Label;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ValueBox, Label);
        DescriptionText.Text = hardwareValue ?? Description;
        UnitText.Text = Unit;
        Rail.Minimum = Minimum;
        Rail.Maximum = Maximum;
        Rail.RecommendedValue = RecommendedValue;
        Rail.LimitValue = LimitValue;
        Rail.DisplayScale = DisplayScale;
        Rail.StepFrequency = StepFrequency;
        Rail.Unit = Unit;
        Rail.Configure();
        Rail.ValueChanged += OnRailValueChanged;
        SetValue(hasPendingValue ? pendingValue : null);
        hasPendingValue = false;
        pendingValue = null;
    }

    private void OnRailValueChanged(object? sender, double value)
    {
        if (synchronizing)
            return;

        synchronizing = true;
        draftValue = value;
        SetSynchronizedText(Format(value));
        ValueBox.IsEnabled = true;
        synchronizing = false;
        ValueChanged?.Invoke(this, value);
    }

    private void OnValueBoxTextChanged(object sender, TextChangedEventArgs args)
    {
        // TextChanged may arrive after SetValue returns; ignore its unchanged programmatic text.
        if (!configured || synchronizing || ValueBox.Text == synchronizedText || !TryParse(ValueBox.Text, out double displayed))
            return;

        synchronizedText = ValueBox.Text;
        double value = Math.Clamp(PerformanceDisplayValue.ToRaw(displayed, DisplayScale), Minimum, Maximum);
        draftValue = value;
        synchronizing = true;
        Rail.SetValue(value);
        synchronizing = false;
        ValueChanged?.Invoke(this, value);
    }

    private void OnValueBoxLostFocus(object sender, RoutedEventArgs args)
    {
        // Showing a hardware readback must not turn it into a new write target on blur.
        if (ValueBox.Text == synchronizedText)
        {
            SetValue(draftValue);
            return;
        }
        double? value = TryParse(ValueBox.Text, out double displayed)
            ? Math.Clamp(PerformanceDisplayValue.ToRaw(displayed, DisplayScale), Minimum, Maximum)
            : draftValue;
        SetValue(value);
    }

    private string Format(double value) =>
        PerformanceDisplayValue.ToDisplay(value, DisplayScale).ToString("0.##", CultureInfo.CurrentCulture);

    private void SetSynchronizedText(string text)
    {
        synchronizedText = text;
        if (ValueBox.Text != text) ValueBox.Text = text;
    }

    private bool TryParse(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) && double.IsFinite(value);
}
