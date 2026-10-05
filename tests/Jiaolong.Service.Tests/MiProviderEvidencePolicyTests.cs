using Jiaolong.Service.Home;

namespace Jiaolong.Service.Tests;

[TestClass]
public sealed class MiProviderEvidencePolicyTests
{
    [TestMethod]
    public void Allows_controlled_write_attempt_when_the_exact_MI_contract_is_present_but_instance_readback_is_denied()
    {
        Assert.IsTrue(MiProviderEvidencePolicy.CanAttemptControlledWrite(
            instanceReadSucceeded: false,
            classContractAvailable: true));
    }

    [TestMethod]
    public void Rejects_controlled_write_attempt_when_the_MI_contract_cannot_be_discovered()
    {
        Assert.IsFalse(MiProviderEvidencePolicy.CanAttemptControlledWrite(
            instanceReadSucceeded: false,
            classContractAvailable: false));
    }
}
