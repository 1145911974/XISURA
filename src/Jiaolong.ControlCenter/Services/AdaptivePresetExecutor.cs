using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;

namespace Jiaolong_ControlCenter.Services;

public sealed record AdaptivePresetApplyResult(CommandResult Command, string? PartialReason = null)
{
    public bool IsPartial => !string.IsNullOrWhiteSpace(PartialReason);
}

public sealed class AdaptivePresetExecutor
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(10);
    private readonly Func<PresetKey, CancellationToken, Task<AdaptivePresetApplyResult>> applyAsync;
    private readonly TimeProvider timeProvider;
    private readonly object gate = new();
    private PresetKey? lastRequested;
    private DateTimeOffset? retryAtUtc;
    private bool isApplying;

    public AdaptivePresetExecutor(
        Func<PresetKey, CancellationToken, Task<AdaptivePresetApplyResult>> applyAsync,
        TimeProvider? timeProvider = null)
    {
        this.applyAsync = applyAsync ?? throw new ArgumentNullException(nameof(applyAsync));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public bool IsApplying
    {
        get { lock (gate) return isApplying; }
    }

    public void Reset()
    {
        lock (gate)
        {
            if (isApplying) return;
            lastRequested = null;
            retryAtUtc = null;
        }
    }

    public bool TryStartApply(PresetKey target, CancellationToken cancellationToken, out Task<AdaptivePresetApplyResult?> completion)
    {
        lock (gate)
        {
            if (isApplying || lastRequested == target &&
                (retryAtUtc is null || timeProvider.GetUtcNow() < retryAtUtc.Value))
            {
                completion = Task.FromResult<AdaptivePresetApplyResult?>(null);
                return false;
            }
            isApplying = true;
            lastRequested = target;
            retryAtUtc = null;
        }

        completion = ApplyCoreAsync(target, cancellationToken);
        return true;
    }

    private async Task<AdaptivePresetApplyResult?> ApplyCoreAsync(PresetKey target, CancellationToken cancellationToken)
    {
        try
        {
            var result = await applyAsync(target, cancellationToken).ConfigureAwait(false);
            lock (gate)
            {
                isApplying = false;
                if (result.Command is not { State: CommandState.Applied, Error: null })
                    retryAtUtc = timeProvider.GetUtcNow() + RetryDelay;
            }

            return result;
        }
        catch
        {
            lock (gate)
            {
                isApplying = false;
                retryAtUtc = timeProvider.GetUtcNow() + RetryDelay;
            }

            throw;
        }
    }
}
