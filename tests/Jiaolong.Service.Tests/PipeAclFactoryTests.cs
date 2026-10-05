using Jiaolong.Service.Ipc;

namespace Jiaolong.Service.Tests;

[TestClass]
public sealed class PipeAclFactoryTests
{
    [TestMethod]
    public void Pipe_dacl_denies_network_and_anonymous_and_allows_system_admin_interactive()
    {
        var aces = PipeAclFactory.Create();
        var normalized = aces.Select(ace => $"{ace.Identity}:{ace.Access}:{ace.Allow}").ToArray();

        CollectionAssert.AreEquivalent(
            new[]
            {
                "SYSTEM:FullControl:True",
                "BUILTIN\\Administrators:ReadWriteSynchronizeCreateNewInstance:True",
                "INTERACTIVE:ReadWriteSynchronizeCreateNewInstance:True",
                "ANONYMOUS LOGON:FullControl:False",
                "NETWORK:FullControl:False"
            },
            normalized);
        Assert.IsFalse(normalized.Any(value => value.StartsWith("Everyone:", StringComparison.Ordinal)));
    }
}
