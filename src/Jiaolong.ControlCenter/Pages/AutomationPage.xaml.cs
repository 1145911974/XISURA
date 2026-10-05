using Jiaolong_ControlCenter.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace Jiaolong_ControlCenter.Pages;

public sealed partial class AutomationPage : Page
{
    public AutomationViewModel ViewModel { get; } = new();

    public AutomationPage() => InitializeComponent();
}
