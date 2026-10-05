using Jiaolong.Contracts.Models;

namespace Jiaolong.Hardware.Mechrevo.Controls;

public interface IQuickSettingsController
{
    Task<bool> ReadAsync(QuickSettingKind setting, CancellationToken cancellationToken);
    Task WriteAsync(QuickSettingKind setting, bool enabled, CancellationToken cancellationToken);
}

public interface IQuickSettingsTransport
{
    Task<bool> ReadAsync(QuickSettingKind setting, CancellationToken cancellationToken);
    Task WriteAsync(QuickSettingKind setting, bool enabled, CancellationToken cancellationToken);
}

public sealed class QuickSettingsController(IQuickSettingsTransport? transport = null) : IQuickSettingsController
{
    public Task<bool> ReadAsync(QuickSettingKind setting, CancellationToken cancellationToken) =>
        transport?.ReadAsync(setting, cancellationToken)
        ?? Task.FromException<bool>(new InvalidOperationException("quickSettingUnavailable"));

    public async Task WriteAsync(QuickSettingKind setting, bool enabled, CancellationToken cancellationToken)
    {
        if (setting is QuickSettingKind.NumLock or QuickSettingKind.CapsLock or QuickSettingKind.Osd or QuickSettingKind.Wifi)
        {
            throw new InvalidOperationException("sessionQuickSettingIsUserOwned");
        }

        if (transport is null) throw new InvalidOperationException("quickSettingUnavailable");
        _ = await transport.ReadAsync(setting, cancellationToken);
        await transport.WriteAsync(setting, enabled, cancellationToken);
        _ = await transport.ReadAsync(setting, cancellationToken);
    }
}
