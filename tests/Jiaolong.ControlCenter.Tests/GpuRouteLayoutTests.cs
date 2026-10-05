using System.Reflection;
using Jiaolong_ControlCenter.ViewModels;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class GpuRouteLayoutTests
{
    [TestMethod]
    public void One_to_four_displays_are_centered_with_room_for_labels()
    {
        var type = typeof(PerformanceViewModel).Assembly.GetType("Jiaolong_ControlCenter.Prototype.Controls.GpuRouteDiagram")!;
        var positions = type.GetMethod("EndpointPositions", BindingFlags.NonPublic | BindingFlags.Static)!;
        for (int count = 1; count <= 4; count++)
        {
            var values = (double[])positions.Invoke(null, [count])!;
            Assert.AreEqual(count, values.Length);
            if (count == 2) CollectionAssert.AreEqual(new[] { 36.5, 94.5 }, values);
            Assert.AreEqual(65.5, values.Average(), 0.001);
            for (int i = 0; i < count; i++)
            {
                Assert.AreEqual(131, values[i] + values[count - 1 - i], 0.001);
                Assert.IsTrue(values[i] >= 13 && values[i] <= 118);
                if (i > 0) Assert.IsTrue(values[i] - values[i - 1] >= 26);
            }
        }
    }
}
