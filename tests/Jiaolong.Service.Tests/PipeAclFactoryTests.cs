using System.Security.AccessControl;
using System.Security.Principal;
using Jiaolong.Service.Ipc;

namespace Jiaolong.Service.Tests;

[TestClass]
public sealed class PipeAclFactoryTests
{
    [TestMethod]
    public void Pipe_dacl_denies_network_and_anonymous_and_allows_only_system_and_administrators()
    {
        var aces = PipeAclFactory.Create();
        var normalized = aces.Select(ace => $"{ace.Identity}:{ace.Access}:{ace.Allow}").ToArray();

        CollectionAssert.AreEquivalent(
            new[]
            {
                "SYSTEM:FullControl:True",
                "BUILTIN\\Administrators:ReadWriteSynchronizeCreateNewInstance:True",
                "ANONYMOUS LOGON:FullControl:False",
                "NETWORK:FullControl:False"
            },
            normalized);
        Assert.IsFalse(normalized.Any(value => value.StartsWith("Everyone:", StringComparison.Ordinal)));
        var security = PipeAclFactory.CreateSecurityDescriptor();
        var allowedSids = security.GetAccessRules(true, false, typeof(SecurityIdentifier))
            .Cast<AccessRule>().Where(rule => rule.AccessControlType == AccessControlType.Allow)
            .Select(rule => rule.IdentityReference.Value).ToArray();
        CollectionAssert.AreEquivalent(new[] { "S-1-5-18", "S-1-5-32-544" }, allowedSids);
    }
}
