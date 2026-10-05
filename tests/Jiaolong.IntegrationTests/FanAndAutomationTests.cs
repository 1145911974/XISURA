using Jiaolong.Automation;
using Jiaolong.Contracts.Models;
using Jiaolong.Hardware.Mechrevo.Controls;

namespace Jiaolong.IntegrationTests;

[TestClass]
public sealed class FanAndAutomationTests
{
    [TestMethod]
    public async Task SimulatorLifecycleReleaseReturnsToEcAutomatic()
    {
        var transport = new InMemoryFanTransport();
        var controller = new FanController(transport);

        var result = await controller.ReleaseAsync(ReleaseReason.SystemSuspend, CancellationToken.None);

        Assert.AreEqual(FanOwner.EcAutomatic, result.FinalOwner);
        Assert.AreEqual(ReleaseReason.SystemSuspend, result.ReleaseReason);
    }

    private sealed class InMemoryFanTransport : IFanTransport
    {
        private FanOwner owner = FanOwner.ApplicationCurve;
        public Task ApplyCurveAsync(FanControlPlan plan, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<FanOwner> ReadOwnerAsync(CancellationToken cancellationToken) => Task.FromResult(owner);
        public Task ReleaseAsync(ReleaseReason reason, CancellationToken cancellationToken)
        {
            owner = FanOwner.EcAutomatic;
            return Task.CompletedTask;
        }
    }
}
