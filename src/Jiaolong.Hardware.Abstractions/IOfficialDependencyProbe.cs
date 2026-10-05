using Jiaolong.Hardware.Abstractions.Models;

namespace Jiaolong.Hardware.Abstractions;

public interface IOfficialDependencyProbe
{
    Task<IReadOnlyList<VerifiedDependency>> GetVerifiedDependenciesAsync(CancellationToken cancellationToken);
}
