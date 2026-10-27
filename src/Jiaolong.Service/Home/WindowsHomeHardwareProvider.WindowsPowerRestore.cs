using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Hardware.Mechrevo.Controls;

namespace Jiaolong.Service.Home;

public sealed partial class WindowsHomeHardwareProvider
{
    public Task<WindowsPowerSettingsSnapshot?> ReadWindowsPowerSchemeAsync(Guid scheme, CancellationToken token) => Task.Run(() =>
    {
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            EnsureProviderReadAccess();
            var read = powerSettings?.ReadSchemeAsync(scheme, token).GetAwaiter().GetResult();
            EnsureProviderReadAccess();
            return read;
        }
    }, token);

    // Frozen service recovery targets only; there is no IPC command accepting these values.
    public Task<CommandResult> RestoreWindowsPowerSettingAsync(Guid id, Guid scheme, WindowsCpuSetting setting,
        int expected, int target, CancellationToken token) => Task.Run(() =>
    {
        lock (commandGate)
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            if (BackgroundWorkPaused || powerSettings is null)
                return Rejected(id, ErrorCode.CapabilityUnavailable) with { Mutation = HardwareMutation.NoMutation };
            try
            {
                bool changed = powerSettings.RestoreSettingAsync(scheme, setting, expected, target, token).GetAwaiter().GetResult();
                return new CommandResult(id, CommandState.Applied, null, RequiredUserAction.None, null, false)
                    { Mutation = changed ? HardwareMutation.Changed : HardwareMutation.NoMutation };
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                bool attempted = error.Data["windowsPowerWriteAttempted"] is true;
                var code = attempted ? ErrorCode.ReadBackMismatch : error switch
                {
                    ArgumentException => ErrorCode.ValidationFailed,
                    InvalidOperationException { Message: "windowsPowerSettingExternallyChanged" } => ErrorCode.ConflictDetected,
                    _ => ErrorCode.HardwareReadFailed
                };
                return Rejected(id, code) with
                { State = attempted ? CommandState.RecoveryRequired : CommandState.Rejected,
                  Mutation = attempted ? HardwareMutation.Unknown : HardwareMutation.NoMutation };
            }
        }
    }, token);
}
