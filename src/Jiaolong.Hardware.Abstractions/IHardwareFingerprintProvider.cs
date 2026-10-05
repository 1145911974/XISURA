using Jiaolong.Hardware.Abstractions.Models;

namespace Jiaolong.Hardware.Abstractions;

public interface IHardwareFingerprintProvider
{
    Task<HardwareFingerprint> GetFingerprintAsync(CancellationToken cancellationToken);
}
