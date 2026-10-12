using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;

namespace Jiaolong.Service.Home;

public sealed partial class WindowsHomeHardwareProvider
{
    private static readonly TimeSpan GpuVfRefreshInterval = TimeSpan.FromSeconds(2);
    private readonly Func<NvapiVfInterop>? gpuVfFactory;
    private readonly Func<bool>? gpuTuningAcPower;
    private readonly Func<CancellationToken, double?>? gpuTuningTemperature;
    private bool gpuVfRecoveryRequired;
    private int[]? gpuVfRecoveryOffsets;
    private NvapiVfInterop.PstateMemory? gpuPstateRecoverySnapshot;
    private readonly List<NvapiVfInterop> gpuNativeContexts = [];
    private readonly ControlExecutionJournal? controlJournal;

    private void EnsureGpuWriteAccess(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (BackgroundWorkPaused) throw new IOException("hardwareAccessPaused");
    }

    private CommandResult GpuZeroWriteRejected(HardwareCommand command, ErrorCode code, string? reason = null) =>
        Rejected(command.OperationId, code) with
        {
            Mutation = HardwareMutation.NoMutation,
            Error = ServiceError.Create(code, command.OperationId, false) with
            { Details = new() { ["reason"] = reason ?? code.ToString(), ["writeAttempted"] = bool.FalseString } }
        };

    private CommandResult GpuFailure(HardwareCommand command, Exception error, bool writeAttempted)
    {
        if (!writeAttempted)
        {
            gpuVfRecoveryOffsets = null;
            gpuPstateRecoverySnapshot = null;
            return GpuZeroWriteRejected(command,
                error is OperationCanceledException || BackgroundWorkPaused || error.Message == "hardwareAccessPaused"
                    ? ErrorCode.CancelledBeforeApply : ErrorCode.HardwareReadFailed, error.Message);
        }
        gpuVfRecoveryRequired = true;
        gpuMemoryRecoveryRequired = gpuCoreRecoveryRequired = true;
        gpuVfCache = new GpuVfState([], -200_000, 200_000, "gpuPstateRecoveryRequired");
        gpuVfCacheAt = DateTimeOffset.UtcNow;
        return new CommandResult(command.OperationId, CommandState.RecoveryRequired, null, RequiredUserAction.None,
            ServiceError.Create(ErrorCode.RollbackFailed, command.OperationId, false) with
            { Details = new() { ["reason"] = error.Message, ["writeAttempted"] = bool.TrueString } }, false)
        { Mutation = HardwareMutation.Unknown };
    }

    private CommandResult? ValidateGpuTuning(HardwareCommand command, CancellationToken token)
    {
        if (gpuVfRecoveryRequired || gpuMemoryRecoveryRequired || gpuCoreRecoveryRequired)
            return new CommandResult(command.OperationId, CommandState.RecoveryRequired, null, RequiredUserAction.None,
                ServiceError.Create(ErrorCode.RollbackFailed, command.OperationId, false) with
                { Details = new() { ["reason"] = "gpuPendingRecovery", ["writeAttempted"] = bool.FalseString } }, false)
            { Mutation = HardwareMutation.NoMutation };
        try
        {
            EnsureGpuWriteAccess(token);
            if (controlJournal is not null)
                controlJournal.CheckGpuAdmissionAsync(controlJournal.CurrentGpuOperation ??
                    throw new IOException("gpuRecoveryAdmissionMissing"), token).GetAwaiter().GetResult();
            if (!IsVerifiedGpuVfDevice() || CommandValidation.Validate(command) is not null ||
                !ReadProviderHardware(ReadGpuTuningAcPower) ||
                ReadProviderHardware(() => ReadGpuTuningTemperature(token)) is not double temperature || temperature is < 0 or >= 75)
                return GpuZeroWriteRejected(command, ErrorCode.ValidationFailed);
            return null;
        }
        catch (Exception error) { return GpuFailure(command, error, writeAttempted: false); }
    }

    private void ClearGpuRecoverySnapshots()
    {
        gpuVfRecoveryOffsets = null;
        gpuPstateRecoverySnapshot = null;
    }

