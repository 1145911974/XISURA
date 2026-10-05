using Jiaolong_ControlCenter.ViewModels;

namespace Jiaolong_ControlCenter.Services;

public sealed class NavigationService(ShellViewModel shell)
{
    public IReadOnlyList<ShellDestination> Destinations => ShellDestination.All;

    public ShellDestination Current => shell.SelectedDestination;

    public event EventHandler<ShellDestination>? Navigated;

    public bool Navigate(ShellDestination destination)
    {
        if (!Destinations.Contains(destination) || !shell.Select(destination.Id)) return false;
        Navigated?.Invoke(this, destination);
        return true;
    }
}
