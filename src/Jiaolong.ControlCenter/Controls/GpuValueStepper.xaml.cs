using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Jiaolong_ControlCenter.Controls;

public sealed partial class GpuValueStepper : UserControl
{
    private double minimum;
    private double maximum = 100d;
    private double smallChange = 1d;
    private double value;

    public GpuValueStepper() => InitializeComponent();

    public event EventHandler<double>? ValueChanged;
    public event EventHandler<double>? InputRejected;

    public double Minimum { get => minimum; set => minimum = value; }
    public double Maximum { get => maximum; set => maximum = value; }
    public double SmallChange { get => smallChange; set => smallChange = value; }
    public bool AllowNegative { get; set; }
    public bool RejectOutOfRangeInput { get; set; }
    public double Value
    {
        get => value;
        set
        {
            this.value = Math.Clamp(value, Minimum, Maximum);
            InputBox.Text = this.value.ToString("0.#");
            ValueChanged?.Invoke(this, this.value);
        }
    }

    private void OnBeforeTextChanging(TextBox sender, TextBoxBeforeTextChangingEventArgs args)
    {
        if (string.IsNullOrEmpty(args.NewText) || (AllowNegative && args.NewText == "-"))
            return;
        int start = AllowNegative && args.NewText[0] == '-' ? 1 : 0;
        args.Cancel = start == args.NewText.Length || args.NewText[start..].Any(character => !char.IsDigit(character));
    }
    private void OnInputLostFocus(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(InputBox.Text, out double parsed))
        {
            InputBox.Text = Value.ToString("0.#");
            return;
        }
        if (RejectOutOfRangeInput && (parsed < Minimum || parsed > Maximum))
        {
            InputBox.Text = Value.ToString("0.#");
            InputRejected?.Invoke(this, parsed);
            return;
        }
        Value = parsed;
    }

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        int delta = e.GetCurrentPoint(this).Properties.MouseWheelDelta;
        if (delta == 0) return;
        double next = Value + (delta > 0 ? SmallChange : -SmallChange);
        if (RejectOutOfRangeInput && (next < Minimum || next > Maximum))
            InputRejected?.Invoke(this, next);
        else
            Value = next;
        e.Handled = true;
    }
}
