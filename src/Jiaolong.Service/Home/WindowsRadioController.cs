using Jiaolong.Contracts.Models;
using Windows.Devices.Radios;

namespace Jiaolong.Service.Home;

public sealed record HomeRadioSnapshot(
    bool? WifiEnabled,
    bool? BluetoothEnabled,
    string? Reason)
{
    public static HomeRadioSnapshot Unknown { get; } = new(null, null, "radioApiUnavailable");
}

public interface IHomeRadioStateReader
{
    Task<HomeRadioSnapshot> ReadAsync(CancellationToken cancellationToken);
}

public sealed class WindowsRadioController : IHomeRadioStateReader
{
    public async Task<HomeRadioSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var radios = await Radio.GetRadiosAsync();
            return new(
                StateOf(radios, RadioKind.WiFi),
                StateOf(radios, RadioKind.Bluetooth),
                null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return HomeRadioSnapshot.Unknown;
        }
    }

    private static bool? StateOf(IReadOnlyList<Radio> radios, RadioKind kind) =>
        radios.FirstOrDefault(radio => radio.Kind == kind)?.State switch
        {
            RadioState.On => true,
            RadioState.Off => false,
            _ => null
        };
}
