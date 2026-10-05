using Jiaolong.Contracts.Commands;
using Jiaolong.Hardware.Mechrevo.Controls;

namespace Jiaolong.Hardware.Tests;

[TestClass]
[TestCategory("HardwareWrite")]
public sealed class WindowsPowerControllerTests
{
    [TestMethod]
    public async Task Boost_enable_is_rejected_on_dc_without_transport_call()
    {
        var transport = new RecordingPowerTransport();
        var result = await new WindowsPowerController(transport).ApplyAsync(
            Guid.NewGuid(), boostEnabled: true, isAcConnected: false, CancellationToken.None);

        Assert.AreEqual(CommandState.Rejected, result.State);
        Assert.AreEqual(0, transport.WriteCount);
    }

    private sealed class RecordingPowerTransport : IWindowsPowerTransport
    {
        public int WriteCount { get; private set; }
        public Task<WindowsPowerState> ReadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new WindowsPowerState(Guid.Empty, false));
        public Task WriteAsync(Guid schemeId, bool boostEnabled, CancellationToken cancellationToken)
        {
            WriteCount++;
            return Task.CompletedTask;
        }
    }
}
