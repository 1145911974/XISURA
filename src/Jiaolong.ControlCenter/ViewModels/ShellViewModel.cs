using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Jiaolong_ControlCenter.ViewModels;

public sealed class ShellViewModel : INotifyPropertyChanged
{
    private ShellDestination selectedDestination = ShellDestination.All[0];

    public IReadOnlyList<ShellDestination> Destinations => ShellDestination.All;

    public IReadOnlyList<QuickSettingItem> QuickSettings => QuickSettingsFactory.CreateDefault();

    public ShellDestination SelectedDestination
    {
        get => selectedDestination;
        private set
        {
            if (selectedDestination == value) return;
            selectedDestination = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool Select(string id)
    {
        var destination = Destinations.FirstOrDefault(x => x.Id == id);
        if (destination is null) return false;
        SelectedDestination = destination;
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
