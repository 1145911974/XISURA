namespace Jiaolong.Service.Home;

public static class MiProviderEvidencePolicy
{
    public static bool CanAttemptControlledWrite(
        bool instanceReadSucceeded,
        bool classContractAvailable) => instanceReadSucceeded || classContractAvailable;
}
