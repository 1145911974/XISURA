using Jiaolong_ControlCenter.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace Jiaolong_ControlCenter.Pages;

public sealed partial class HomePage : Page
{
    public HomeViewModel ViewModel { get; } = new(client: new Jiaolong_ControlCenter.Services.ControlCenterClient());

    public HomePage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) =>
        await ViewModel.ActivateAsync(CancellationToken.None);

    private async void OnUnloaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) =>
        await ViewModel.DeactivateAsync();
}
