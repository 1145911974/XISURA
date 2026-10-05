using Jiaolong.Hardware.Mechrevo.Controls;

namespace Jiaolong.Hardware.Tests;

[TestClass]
public sealed class NvidiaClockLimitTests
{
    [TestMethod]
    public void Clock_commands_target_exact_gpu_preserve_idle_and_reset_explicitly()
    {
        var gpu = new NvidiaClockDevice("GPU-603acbd9-d976-5cbc-9f78-1516fd0e4dc8", 210, 3105);
        CollectionAssert.AreEqual(new[] { "-i", gpu.Uuid, "-lgc", "0,2400" }, NvidiaClockLimitTransport.BuildArguments(gpu, 2400));
        CollectionAssert.AreEqual(new[] { "-i", gpu.Uuid, "-rgc" }, NvidiaClockLimitTransport.BuildArguments(gpu, null));
        var unreadable = NvidiaClockLimitTransport.ParsePowerRange("5.00, 140.00, 80.00, [N/A]");
        Assert.AreEqual(140, unreadable.MaximumWatts);
        Assert.IsNull(unreadable.CurrentWatts);
        Assert.AreEqual(125d, NvidiaClockLimitTransport.ParsePowerRange("5, 140, 80, 125").CurrentWatts);
        foreach (var invalid in new[] { "5, 140, 80", "5, 140, 80, NaN", "5, 140, 80, 141" })
            Assert.Throws<IOException>(() => NvidiaClockLimitTransport.ParsePowerRange(invalid));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => NvidiaClockLimitTransport.BuildArguments(gpu, 3106));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => NvidiaClockLimitTransport.BuildArguments(gpu, 209));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => NvidiaClockLimitTransport.BuildArguments(gpu with { Uuid = "0 -pl 200" }, 2400));
    }
}
