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
    private bool lightingOwnershipLost;
    private long nextLightingOwnershipCheck;
    private readonly LightingPreviewLease lightingPreview = new();
    private KeyboardLightingPlan? lastPreviewPlan;

    private KeyboardLightingPlan? ReadLightingLocked(CancellationToken token)
    {
        if (compatibilityDecision is null || !compatibilityDecision.Capabilities.Items.Any(
                item => item.Key == "keyboardLighting" && item.State == CapabilityState.Available))
            return null;
        try
        {
            var controller = lightingController ?? new MiKeyboardLightingController(miInterface, compatibilityDecision);
            var observed = controller.ReadHardwareAsync(token).GetAwaiter().GetResult();
            if (appliedLighting is not { } effect) return observed;
            // Gradient uses firmware Fixed; only our last confirmed frame can justify virtual effect metadata.
            if (MatchesLightingFrame(observed, effect, lastLightingColor))
                return effect with { LogoEnabled = observed.LogoEnabled };
            StopLightingOwnershipLocked("externalLightingChange");
            return observed;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            if (appliedLighting is not null) StopLightingOwnershipLocked("lightingReadUnavailable");
            logger?.LogDebug(error, "Keyboard lighting hardware readback unavailable");
            return null;
        }
    }

    private static bool MatchesLightingFrame(KeyboardLightingPlan observed, KeyboardLightingPlan effect, KeyboardRgb? frame) =>
        observed.IsValid() && observed.Effect == "Fixed" && observed.BrightnessLevel == effect.BrightnessLevel &&
        frame is { } value && observed.Red == value.Red && observed.Green == value.Green && observed.Blue == value.Blue;

    public Task StopClientLightingDynamicsAsync(CancellationToken token)
    {
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            if (lightingPreview.Original is not null) throw new IOException("lightingPreviewRecoveryRequired");
            lightingTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            lightingClock.Stop();
            if (appliedLighting is not { } effect) return Task.CompletedTask;
            if (lightingOwnershipLost) throw new IOException("lightingRetainedOwnershipLost");
            EnsureProviderReadAccess();
            var observed = (lightingController ?? throw new IOException("lightingControllerUnavailable"))
                .ReadHardwareAsync(token).GetAwaiter().GetResult();
            token.ThrowIfCancellationRequested();
            if (!MatchesLightingFrame(observed, effect, lastLightingColor))
                throw new IOException("lightingRetainedFrameChanged");
            // Retain the physical frame, not the software Gradient or its timer.
            appliedLighting = null;
            return Task.CompletedTask;
        }
    }

    private CommandResult ExecuteLightingLocked(SetKeyboardLightingCommand command, CancellationToken token)
    {
        if (BackgroundWorkPaused) return Rejected(command.OperationId, ErrorCode.CancelledBeforeApply) with { Mutation = HardwareMutation.NoMutation };
        if (CommandValidation.Validate(command) is not null)
            return Rejected(command.OperationId, ErrorCode.ValidationFailed);
        if (command.Preview && lightingOwnershipLost)
            return Rejected(command.OperationId, ErrorCode.ConflictDetected) with { Mutation = HardwareMutation.NoMutation };
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
            lightingOwnershipLost = false;
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
        catch (OperationCanceledException error) when (token.IsCancellationRequested &&
            error.Data["keyboardLightingWriteAttempted"] is not false)
        {
            if (lightingPreview.Original is not null) KeepPreviewRecoveryTimer();
            throw;
        }
        catch (Exception error)
        {
            logger?.LogWarning(error, "Keyboard lighting apply failed: {OperationId}", command.OperationId);
            lightingError = error.Message;
            if (lightingPreview.Original is not null) KeepPreviewRecoveryTimer();
            if (error.Data["keyboardLightingWriteAttempted"] is false)
            {
                var code = error is OperationCanceledException || error.Message == "hardwareAccessPaused" ? ErrorCode.CancelledBeforeApply : ErrorCode.HardwareWriteFailed;
                return Rejected(command.OperationId, code) with
                {
                    Mutation = HardwareMutation.NoMutation,
                    Error = ServiceError.Create(code, command.OperationId, false) with
                    { Details = new() { ["reason"] = error.Message, ["writeAttempted"] = bool.FalseString } }
                };
            }
            if (error.Message == "rollbackFailed")
            {
                StopLightingOwnershipLocked("lightingRollbackFailed");
                return new(command.OperationId, CommandState.RecoveryRequired, null, RequiredUserAction.None,
                    ServiceError.Create(ErrorCode.RollbackFailed, command.OperationId, false), false);
            }
            return Rejected(command.OperationId, ErrorCode.HardwareWriteFailed);
        }
    }

    private void OnLightingFrame(object? state)
    {
        if (BackgroundWorkPaused) return;
        // Skip a busy tick rather than queueing stale frames behind telemetry or hardware commands.
        if (!Monitor.TryEnter(gate)) return;
        try
        {
            if (disposed || BackgroundWorkPaused) return;
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
                    StopLightingOwnershipLocked("competingControlCenter");
                    logger?.LogWarning("Keyboard effect stopped because another control center started");
                    return;
                }
            }
            var color = appliedLighting.ColorAt(lightingClock.Elapsed.TotalSeconds);
            if (color == lastLightingColor) return;
            if (ReadLightingLocked(CancellationToken.None) is null || appliedLighting is null) return;
            lightingController.WriteColorAsync(color, CancellationToken.None).GetAwaiter().GetResult();
            lastLightingColor = color;
            lightingFailures = 0;
        }
        catch (Exception error)
        {
            if (++lightingFailures < 3) return;
            StopLightingOwnershipLocked(error.Message);
            logger?.LogError(error, "Keyboard lighting effect stopped after three failed writes");
        }
        finally { Monitor.Exit(gate); }
    }

    private CommandResult RestoreLightingPreviewLocked(Guid operationId, CancellationToken token)
    {
        if (lightingPreview.Original is not { } original)
            return new(operationId, CommandState.Applied, null, RequiredUserAction.None, null, false);
        if (appliedLighting is not null) ReadLightingLocked(token);
        if (lightingOwnershipLost)
            return new(operationId, CommandState.RecoveryRequired, null, RequiredUserAction.None,
                ServiceError.Create(ErrorCode.ConflictDetected, operationId, false), false)
                { Mutation = HardwareMutation.NoMutation };
        var result = ExecuteLightingLocked(new(operationId, original), token);
        if (result.State != CommandState.Applied)
        {
            // Preserve the snapshot and retry slowly; never discard a failed recovery.
            lightingTimer ??= new Timer(OnLightingFrame, null, Timeout.Infinite, Timeout.Infinite);
            lightingTimer.Change(1000, 1000);
        }
        return result;
    }

    private void StopLightingOwnershipLocked(string reason)
    {
        lightingError = $"keyboardEffectStopped: {reason}";
        lightingOwnershipLost = true;
        appliedLighting = null;
        // Retain preview recovery evidence, but expiry/Dispose must not overwrite the external state.
        lightingTimer?.Change(Timeout.Infinite, Timeout.Infinite);
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
