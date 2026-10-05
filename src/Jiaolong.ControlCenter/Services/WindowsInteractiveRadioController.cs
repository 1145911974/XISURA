using Jiaolong.Contracts.Models;
using Windows.Devices.Radios;

namespace Jiaolong_ControlCenter.Services;

public sealed record HomeRadioSnapshot(bool? WifiEnabled, bool? BluetoothEnabled, string? Reason)
{
    public static HomeRadioSnapshot Unknown { get; } = new(null, null, "radioApiUnavailable");
}

public interface IInteractiveRadioController
{
    Task<HomeRadioSnapshot> ReadAsync(CancellationToken cancellationToken);
    Task<bool> SetAsync(QuickSettingKind setting, bool enabled, CancellationToken cancellationToken);
}

public sealed class WindowsInteractiveRadioController : IInteractiveRadioController
{
    private readonly SemaphoreSlim accessGate = new(1, 1);
    private RadioAccessStatus? accessStatus;

    public async Task<HomeRadioSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var radios = await Radio.GetRadiosAsync();
            return new(StateOf(radios, RadioKind.WiFi), StateOf(radios, RadioKind.Bluetooth), null);
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

    public async Task<bool> SetAsync(QuickSettingKind setting, bool enabled, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var kind = setting switch
        {
            QuickSettingKind.Wifi => RadioKind.WiFi,
            QuickSettingKind.Bluetooth => RadioKind.Bluetooth,
            _ => (RadioKind?)null
        };
        if (kind is not RadioKind radioKind) return false;

        try
        {
            var radios = await Radio.GetRadiosAsync();
            var radio = radios.FirstOrDefault(candidate => candidate.Kind == radioKind);
            if (radio is null || !await HasControlAccessAsync(cancellationToken)) return false;

            var target = enabled ? RadioState.On : RadioState.Off;
            if (await radio.SetStateAsync(target) != RadioAccessStatus.Allowed) return false;
            for (var attempt = 0; attempt < 10 && radio.State != target; attempt++)
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
            return radio.State == target;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> HasControlAccessAsync(CancellationToken cancellationToken)
    {
        if (accessStatus is RadioAccessStatus status) return status == RadioAccessStatus.Allowed;
        await accessGate.WaitAsync(cancellationToken);
        try
        {
            accessStatus ??= await Radio.RequestAccessAsync();
            return accessStatus == RadioAccessStatus.Allowed;
        }
        finally
        {
            accessGate.Release();
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