    private void PersistGpuRecoveryBaseline(NvapiVfInterop nvapi, IntPtr gpu, int[] offsets,
        NvapiVfInterop.PstateMemory pstate, CancellationToken token)
    {
        EnsureGpuWriteAccess(token);
        if (controlJournal is null)
        {
            if (gpuVfFactory is null) throw new IOException("gpuRecoveryJournalUnavailable");
            return; // Fake transports cannot alter the machine; production requires durable capture.
        }
        var operation = controlJournal.CurrentGpuOperation ?? throw new IOException("gpuRecoveryAdmissionMissing");
        GpuAdapterIdentity adapter;
        try { adapter = nvapi.ReadAdapterIdentity(gpu); }
        catch (Exception error) when (error is NvapiCallException or EntryPointNotFoundException)
        { throw new IOException("gpuAdapterIdentityUnavailable", error); }
        var baseline = new GpuRecoveryBaseline(DateTimeOffset.UtcNow, operation.SessionId, operation.HardwareEpoch,
            operation.OwnerSid, identity, adapter, offsets, new(pstate.OffsetKhz, pstate.MinimumKhz, pstate.MaximumKhz,
                pstate.CorePstateOffsetKhz, pstate.CoreVoltageDeltaUv, pstate.StateCount, pstate.ClockCount));
        EnsureGpuWriteAccess(token);
        try { controlJournal.RecordGpuRecoveryAsync(operation.OperationId, baseline, token).GetAwaiter().GetResult(); }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Text.Json.JsonException ||
            error is IOException && error.Message != "gpuPersistedRecoveryRequired")
        { throw new IOException("gpuRecoveryPersistenceFailed", error); }
        EnsureGpuWriteAccess(token);
        if (controlJournal.CurrentGpuOperation != operation) throw new IOException("gpuRecoveryAdmissionRevoked");
    }

    private static CommandResult GpuRolledBack(HardwareCommand command, Exception cause) => new(
        command.OperationId, CommandState.RolledBack, null, RequiredUserAction.None,
        ServiceError.Create(cause is InvalidDataException ? ErrorCode.ReadBackMismatch : ErrorCode.HardwareWriteFailed,
            command.OperationId, false) with
        { Details = new() { ["reason"] = cause.Message, ["writeAttempted"] = bool.TrueString } }, false)
        { Mutation = HardwareMutation.Unknown };

    internal WindowsHomeHardwareProvider(Func<NvapiVfInterop> gpuVfFactory, Func<bool> acPower,
        Func<CancellationToken, double?> temperature, ControlExecutionJournal? journal = null) : this(controlJournal: journal)
    {
        this.gpuVfFactory = gpuVfFactory;
        gpuTuningAcPower = acPower;
        gpuTuningTemperature = temperature;
    }

    private NvapiVfInterop CreateGpuVfInterop()
    {
        if (disposed) throw new ObjectDisposedException(nameof(WindowsHomeHardwareProvider));
        ReleaseGpuNativeContexts();
        if (gpuNativeContexts.Any(context => context.InitializationUncertain))
            throw new IOException("nvapiInitializationRecoveryRequired");
        if (gpuNativeContexts.Any(context => context.DisposalRequested || context.UnloadUncertain || context.LibraryReleaseUncertain))
            throw new IOException("nvapiCleanupRecoveryRequired");
        var context = gpuVfFactory?.Invoke() ?? new NvapiVfInterop(
            hardwareAccessAllowed: () => !BackgroundWorkPaused, hardwareReadAllowed: () => CpuHardwareReadAllowed);
        // Lazy initialization keeps failures owned before any library or driver call.
        gpuNativeContexts.Add(context);
        return context;
    }

    private void ReleaseGpuNativeContexts(bool shutdown = false)
    {
        for (int index = gpuNativeContexts.Count - 1; index >= 0; index--)
        {
            var context = gpuNativeContexts[index];
            if (shutdown) context.RequestDisposal();
            if (context.IsDisposed) { gpuNativeContexts.RemoveAt(index); continue; }
            if (!context.DisposalRequested) continue;
            if (!BackgroundWorkPaused && context.TryDispose()) gpuNativeContexts.RemoveAt(index);
            else TryLogShutdown(() => logger?.LogWarning(context.CleanupError,
                "NVAPI native cleanup pending; context retained (paused={Paused}, initializationUnknown={Unknown}, unloadUnknown={UnloadUnknown}, releaseUnknown={ReleaseUnknown})",
                BackgroundWorkPaused, context.InitializationUncertain, context.UnloadUncertain, context.LibraryReleaseUncertain),
                $"NVAPI native cleanup pending; context retained (paused={BackgroundWorkPaused}, initializationUnknown={context.InitializationUncertain}, unloadUnknown={context.UnloadUncertain}, releaseUnknown={context.LibraryReleaseUncertain}).");
        }
    }

    private bool ReadGpuTuningAcPower() => gpuTuningAcPower?.Invoke() ?? SystemPowerStatusReader.Read().AcPowerConnected is true;
    private double? ReadGpuTuningTemperature(CancellationToken token) =>
        gpuTuningTemperature is not null ? gpuTuningTemperature(token) : ReadTelemetryLocked(token).GpuTemperatureC;

    private bool IsVerifiedGpuVfDevice() =>
        compatibilityDecision?.Capabilities.Items.Any(item => item.Key == "gpuVfCurve" &&
            item.State == CapabilityState.Available) == true;

    private GpuVfState ReadGpuVfLocked(CancellationToken cancellationToken, bool force = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureProviderReadAccess();
        var pendingCleanup = gpuNativeContexts.Where(context => context.DisposalRequested).ToArray();
        if (BackgroundWorkPaused && IsVerifiedGpuVfDevice() && pendingCleanup.Length > 0 &&
            pendingCleanup.All(context => !context.InitializationUncertain && !context.UnloadUncertain &&
                !context.LibraryReleaseUncertain && (context.CleanupError is null ||
                    context.CleanupError is IOException { Message: "hardwareAccessPaused" })))
        {
            // Recovery reads cannot release a paused context or claim that an old sample was refreshed.
            if (!gpuVfRecoveryRequired && !gpuMemoryRecoveryRequired && !gpuCoreRecoveryRequired &&
                gpuVfCache is { Nodes.Length: 127, Error: null } cached)
                return cached with { Source = "suspended-last-known", ObservedAtUtc = cached.ObservedAtUtc ?? gpuVfCacheAt };
            return new GpuVfState([], -200_000, 200_000, "hardwareAccessPaused") { Source = "suspended-unavailable" };
        }
        if (!force && gpuVfCache is not null && DateTimeOffset.UtcNow - gpuVfCacheAt < GpuVfRefreshInterval)
            return gpuVfCache;
        if (!IsVerifiedGpuVfDevice())
            return gpuVfCache = new GpuVfState([], -200_000, 200_000, "gpuVfDeviceNotVerified");

        try
        {
            using var nvapi = CreateGpuVfInterop();
            var (gpu, mask) = nvapi.OpenSingleGpu();
            var curve = nvapi.ReadCurve(gpu, mask);
            if (curve.Length != 127) throw new InvalidDataException($"Unexpected GPU V/F node count {curve.Length}");
            var deltas = nvapi.ReadDeltas(gpu, mask, curve.Length);
            NvapiVfInterop.PstateMemory? memory = null;
            (int MinimumKhz, int MaximumKhz)? coreRange = null;
            try { memory = nvapi.ReadPstate0MemoryOffset(gpu); }
            catch (Exception exception) when (CpuHardwareReadAllowed && exception is not OperationCanceledException)
            { logger?.LogDebug(exception, "GPU memory offset read unavailable"); }
            try { coreRange = nvapi.ReadGraphicsDeltaRange(gpu); }
            catch (Exception exception) when (CpuHardwareReadAllowed && exception is not OperationCanceledException)
            { logger?.LogDebug(exception, "GPU core offset range read unavailable"); }
            EnsureProviderReadAccess();
            gpuVfCache = new GpuVfState(curve.Select((node, index) =>
                new GpuVfNode(node.VoltageMv, node.BaseMhz, deltas[index])).ToArray(),
                -200_000, 200_000, null)
            {
                ObservedAtUtc = DateTimeOffset.UtcNow,
                Source = "nvapi",
                MemoryOffsetKhz = memory?.OffsetKhz,
                MemoryMinimumOffsetKhz = memory?.MinimumKhz,
                MemoryMaximumOffsetKhz = memory?.MaximumKhz,
                CoreOffsetKhz = memory?.CorePstateOffsetKhz,
                CoreMinimumOffsetKhz = coreRange?.MinimumKhz,
                CoreMaximumOffsetKhz = coreRange?.MaximumKhz
            };
        }
        catch (Exception exception) when (CpuHardwareReadAllowed && exception is not OperationCanceledException)
        {
            logger?.LogWarning(exception, "GPU V/F read failed");
            gpuVfCache = new GpuVfState([], -200_000, 200_000, "gpuVfReadFailed");
        }
        gpuVfCacheAt = DateTimeOffset.UtcNow;
        return gpuVfCache;
    }

    private CommandResult ExecuteGpuVfLocked(SetGpuVfCurveCommand command, CancellationToken cancellationToken)
    {
        if (ValidateGpuTuning(command, cancellationToken) is { } rejected) return rejected;
        NvapiVfInterop? transport = null;
        int[]? original = null;

        try
        {
            using var nvapi = transport = CreateGpuVfInterop();
            var (gpu, mask) = nvapi.OpenSingleGpu();
            if (nvapi.ReadCurve(gpu, mask).Length != 127)
                return GpuZeroWriteRejected(command, ErrorCode.HardwareReadFailed);
            var before = nvapi.ReadDeltas(gpu, mask, 127);
            if (!before.SequenceEqual(command.ExpectedOffsetsKhz))
                return GpuZeroWriteRejected(command, ErrorCode.ReadBackMismatch);
            if (before.SequenceEqual(command.OffsetsKhz))
                return new CommandResult(command.OperationId, CommandState.Applied, null, RequiredUserAction.None, null, false)
                { Mutation = HardwareMutation.NoMutation };

            try
            {
                EnsureGpuWriteAccess(cancellationToken);
                var pstateBefore = nvapi.ReadPstate0MemoryOffset(gpu);
                PersistGpuRecoveryBaseline(nvapi, gpu, before, pstateBefore, cancellationToken);
                gpuVfRecoveryOffsets = original = before.ToArray();
                gpuPstateRecoverySnapshot = pstateBefore;
                // A failed NVAPI call may have partially applied the table; always inspect before deciding recovery.
                nvapi.WriteDeltas(gpu, mask, command.OffsetsKhz);
                EnsureGpuWriteAccess(cancellationToken);
                if (!nvapi.ReadDeltas(gpu, mask, 127).SequenceEqual(command.OffsetsKhz))
                    throw new InvalidDataException("GPU V/F readback mismatch");
                gpuVfCacheAt = DateTimeOffset.MinValue;
                ReadGpuVfLocked(CancellationToken.None, force: true);
                EnsureGpuWriteAccess(cancellationToken);
                ClearGpuRecoverySnapshots();
                return new CommandResult(command.OperationId, CommandState.Applied, null, RequiredUserAction.None, null, false)
                { Mutation = HardwareMutation.Changed };
            }
            catch (Exception exception)
            {
                logger?.LogWarning(exception, "GPU V/F apply failed; restoring original table");
                if (!nvapi.WriteAttempted) return GpuFailure(command, exception, false);
                if (BackgroundWorkPaused) return GpuFailure(command, exception, true);
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    try
                    {
                        EnsureGpuWriteAccess(CancellationToken.None);
                        if (!nvapi.ReadDeltas(gpu, mask, 127).SequenceEqual(before))
                            nvapi.WriteDeltas(gpu, mask, before);
                        if (!nvapi.ReadDeltas(gpu, mask, 127).SequenceEqual(before) ||
                            gpuPstateRecoverySnapshot is { } expectedPstate && nvapi.ReadPstate0MemoryOffset(gpu) != expectedPstate) continue;
                        gpuVfCacheAt = DateTimeOffset.MinValue;
                        ReadGpuVfLocked(CancellationToken.None, force: true);
                        EnsureGpuWriteAccess(CancellationToken.None);
                        ClearGpuRecoverySnapshots();
                        return GpuRolledBack(command, exception);
                    }
                    catch (Exception recoveryException)
                    {
                        logger?.LogWarning(recoveryException, "GPU V/F recovery attempt {Attempt} failed", attempt + 1);
                        if (BackgroundWorkPaused) return GpuFailure(command, recoveryException, true);
                    }
                }
                return GpuFailure(command, exception, true);
            }
        }
        catch (Exception exception)
        {
            logger?.LogWarning(exception, "GPU V/F initial read failed");
            if (transport?.WriteAttempted == true && original is not null) gpuVfRecoveryOffsets = original;
            return GpuFailure(command, exception, transport?.WriteAttempted == true);
        }
    }

    private CommandResult ExecuteGpuMemoryOffsetLocked(SetGpuMemoryOffsetCommand command, CancellationToken cancellationToken)
        => ExecuteGpuPstateOffsetLocked(command, command.ExpectedOffsetKhz, command.OffsetKhz, core: false, cancellationToken);

    private CommandResult ExecuteGpuCoreOffsetLocked(SetGpuCoreOffsetCommand command, CancellationToken cancellationToken)
        => ExecuteGpuPstateOffsetLocked(command, command.ExpectedOffsetKhz, command.OffsetKhz, core: true, cancellationToken);

    private CommandResult ExecuteGpuPstateOffsetLocked(HardwareCommand command, int expected, int target,
        bool core, CancellationToken cancellationToken)
    {
        if (ValidateGpuTuning(command, cancellationToken) is { } rejected) return rejected;
        NvapiVfInterop? transport = null;
        int[]? original = null;
        NvapiVfInterop.PstateMemory? originalPstate = null;

        try
        {
            using var nvapi = transport = CreateGpuVfInterop();
            var (gpu, mask) = nvapi.OpenSingleGpu();
            if (nvapi.ReadCurve(gpu, mask).Length != 127)
                return GpuZeroWriteRejected(command, ErrorCode.HardwareReadFailed);
            var before = nvapi.ReadPstate0MemoryOffset(gpu);
            var coreRange = core ? nvapi.ReadGraphicsDeltaRange(gpu) : null;
            int current = core ? before.CorePstateOffsetKhz : before.OffsetKhz;
            int minimum = core ? coreRange?.MinimumKhz ?? int.MaxValue : before.MinimumKhz;
            int maximum = core ? coreRange?.MaximumKhz ?? int.MinValue : before.MaximumKhz;
            if (current != expected)
                return GpuZeroWriteRejected(command, ErrorCode.ReadBackMismatch);
            if (minimum > target || maximum < target || minimum > maximum)
                return GpuZeroWriteRejected(command, ErrorCode.ValidationFailed);
            var originalCurve = nvapi.ReadDeltas(gpu, mask, 127);
            // Validate the table under the same gate as the write, including a no-op core reset.
            if (command is SetGpuCoreOffsetCommand { ExpectedVfOffsetsKhz: { } expectedCurveOffsets } &&
                !originalCurve.SequenceEqual(expectedCurveOffsets))
                return GpuZeroWriteRejected(command, ErrorCode.ReadBackMismatch);
            if (current == target)
                return new CommandResult(command.OperationId, CommandState.Applied, null, RequiredUserAction.None, null, false)
                { Mutation = HardwareMutation.NoMutation };
            var expectedState = core ? before with { CorePstateOffsetKhz = target } : before with { OffsetKhz = target };
            int delta = core ? target - current : 0;
            var expectedCurve = originalCurve.Select((value, index) => index == 0 ? value : checked(value + delta)).ToArray();
            if (expectedCurve.Skip(1).Any(value => value is < -200_000 or > 200_000))
                return GpuZeroWriteRejected(command, ErrorCode.ValidationFailed);

            try
            {
                EnsureGpuWriteAccess(cancellationToken);
                PersistGpuRecoveryBaseline(nvapi, gpu, originalCurve, before, cancellationToken);
                gpuVfRecoveryOffsets = original = originalCurve.ToArray();
                gpuPstateRecoverySnapshot = originalPstate = before;
                nvapi.WritePstate0Offsets(gpu, before,
                    core ? target : before.CorePstateOffsetKhz, core ? before.OffsetKhz : target);
                EnsureGpuWriteAccess(cancellationToken);
                // This driver rebuilds the V/F table on a P0 write, even for memory-only edits.
                // Restore the requested curve before validating the complete transaction.
                if (!nvapi.ReadDeltas(gpu, mask, 127).SequenceEqual(expectedCurve))
                    nvapi.WriteDeltas(gpu, mask, expectedCurve);
                var actual = nvapi.ReadPstate0MemoryOffset(gpu);
                if (actual != expectedState ||
                    !nvapi.ReadDeltas(gpu, mask, 127).SequenceEqual(expectedCurve) ||
                    !ReadGpuTuningAcPower() ||
                    ReadGpuTuningTemperature(CancellationToken.None) is not double currentTemperature ||
                    currentTemperature is < 0 or >= 75)
                    throw new InvalidDataException("GPU P0 offset or adjacent state readback mismatch");
                gpuVfCacheAt = DateTimeOffset.MinValue;
                var refreshed = ReadGpuVfLocked(CancellationToken.None, force: true);
                if ((core ? refreshed.CoreOffsetKhz : refreshed.MemoryOffsetKhz) != target)
                    throw new InvalidDataException("GPU P0 offset state refresh mismatch");
                EnsureGpuWriteAccess(cancellationToken);
                ClearGpuRecoverySnapshots();
                return new CommandResult(command.OperationId, CommandState.Applied, null, RequiredUserAction.None, null, false)
                { Mutation = HardwareMutation.Changed };
            }
            catch (Exception exception)
            {
                logger?.LogWarning(exception, "GPU P0 offset apply failed; restoring original P0 state");
                if (!nvapi.WriteAttempted) return GpuFailure(command, exception, false);
                if (BackgroundWorkPaused) return GpuFailure(command, exception, true);
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    try
                    {
                        EnsureGpuWriteAccess(CancellationToken.None);
                        if (nvapi.ReadPstate0MemoryOffset(gpu) != before)
                            nvapi.WritePstate0Offsets(gpu, before, before.CorePstateOffsetKhz, before.OffsetKhz);
                        if (!nvapi.ReadDeltas(gpu, mask, 127).SequenceEqual(originalCurve))
                            nvapi.WriteDeltas(gpu, mask, originalCurve);
                        if (nvapi.ReadPstate0MemoryOffset(gpu) != before ||
                            !nvapi.ReadDeltas(gpu, mask, 127).SequenceEqual(originalCurve)) continue;
                        gpuVfCacheAt = DateTimeOffset.MinValue;
                        ReadGpuVfLocked(CancellationToken.None, force: true);
                        EnsureGpuWriteAccess(CancellationToken.None);
                        ClearGpuRecoverySnapshots();
                        return GpuRolledBack(command, exception);
                    }
                    catch (Exception recoveryException)
                    {
                        logger?.LogWarning(recoveryException, "GPU P0 offset recovery attempt {Attempt} failed", attempt + 1);
                        if (BackgroundWorkPaused) return GpuFailure(command, recoveryException, true);
                    }
                }
                return GpuFailure(command, exception, true);
            }
        }
        catch (Exception exception)
        {
            logger?.LogWarning(exception, "GPU P0 offset initial read failed");
            if (transport?.WriteAttempted == true)
            {
                gpuVfRecoveryOffsets = original;
                gpuPstateRecoverySnapshot = originalPstate;
            }
            return GpuFailure(command, exception, transport?.WriteAttempted == true);
        }
    }
}
