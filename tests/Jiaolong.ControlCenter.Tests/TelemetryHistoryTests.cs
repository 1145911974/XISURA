using Jiaolong_ControlCenter.ViewModels;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class TelemetryHistoryTests
{
    [TestMethod]
    public void History_keeps_exactly_sixty_one_hertz_samples()
    {
        var history = new TelemetryHistory(capacity: 60);
        foreach (var sample in Enumerable.Range(0, 75)) history.Add(HomeFixture.Sample(sample));

        Assert.AreEqual(60, history.Count);
        Assert.AreEqual(15, history[0].Sequence);
        Assert.AreEqual(74, history[^1].Sequence);
    }
}
