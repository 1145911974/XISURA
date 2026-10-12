using Jiaolong.Contracts.Models;

namespace Jiaolong.Contracts.Commands;

public sealed record SetAdaptiveAutomationConfigurationCommand(Guid OperationId, AdaptiveAutomationConfiguration Configuration) : HardwareCommand(OperationId)
{
    public long ExpectedRevision { get; init; }
    public AutomationConfigurationIntent Intent { get; init; } = AutomationConfigurationIntent.Update;
}

public sealed record UpdateAdaptiveAutomationContextCommand(Guid OperationId, AdaptiveAutomationClientContext Context) : HardwareCommand(OperationId);
