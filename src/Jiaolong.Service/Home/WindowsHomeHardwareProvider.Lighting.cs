using System.Diagnostics;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;
using Jiaolong.Hardware.Mechrevo.Controls;

namespace Jiaolong.Service.Home;

public sealed partial class WindowsHomeHardwareProvider
{
    private Timer? lightingTimer;
    private MiKeyboardLightingController? lightingController;
    private KeyboardLightingPlan? appliedLighting;
    private string? lightingError;
    private readonly Stopwatch lightingClock = new();
    private KeyboardRgb? lastLightingColor;
    private int lightingFailures;
    private long nextLightingOwnershipCheck;
    private readonly LightingPreviewLease lightingPreview = new();
    private KeyboardLightingPlan? lastPreviewPlan;

    private KeyboardLightingPlan? ReadLightingLocked(CancellationToken token)
    {
        if (appliedLighting is not null) return appliedLighting;
        if (compatibilityDecision is null || !compatibilityDecision.Capabilities.Items.Any(
                item => item.Key == "keyboardLighting" && item.State == CapabilityState.Available))
            return null;
        try
        {
            var controller = lightingController ?? new MiKeyboardLightingController(miInterface, compatibilityDecision);
            return controller.ReadHardwareAsync(token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            logger?.LogDebug(error, "Keyboard lighting hardware readback unavailable");
            return null;
        }
    }

    private CommandResult ExecuteLightingLocked(SetKeyboardLightingCommand command, CancellationToken token)
    {
        if (CommandValidation.Validate(command) is not null)
            return Rejected(command.OperationId, ErrorCode.ValidationFailed);
        if (compatibilityDecision is null || !compatibilityDecision.Capabilities.Items.Any(
                item => item.Key == "keyboardLighting" && item.State == CapabilityState.Available))
            return Rejected(command.OperationId, ErrorCode.CapabilityUnavailable);
        if (ThirdPartyFanWriterRunning())
            return Rejected(command.OperationId, ErrorCode.ConflictDetected);
        try
        {
            if (command.Preview)
            {
                lightingPreview.Begin(lightingPreview.Original ?? ReadLightingLocked(token), DateTimeOffset.UtcNow);
                if (lastPreviewPlan == command.Plan)
                    return new(command.OperationId, CommandState.Applied, null, RequiredUserAction.None, null, false);
            }
            var controller = new MiKeyboardLightingController(miInterface, compatibilityDecision);
            var plan = command.Preview ? command.Plan with { LogoEnabled = null } : command.Plan;
            var resolved = controller.ApplyAsync(plan, token).GetAwaiter().GetResult();
            // The provider gate serializes preset application with effect frames.
            lightingTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            lightingController = controller;
            bool softwareEffect = resolved.BrightnessLevel > 0 && resolved.Effect == "Gradient";
            appliedLighting = softwareEffect ? resolved : null;
            lightingError = null;
            lightingFailures = 0;
            nextLightingOwnershipCheck = 0;
            lastLightingColor = resolved.ColorAt(0);
            if (softwareEffect) lightingClock.Restart();
            else lightingClock.Reset();
            logger?.LogInformation("Keyboard lighting applied: {Effect}, level {Level}, speed {Speed}, operation {OperationId}",
                resolved.Effect, resolved.BrightnessLevel, resolved.Speed, command.OperationId);
            if (command.Preview) lastPreviewPlan = command.Plan;
            else { lightingPreview.Complete(); lastPreviewPlan = null; }
            if (softwareEffect || command.Preview)
            {
                lightingTimer ??= new Timer(OnLightingFrame, null, Timeout.Infinite, Timeout.Infinite);
                lightingTimer.Change(100, 100);
            }
            return new(command.OperationId, CommandState.Applied, null, RequiredUserAction.None, null, false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (lightingPreview.Original is not null) KeepPreviewRecoveryTimer();
            throw;
        }
        catch (Exception error)
        {
            logger?.LogWarning(error, "Keyboard lighting apply failed: {OperationId}", command.OperationId);
            lightingError = error.Message;
            if (lightingPreview.Original is not null) KeepPreviewRecoveryTimer();
            if (error.Message == "rollbackFailed")
            {
                appliedLighting = null;
                return new(command.OperationId, CommandState.RecoveryRequired, null, RequiredUserAction.None,
                    ServiceError.Create(ErrorCode.RollbackFailed, command.OperationId, false), false);
            }
            return Rejected(command.OperationId, ErrorCode.HardwareWriteFailed);
        }
    }

    private void OnLightingFrame(object? state)
    {
        // Skip a busy tick rather than queueing stale frames behind telemetry or hardware commands.
        if (!Monitor.TryEnter(gate)) return;
        try
        {
            if (disposed) return;
            if (lightingPreview.Expired(DateTimeOffset.UtcNow))
            {
                RestoreLightingPreviewLocked(Guid.NewGuid(), CancellationToken.None);
                return;
            }
            if (appliedLighting is null || lightingController is null) return;
            if (lightingClock.ElapsedMilliseconds >= nextLightingOwnershipCheck)
            {
                nextLightingOwnershipCheck = lightingClock.ElapsedMilliseconds + 1000;
                if (ThirdPartyFanWriterRunning())
                {
                    lightingError = "keyboardEffectStopped: competingControlCenter";
                    appliedLighting = null;
                    KeepPreviewRecoveryTimer();
                    logger?.LogWarning("Keyboard effect stopped because another control center started");
                    return;
                }
            }
            var color = appliedLighting.ColorAt(lightingClock.Elapsed.TotalSeconds);
            if (color == lastLightingColor) return;
            lightingController.WriteColorAsync(color, CancellationToken.None).GetAwaiter().GetResult();
            lastLightingColor = color;
            lightingFailures = 0;
        }
        catch (Exception error)
        {
            if (++lightingFailures < 3) return;
            lightingError = $"keyboardEffectStopped: {error.Message}";
            appliedLighting = null;
            KeepPreviewRecoveryTimer();
            logger?.LogError(error, "Keyboard lighting effect stopped after three failed writes");
        }
        finally { Monitor.Exit(gate); }
    }

    private CommandResult RestoreLightingPreviewLocked(Guid operationId, CancellationToken token)
    {
        if (lightingPreview.Original is not { } original)
            return new(operationId, CommandState.Applied, null, RequiredUserAction.None, null, false);
        var result = ExecuteLightingLocked(new(operationId, original), token);
        if (result.State != CommandState.Applied)
        {
            // Preserve the snapshot and retry slowly; never discard a failed recovery.
            lightingTimer ??= new Timer(OnLightingFrame, null, Timeout.Infinite, Timeout.Infinite);
            lightingTimer.Change(1000, 1000);
        }
        return result;
    }

    private void KeepPreviewRecoveryTimer()
    {
        if (lightingPreview.Original is null) lightingTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        else
        {
            lightingTimer ??= new Timer(OnLightingFrame, null, Timeout.Infinite, Timeout.Infinite);
            lightingTimer.Change(1000, 1000);
        }
    }
}
