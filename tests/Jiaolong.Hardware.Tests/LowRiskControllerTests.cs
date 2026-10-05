using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong.Hardware.Mechrevo.Controls;

namespace Jiaolong.Hardware.Tests;

[TestClass]
[TestCategory("HardwareWrite")]
public sealed class LowRiskControllerTests
{
    [TestMethod]
    public async Task Mux_success_requires_readback_and_returns_restart_required()
    {
        var result = await new GpuController(new RecordingMuxTransport(MuxMode.Discrete))
            .ApplyMuxAsync(MuxMode.Discrete, CancellationToken.None);

        Assert.AreEqual(CommandState.Applied, result.State);
        Assert.AreEqual(RequiredUserAction.Restart, result.RequiredAction);
        Assert.IsNull(result.Error);
        Assert.AreEqual(MuxMode.Discrete, result.VerifiedState?.MuxMode);
    }
}

internal sealed class RecordingMuxTransport(MuxMode readBack) : IMuxTransport
{
    public bool WriteCalled { get; private set; }

    public Task<MuxMode> ReadMuxAsync(CancellationToken cancellationToken) => Task.FromResult(readBack);

    public Task WriteMuxAsync(MuxMode mode, CancellationToken cancellationToken)
    {
        WriteCalled = true;
        return Task.CompletedTask;
    }
}
