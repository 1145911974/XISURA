using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;
using Jiaolong.Hardware.Abstractions;
using Jiaolong.Hardware.Abstractions.Models;

namespace Jiaolong.Service.Commands;

public interface ICommandOrchestrator
{
    Task<CommandResult> ExecuteAsync(HardwareCommand command, CommandContext context, CancellationToken cancellationToken);
}

public sealed class CommandOrchestrator(
    IHardwareAdapter adapter,
    ICommandJournal journal,
    CircuitBreaker circuitBreaker,
    Jiaolong.Service.Storage.LastKnownGoodStore lastKnownGood,
    TimeProvider? timeProvider = null) : ICommandOrchestrator
{
    private const string WriterMutexName = "Global\\JiaolongControlCenter.HardwareWriter";
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task<CommandResult> ExecuteAsync(
        HardwareCommand command,
        CommandContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        var requestHash = ComputeRequestHash(command);
        var claim = await journal.ClaimAsync(command.OperationId, requestHash, cancellationToken);
        if (claim.IsConflict)
        {
            return Rejected(command.OperationId, ErrorCode.IdempotencyConflict, context.CorrelationId);
        }

        if (claim.IsInProgress)
        {
            return Rejected(command.OperationId, ErrorCode.CommandInProgress, context.CorrelationId);
        }

        if (claim.IsReplay)
        {
            return claim.Result! with { IsReplay = true };
        }

        var policyError = CommandPolicy.Validate(command, context, clock.GetUtcNow());
        if (policyError is not null)
        {
            return await CompleteAsync(Rejected(command.OperationId, policyError.Code, context.CorrelationId), CancellationToken.None);
        }

        if (!circuitBreaker.CanWrite)
        {
            return await CompleteAsync(Rejected(command.OperationId, ErrorCode.CircuitOpen, context.CorrelationId), CancellationToken.None);
        }

        if (!TryCreateWrite(command, out var write))
        {
            return await CompleteAsync(Rejected(command.OperationId, ErrorCode.CapabilityUnavailable, context.CorrelationId), CancellationToken.None);
        }

        using var mutex = CreateWriterMutex();
        try
        {
            if (!mutex.WaitOne(0))
            {
                return await CompleteAsync(Rejected(command.OperationId, ErrorCode.CommandInProgress, context.CorrelationId), CancellationToken.None);
            }
        }
        catch (AbandonedMutexException)
        {
            // The abandoned writer is treated as acquired; the saved snapshot is still mandatory.
        }
        catch (UnauthorizedAccessException)
        {
            return await CompleteAsync(Rejected(command.OperationId, ErrorCode.ServiceUnavailable, context.CorrelationId), CancellationToken.None);
        }

        try
        {
            ControlSnapshot before;
            try
            {
                before = await adapter.ReadControlAsync(write.Key, cancellationToken);
            }
            catch
            {
                return await CompleteAsync(Rejected(command.OperationId, ErrorCode.HardwareReadFailed, context.CorrelationId), CancellationToken.None);
            }
            if (!before.IsAvailable)
            {
                return await CompleteAsync(Rejected(command.OperationId, ErrorCode.HardwareReadFailed, context.CorrelationId), CancellationToken.None);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var applyingToken = CancellationToken.None;
            try
            {
                await adapter.WriteAsync(write, applyingToken);
                var after = await adapter.ReadControlAsync(write.Key, applyingToken);
                if (!Matches(after, write))
                {
                    var restored = await RestoreAndVerifyAsync(before, applyingToken);
                    return await CompleteAsync(
                        restored
                            ? RolledBack(command.OperationId, ErrorCode.ReadBackMismatch, context.CorrelationId)
                            : Recovery(command.OperationId, context.CorrelationId),
                        CancellationToken.None);
                }

                var applied = new CommandResult(
                    command.OperationId,
                    CommandState.Applied,
                    await adapter.ReadSnapshotAsync(applyingToken),
                    RequiredUserAction.None,
                    null,
                    false);
                await lastKnownGood.UpdateAsync(applied, applyingToken);
                circuitBreaker.RecordSuccess();
                return await CompleteAsync(applied, CancellationToken.None);
            }
            catch (HardwareOperationException exception)
            {
                if (!exception.RollbackSucceeded)
                {
                    circuitBreaker.Open();
                    return await CompleteAsync(Recovery(command.OperationId, context.CorrelationId), CancellationToken.None);
                }

                var restored = await RestoreAndVerifyAsync(before, applyingToken);
                if (!restored)
                {
                    circuitBreaker.Open();
                    return await CompleteAsync(Recovery(command.OperationId, context.CorrelationId), CancellationToken.None);
                }

                var code = string.Equals(exception.Outcome, "readbackMismatch", StringComparison.Ordinal)
                    ? ErrorCode.ReadBackMismatch
                    : ErrorCode.HardwareWriteFailed;
                return await CompleteAsync(RolledBack(command.OperationId, code, context.CorrelationId), CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                circuitBreaker.Open();
                return await CompleteAsync(Recovery(command.OperationId, context.CorrelationId), CancellationToken.None);
            }
            catch
            {
                circuitBreaker.RecordCommunicationFailure();
                var restored = await RestoreAndVerifyAsync(before, applyingToken);
                if (!restored) circuitBreaker.Open();
                return await CompleteAsync(Recovery(command.OperationId, context.CorrelationId), CancellationToken.None);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return await CompleteAsync(Rejected(command.OperationId, ErrorCode.CancelledBeforeApply, context.CorrelationId), CancellationToken.None);
        }
        finally
        {
            try { mutex.ReleaseMutex(); } catch (ApplicationException) { }
        }
    }

    private async Task<CommandResult> CompleteAsync(CommandResult result, CancellationToken cancellationToken)
    {
        await journal.CompleteAsync(result.OperationId, result, cancellationToken);
        return result;
    }

    private async Task<bool> RestoreAndVerifyAsync(ControlSnapshot before, CancellationToken cancellationToken)
    {
        if (adapter is not IHardwareControlRestorer restorer) return false;
        try
        {
            await restorer.RestoreControlAsync(before, cancellationToken);
            var restored = await adapter.ReadControlAsync(before.Key, cancellationToken);
            return Matches(restored, before);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryCreateWrite(HardwareCommand command, out ValidatedHardwareWrite write)
    {
        switch (command)
        {
            case SetCpuTuningCommand cpu:
                var cpuValue = cpu.Plan.SplWatts ?? cpu.Plan.SpptWatts ?? cpu.Plan.TemperatureLimitC ?? cpu.Plan.MaxFrequencyMhz ?? cpu.Plan.EnabledCoreCount;
                if (cpuValue is null) break;
                write = new ValidatedHardwareWrite(command.OperationId, new ControlKey(ControlKeys.CpuTuning), cpuValue, null, null);
                return true;
            case SetGpuFrequencyLimitCommand gpu when gpu.Megahertz is not null:
                write = new ValidatedHardwareWrite(command.OperationId, new ControlKey(ControlKeys.GpuFrequencyLimit), gpu.Megahertz, null, null);
                return true;
        }

        write = new ValidatedHardwareWrite(Guid.Empty, default, null, null, string.Empty);
        return false;
    }

    private static bool Matches(ControlSnapshot actual, ValidatedHardwareWrite expected)
    {
        if (!actual.IsAvailable) return false;
        if (expected.NumericValue is { } number) return actual.NumericValue is { } actualNumber && Math.Abs(actualNumber - number) <= 0.01;
        if (expected.BooleanValue is { } boolean) return actual.BooleanValue == boolean;
        return string.Equals(actual.TextValue, expected.TextValue, StringComparison.Ordinal);
    }

    private static bool Matches(ControlSnapshot actual, ControlSnapshot expected) =>
        actual.IsAvailable && expected.IsAvailable &&
        (actual.NumericValue is { } actualNumber && expected.NumericValue is { } expectedNumber
            ? Math.Abs(actualNumber - expectedNumber) <= 0.01
            : actual.BooleanValue == expected.BooleanValue && string.Equals(actual.TextValue, expected.TextValue, StringComparison.Ordinal));

    private static string ComputeRequestHash(HardwareCommand command)
    {
        var json = JsonSerializer.Serialize(command, command.GetType());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    private static Mutex CreateWriterMutex() => new(false, WriterMutexName);

    private static CommandResult Rejected(Guid operationId, ErrorCode code, Guid correlationId) =>
        new(operationId, CommandState.Rejected, null, RequiredUserAction.None, ServiceError.Create(code, correlationId, code is ErrorCode.CircuitOpen or ErrorCode.CommandInProgress), false);

    private static CommandResult RolledBack(Guid operationId, ErrorCode code, Guid correlationId) =>
        new(operationId, CommandState.RolledBack, null, RequiredUserAction.None, ServiceError.Create(code, correlationId, true), false);

    private static CommandResult Recovery(Guid operationId, Guid correlationId) =>
        new(operationId, CommandState.RecoveryRequired, null, RequiredUserAction.None, ServiceError.Create(ErrorCode.RollbackFailed, correlationId, false), false);
}
