using Jiaolong.Contracts.Models;
using Jiaolong.Hardware.Abstractions.Models;

namespace Jiaolong.Hardware.Abstractions;

public interface IHardwareAdapter
{
    Task<HardwareSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken);

    Task<ControlSnapshot> ReadControlAsync(ControlKey key, CancellationToken cancellationToken);

    Task WriteAsync(ValidatedHardwareWrite write, CancellationToken cancellationToken);

    Task ReleaseFanControlAsync(ReleaseReason reason, CancellationToken cancellationToken);
}
