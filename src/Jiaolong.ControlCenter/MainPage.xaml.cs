using Jiaolong_ControlCenter.ViewModels;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Jiaolong_ControlCenter;

public sealed partial class MainPage : Page
{
    public MainPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        DestinationTitle.Text = e.Parameter is ShellDestination destination
            ? destination.Title
            : ShellDestination.All[0].Title;
    }
}
