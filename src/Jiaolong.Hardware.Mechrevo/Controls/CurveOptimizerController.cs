using Jiaolong.Contracts.Errors;
using Jiaolong.Hardware.Mechrevo.Safety;

namespace Jiaolong.Hardware.Mechrevo.Controls;

public interface ICurveOptimizerTransport
{
    Task<int> ReadAsync(CancellationToken cancellationToken);
    Task WriteAsync(int value, CancellationToken cancellationToken);
    Task RestoreAsync(int value, CancellationToken cancellationToken);
}

public interface IPerCoreCurveOptimizerTransport : ICurveOptimizerTransport
{
    IReadOnlyDictionary<int, int> ReadPerCore(CancellationToken cancellationToken);
    void WritePerCore(IReadOnlyDictionary<int, int> values, CancellationToken cancellationToken);
    void RestorePerCore(IReadOnlyDictionary<int, int> values, CancellationToken cancellationToken);
}

public sealed record CurveOptimizerResult(bool Applied, int? VerifiedValue, ErrorCode? Error);

public sealed class CurveOptimizerController(ICurveOptimizerTransport? transport = null)
{
    public static ValidationResult Validate(int value, int minimum, int maximum)
    {
        if (minimum > maximum || maximum > 0 || minimum < -30)
        {
            return ValidationResult.Reject("curveRangeInvalid");
        }

        return value is >= -30 and <= 0 && value >= minimum && value <= maximum
            ? ValidationResult.Allow()
            : ValidationResult.Reject("curveValueInvalid");
    }

    public async Task<CurveOptimizerResult> ApplyAsync(
        int value,
        CancellationToken cancellationToken)
    {
        var validation = Validate(value, -30, 0);
        if (!validation.IsValid || transport is null)
        {
            return new CurveOptimizerResult(false, null,
                transport is null ? ErrorCode.CapabilityUnavailable : ErrorCode.ValidationFailed);
        }

        try
        {
            var before = await transport.ReadAsync(cancellationToken);
            await transport.WriteAsync(value, cancellationToken);
            var readBack = await transport.ReadAsync(cancellationToken);
            if (readBack != value)
            {
                try
                {
                    await transport.RestoreAsync(before, cancellationToken);
                }
                catch
                {
                    return new CurveOptimizerResult(false, readBack, ErrorCode.RollbackFailed);
                }

                return new CurveOptimizerResult(false, readBack, ErrorCode.ReadBackMismatch);
            }

            return new CurveOptimizerResult(true, readBack, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new CurveOptimizerResult(false, null, ErrorCode.HardwareWriteFailed);
        }
    }
}
