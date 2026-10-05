using Jiaolong.Contracts.Models;
using Jiaolong.Hardware.Mechrevo.Wmi;

namespace Jiaolong.Hardware.Tests;

[TestClass]
public sealed class MiPerformanceModeCodecTests
{
    [TestMethod]
    [DataRow(PerformanceMode.Balanced, 0)]
    [DataRow(PerformanceMode.Turbo, 1)]
    [DataRow(PerformanceMode.Quiet, 2)]
    public void Encodes_the_verified_jiaolong_wire_values(PerformanceMode mode, int wireValue)
    {
        Assert.IsTrue(MiPerformanceModeCodec.TryEncode(mode, out var encoded));
        Assert.AreEqual(wireValue, encoded);
    }

    [TestMethod]
    [DataRow(0, PerformanceMode.Balanced)]
    [DataRow(1, PerformanceMode.Turbo)]
    [DataRow(2, PerformanceMode.Quiet)]
    public void Decodes_the_verified_jiaolong_wire_values(int wireValue, PerformanceMode mode)
    {
        Assert.AreEqual(mode, MiPerformanceModeCodec.Decode((byte)wireValue));
    }

    [TestMethod]
    public void Unknown_or_custom_wire_values_are_not_claimed_as_a_real_mode()
    {
        Assert.IsFalse(MiPerformanceModeCodec.TryEncode(PerformanceMode.Custom, out _));
        Assert.IsNull(MiPerformanceModeCodec.Decode(3));
        Assert.IsNull(MiPerformanceModeCodec.Decode(255));
    }
}
