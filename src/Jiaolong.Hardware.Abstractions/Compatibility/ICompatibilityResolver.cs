using Jiaolong.Hardware.Abstractions.Models;

namespace Jiaolong.Hardware.Abstractions.Compatibility;

public interface ICompatibilityResolver
{
    Task<CompatibilityDecision> ResolveAsync(
        HardwareFingerprint fingerprint,
        CancellationToken cancellationToken);
}
