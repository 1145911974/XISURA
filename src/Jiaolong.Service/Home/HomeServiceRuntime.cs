using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;

namespace Jiaolong.Service.Home;

public sealed record HomeHardwareState(
    CapabilitySnapshot Capabilities,
    DeviceSupportState SupportState,
    string? Reason,
    HardwareIdentity Identity,
    HardwareSnapshot? Telemetry)
{
    public HomeControlState Controls { get; init; } = new(null, null, Array.Empty<QuickSettingStatus>());
}

public interface IHomeHardwareProvider
{
    Task<HomeHardwareState> DiagnoseAsync(CancellationToken cancellationToken);
    Task<HomeHardwareState> ReinitializeAsync(CancellationToken cancellationToken);
    Task<HardwareSnapshot> ReadTelemetryAsync(CancellationToken cancellationToken);
    Task<HomeControlState> ReadControlsAsync(CancellationToken cancellationToken);
    Task<CommandResult> ExecuteAsync(HardwareCommand command, CancellationToken cancellationToken);
}

public sealed class HomeServiceRuntime(IHomeHardwareProvider provider)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly SemaphoreSlim fanGate = new(1, 1);
    private Guid? fanOwner;
    private HomeHardwareState? state;
    private bool repairAttempted;

    public HomeHardwareState State => Volatile.Read(ref state) ?? new(
        new CapabilitySnapshot(Array.Empty<CapabilityDescriptor>()),
        DeviceSupportState.Diagnosing,
        "notInitialized",
        new HardwareIdentity("未知", "未知", "未知", "未知"),
        null);

    public async Task<HomeHardwareState> InitializeAsync(CancellationToken cancellationToken)
    {
        var current = await GetStateAsync(cancellationToken);
        if (current.SupportState == DeviceSupportState.Ready) return current;
        return await ReinitializeAsync(cancellationToken);
    }

    public async Task<HomeHardwareState> GetStateAsync(CancellationToken cancellationToken)
    {
        if (state is not null) return state;
        await gate.WaitAsync(cancellationToken);
        try
        {
            return Volatile.Read(ref state) ?? await DiagnoseAndStoreAsync(cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public Task<HardwareSnapshot> ReadTelemetryAsync(CancellationToken cancellationToken) =>
        provider.ReadTelemetryAsync(cancellationToken);

    public async Task<HomeStateSnapshot> GetHomeStateAsync(CancellationToken cancellationToken)
    {
        var current = await GetStateAsync(cancellationToken);
        var telemetry = await provider.ReadTelemetryAsync(cancellationToken);
        var controls = await provider.ReadControlsAsync(cancellationToken);
        var refreshed = current with { Telemetry = telemetry, Controls = controls };
        Volatile.Write(ref state, refreshed);
        return new(refreshed.Capabilities, refreshed.Telemetry, refreshed.Controls);
    }

    public async Task<CommandResult> ExecuteAsync(HardwareCommand command, CancellationToken cancellationToken, Guid connectionId = default)
    {
        if (command is not (SetFanControlCommand or ReleaseFanControlCommand or SetStrongCoolingCommand))
            return await ExecuteCoreAsync(command, cancellationToken);

        await fanGate.WaitAsync(cancellationToken);
        var previousOwner = fanOwner;
        try
        {
            if (command is SetFanControlCommand { PreserveStrongCooling: true } or ReleaseFanControlCommand { PreserveStrongCooling: true } &&
                (await provider.ReadControlsAsync(cancellationToken)).StrongCooling != false)
                return Rejected(command, ErrorCode.CommandInProgress);
            // Claim before dispatch: cancellation can arrive after a partial EC write.
            fanOwner = connectionId;
            var result = await ExecuteCoreAsync(command, cancellationToken);
            if (result.State == CommandState.Applied)
            {
                if (command is ReleaseFanControlCommand or SetStrongCoolingCommand { Enabled: false }) fanOwner = null;
            }
            else if (result.State != CommandState.RecoveryRequired) fanOwner = previousOwner;
            return result;
        }
        finally { fanGate.Release(); }
    }

    public async Task<CommandResult?> ReleaseClientFanAsync(Guid connectionId)
    {
        if (connectionId == Guid.Empty) return null;
        await fanGate.WaitAsync();
        try
        {
            if (fanOwner != connectionId) return null;
            // Bypass capability refresh: losing telemetry must not prevent EC cleanup.
            var result = await provider.ExecuteAsync(
                new ReleaseFanControlCommand(Guid.NewGuid(), ReleaseReason.SessionEnded), CancellationToken.None);
            if (result.State == CommandState.Applied) fanOwner = null;
            return result;
        }
        finally { fanGate.Release(); }
    }

    private async Task<CommandResult> ExecuteCoreAsync(HardwareCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        // Retain wire compatibility while rejecting the removed operation before any driver access.
        if (command is SetGpuVoltageBoostCommand) return Rejected(command, ErrorCode.CapabilityUnavailable);
        var current = await GetStateAsync(cancellationToken);
        if (!CanExecute(command, current))
        {
            if (!repairAttempted) current = await ReinitializeAsync(cancellationToken);
            if (!CanExecute(command, current)) return Rejected(command, ErrorFor(current));
        }

        var result = await provider.ExecuteAsync(command, cancellationToken);
        if (result.Error is null || !result.Error.IsRetryable)
        {
            await RefreshCommandStateAsync(command, result, cancellationToken);
            return result;
        }

        current = await ReinitializeAsync(cancellationToken);
        if (!CanExecute(command, current)) return Rejected(command, ErrorFor(current));
        result = await provider.ExecuteAsync(command, cancellationToken);
        await RefreshCommandStateAsync(command, result, cancellationToken);
        return result;
    }

    private async Task RefreshCommandStateAsync(HardwareCommand command, CommandResult result, CancellationToken cancellationToken)
    {
        // Context heartbeats do not change hardware; probing drivers here stalls every scheduling tick.
        if (command is SetAdaptiveAutomationConfigurationCommand or UpdateAdaptiveAutomationContextCommand) return;
        if (result.State == CommandState.Applied && result.Error is null && command is (SetPerformanceModeCommand or SetCpuTuningCommand or SetCpuTuningBatchCommand))
        {
            await GetHomeStateAsync(cancellationToken);
            return;
        }
        await RefreshStateAfterCommandAsync(cancellationToken);
    }

    public async Task<HomeHardwareState> ReinitializeAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var current = Volatile.Read(ref state) is null
                ? await provider.DiagnoseAsync(cancellationToken)
                : await provider.ReinitializeAsync(cancellationToken);
            repairAttempted = true;
            Volatile.Write(ref state, current);
            return current;
        }
        finally
        {
            gate.Release();
        }
    }

    private static bool CanExecute(HardwareCommand command, HomeHardwareState current)
    {
        if (current.SupportState != DeviceSupportState.Ready) return false;
        var key = HomeCapabilityCatalog.RequiredCapability(command);
        return current.Capabilities.Items.Any(item =>
            string.Equals(item.Key, key, StringComparison.Ordinal) &&
            item.State == CapabilityState.Available);
    }

    private static ErrorCode ErrorFor(HomeHardwareState current) => current.Reason switch
    {
        "deviceMismatch" => ErrorCode.DeviceMismatch,
        "biosUnsupported" => ErrorCode.BiosUnsupported,
        "dependencyMissing" => ErrorCode.DependencyMissing,
        "conflictDetected" => ErrorCode.ConflictDetected,
        "readOnlySafeMode" => ErrorCode.ReadOnlySafeMode,
        "verifiedWritableEvidenceMissing" => ErrorCode.ReadOnlySafeMode,
        _ => ErrorCode.CapabilityUnavailable
    };

    private static CommandResult Rejected(HardwareCommand command, ErrorCode code) =>
        new(command.OperationId, CommandState.Rejected, null, RequiredUserAction.None, ServiceError.Create(code, command.OperationId, false), false);

    private async Task<HomeHardwareState> DiagnoseAndStoreAsync(CancellationToken cancellationToken)
    {
        var diagnosed = await provider.DiagnoseAsync(cancellationToken);
        Volatile.Write(ref state, diagnosed);
        return diagnosed;
    }

    private async Task RefreshStateAfterCommandAsync(CancellationToken cancellationToken)
    {
        try
        {
            Volatile.Write(ref state, await provider.DiagnoseAsync(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // The command result remains authoritative when a follow-up diagnostic read fails.
        }
    }
}
