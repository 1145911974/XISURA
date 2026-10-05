using Jiaolong_ControlCenter.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace Jiaolong_ControlCenter.Controls;

public sealed partial class FanCurveEditor : UserControl
{
    public FanCurveEditor() => InitializeComponent();

    public FanCurve? Curve { get; set; }
}
