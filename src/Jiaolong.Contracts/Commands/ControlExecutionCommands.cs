using Jiaolong.Contracts.Models;

namespace Jiaolong.Contracts.Commands;

public sealed record PublishPresetCatalogCommand(Guid OperationId, long ExpectedCatalogRevision,
    PagePresetEnvelope[] Items) : HardwareCommand(OperationId)
{
    public PageSelection[] Selections { get; init; } = [];
}

public sealed record ApplyControlRequestCommand(Guid OperationId, ControlRequest Request) : HardwareCommand(OperationId);

public enum TurboBranchTier { Quiet, Extreme }
public sealed record ApplyTurboBranchCommand(Guid OperationId, TurboBranchTier Tier) : HardwareCommand(OperationId);
public sealed record ReleaseTurboBranchCommand(Guid OperationId, Guid BranchOperationId) : HardwareCommand(OperationId);
