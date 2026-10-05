using Jiaolong.Hardware.Abstractions.Models;

namespace Jiaolong.Hardware.Abstractions;

public interface IHardwareControlRestorer
{
    Task RestoreControlAsync(ControlSnapshot snapshot, CancellationToken cancellationToken);
}
