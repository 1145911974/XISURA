using Jiaolong.Service.Ipc;

namespace Jiaolong.Service.Tests;

[TestClass]
public sealed class ClientIdentityVerifierTests
{
    [TestMethod]
    public void Identity_policy_allows_only_local_interactive_or_administrator()
    {
        Assert.IsTrue(ClientIdentityVerifier.IsAuthorized(ClientIdentity.LocalInteractive("S-1-5-21-1-2-3-4", 1)));
        Assert.IsTrue(ClientIdentityVerifier.IsAuthorized(ClientIdentity.LocalAdministrator("S-1-5-21-1-2-3-4", 1)));
        Assert.IsFalse(ClientIdentityVerifier.IsAuthorized(ClientIdentity.Network("S-1-5-21-1-2-3-4")));
        Assert.IsFalse(ClientIdentityVerifier.IsAuthorized(ClientIdentity.Anonymous()));
        Assert.IsFalse(ClientIdentityVerifier.IsAuthorized(ClientIdentity.NonInteractive("S-1-5-21-1-2-3-4")));
    }
}
