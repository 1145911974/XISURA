using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;
using Jiaolong.Service.Commands;
using Jiaolong.Service.Ipc;

namespace Jiaolong.IntegrationTests;

[TestClass]
public sealed class SecurityBoundaryTests
{
    [TestMethod]
    public void Protocol_acl_unauthorized_and_network_clients_are_rejected()
    {
        Assert.IsFalse(ClientIdentityVerifier.IsAuthorized(ClientIdentity.Network("S-1-5-21")));
        Assert.IsFalse(ClientIdentityVerifier.IsAuthorized(ClientIdentity.Anonymous()));
        Assert.IsTrue(ClientIdentityVerifier.IsAuthorized(ClientIdentity.LocalInteractive("S-1-5-18", 1)));

        var command = new SetPerformanceModeCommand(Guid.NewGuid(), PerformanceMode.Balanced);
        var unauthorized = CommandPolicy.Validate(command,
            new CommandContext(false, true, true, true, true, DateTimeOffset.UtcNow.AddMinutes(1), Guid.NewGuid()),
            DateTimeOffset.UtcNow);

        Assert.AreEqual(ErrorCode.UnauthorizedClient, unauthorized!.Code);

        var securityModel = File.ReadAllText(Path.Combine(FindRoot(), "docs", "engineering", "security-model.md"));
        StringAssert.Contains(securityModel, "Reachability review");
        StringAssert.Contains(securityModel, "No TCP listeners");
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Jiaolong.ControlCenter.slnx"))) return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
