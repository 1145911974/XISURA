using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;

namespace Jiaolong.Hardware.Mechrevo.Controls;

public sealed record LightingState(string Effect, int Brightness, byte Red, byte Green, byte Blue, bool LogoEnabled);

public sealed record ValidatedLightingPlan(string Effect, int Brightness, byte Red, byte Green, byte Blue, bool? LogoEnabled)
{
    public bool IsValid => !string.IsNullOrWhiteSpace(Effect) && Brightness is >= 0 and <= 100;
}

public interface ILightingController
{
    Task<LightingState> ReadAsync(CancellationToken cancellationToken);
    Task ApplyAsync(ValidatedLightingPlan plan, CancellationToken cancellationToken);
}

public interface ILightingTransport
{
    Task<LightingState> ReadAsync(CancellationToken cancellationToken);
    Task ApplyAsync(ValidatedLightingPlan plan, CancellationToken cancellationToken);
    Task RestoreAsync(LightingState state, CancellationToken cancellationToken);
}

public sealed class LightingController(ILightingTransport? transport = null) : ILightingController
{
    public Task<LightingState> ReadAsync(CancellationToken cancellationToken) =>
        transport?.ReadAsync(cancellationToken)
        ?? Task.FromException<LightingState>(new InvalidOperationException("lightingUnavailable"));

    public async Task ApplyAsync(ValidatedLightingPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.IsValid) throw new ArgumentException("Lighting plan is invalid.", nameof(plan));
        if (transport is null) throw new InvalidOperationException("lightingUnavailable");

        var before = await transport.ReadAsync(cancellationToken);
        try
        {
            await transport.ApplyAsync(plan, cancellationToken);
            _ = await transport.ReadAsync(cancellationToken);
        }
        catch
        {
            try { await transport.RestoreAsync(before, cancellationToken); }
            catch (Exception restoreException) { throw new InvalidOperationException(ErrorCode.RollbackFailed.ToWireValue(), restoreException); }
            throw;
        }
    }
}
