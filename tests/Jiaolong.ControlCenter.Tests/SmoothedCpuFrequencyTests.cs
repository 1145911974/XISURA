using Jiaolong_ControlCenter.Services;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class SmoothedCpuFrequencyTests
{
    [TestMethod]
    public void Large_change_moves_toward_real_sample_without_a_frame_by_frame_digit_count()
    {
        var display = new SmoothedCpuFrequency();

        Assert.AreEqual(4200d, display.Update(4200d));
        Assert.AreEqual(4500d, display.Update(5000d));
        Assert.AreEqual(4700d, display.Update(5000d));
    }

    [TestMethod]
    public void Small_fluctuation_stays_stable_and_missing_sample_clears_readout()
    {
        var display = new SmoothedCpuFrequency();

        Assert.AreEqual(4200d, display.Update(4200d));
        Assert.AreEqual(4200d, display.Update(4280d));
        Assert.IsNull(display.Update(null));
        Assert.AreEqual(4300d, display.Update(4300d));
    }
}
