using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;
using Jiaolong.Hardware.Mechrevo.Controls;

namespace Jiaolong.Service.Home;

// Used by the isolated live-read host; no mutating call reaches the physical provider.
public sealed class ReadOnlyHomeHardwareProvider(IHomeHardwareProvider inner) : IHomeHardwareProvider
{
    public async Task<HomeHardwareState> DiagnoseAsync(CancellationToken token)
    {
        var actual = await inner.DiagnoseAsync(token);
        return actual with
        {
            SupportState = DeviceSupportState.ReadOnly,
            Reason = "readOnlyVerification",
            Capabilities = actual.Capabilities with
            {
                SupportState = DeviceSupportState.ReadOnly,
                Reason = "readOnlyVerification",
                Items = actual.Capabilities.Items.Select(item => item.State == CapabilityState.Available
                    ? item with { State = CapabilityState.ReadOnly } : item).ToArray(),
                Fields = actual.Capabilities.Fields.Select(field => field with { Writable = false }).ToArray()
            }
        };
    }

    public Task<HomeHardwareState> ReinitializeAsync(CancellationToken token) => DiagnoseAsync(token);
    public Task<HardwareSnapshot> ReadTelemetryAsync(CancellationToken token) => inner.ReadTelemetryAsync(token);
    public Task<HomeControlState> ReadControlsAsync(CancellationToken token) => inner.ReadControlsAsync(token);
    public Task<HomeControlState> ReadControlBaselineAsync(CancellationToken token, bool includeCpuAdvanced = false) =>
        inner.ReadControlBaselineAsync(token, includeCpuAdvanced);
    public Task<OemCpuConfigurationSnapshot?> ReadOemCpuConfigurationAsync(CancellationToken token) =>
        inner.ReadOemCpuConfigurationAsync(token);
    public Task<string?> ReadDeviceRecoveryKeyAsync(HardwareIdentity identity, CancellationToken token) => inner.ReadDeviceRecoveryKeyAsync(identity, token);
    public Task<bool> ReconcileGpuRecoveryAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(false); }
    public Task<CommandResult> RecoverPendingGpuAsync(Guid id, CancellationToken token) => Reject(id, token);
    public Task<bool> VerifyPendingGpuTargetAsync(Guid id, CancellationToken token) => Task.FromResult(false);
    public Task<CommandResult> RecoverSessionFanAsync(Guid id, FanEcRecoveryScope scope, CancellationToken token) => Reject(id, token);
    public Task<WindowsPowerSettingsSnapshot?> ReadWindowsPowerSchemeAsync(Guid scheme, CancellationToken token) => inner.ReadWindowsPowerSchemeAsync(scheme, token);
    public Task<CommandResult> RestoreWindowsPowerSettingAsync(Guid id, Guid scheme, WindowsCpuSetting setting, int expected, int target, CancellationToken token) => Reject(id, token);
    public Task<PerformanceModeEvidence?> ReadModeEvidenceAsync(CancellationToken token) => inner.ReadModeEvidenceAsync(token);
    public Task<PerformanceModeEvidence?> ReadNativeModeEvidenceAsync(CancellationToken token) => inner.ReadNativeModeEvidenceAsync(token);
    public void SetBackgroundWorkPaused(bool paused) => inner.SetBackgroundWorkPaused(paused);
    public void SetBackgroundWorkState(PowerLifecycleState state) => inner.SetBackgroundWorkState(state);
    public long? PrepareShutdownCleanup() => inner.PrepareShutdownCleanup();
    public Task<CommandResult> ExecuteAsync(HardwareCommand command, CancellationToken token) => Reject(command.OperationId, token);
    public Task<CommandResult> RestoreCpuFieldAsync(Guid id, string field, CpuFieldRestorationValue baseline,
        CpuFieldRestorationValue expected, CancellationToken token) => Reject(id, token);
    private static Task<CommandResult> Reject(Guid id, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(new CommandResult(id, CommandState.Rejected, null, RequiredUserAction.None,
            ServiceError.Create(ErrorCode.ReadOnlySafeMode, id, false), false) { Mutation = HardwareMutation.NoMutation });
    }
}
