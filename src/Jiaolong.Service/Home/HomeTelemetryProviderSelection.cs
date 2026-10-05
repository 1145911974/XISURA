using Jiaolong.Hardware.Abstractions.Compatibility;

namespace Jiaolong.Service.Home;

public static class HomeTelemetryProviderSelection
{
    public static WmiProviderEvidence? ForRead(
        WmiProviderEvidence? verifiedProvider,
        WmiProviderEvidence? observedProvider) => verifiedProvider ?? observedProvider;
}
