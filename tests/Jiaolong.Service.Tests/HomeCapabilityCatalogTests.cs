using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong.Service.Home;

namespace Jiaolong.Service.Tests;

[TestClass]
public sealed class HomeCapabilityCatalogTests
{
    [TestMethod]
    public void Mux_command_requires_the_mux_mode_capability()
    {
        var command = new SetMuxModeCommand(Guid.NewGuid(), MuxMode.Discrete, UserConfirmedRestartImpact: true);

        Assert.AreEqual("muxMode", HomeCapabilityCatalog.RequiredCapability(command));
    }

    [TestMethod]
    public void Gpu_vf_command_requires_its_own_verified_capability()
    {
        var command = new SetGpuVfCurveCommand(Guid.NewGuid(), new int[127], new int[127], true);
        Assert.AreEqual("gpuVfCurve", HomeCapabilityCatalog.RequiredCapability(command));
    }
}
