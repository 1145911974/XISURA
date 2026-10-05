using Jiaolong_ControlCenter.Prototype;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class ThermalVisualStateTests
{
    [TestMethod]
    public void Missing_or_invalid_temperature_uses_unknown_minimum_state()
    {
        foreach (var value in new double?[] { null, double.NaN, double.PositiveInfinity })
        {
            var state = ThermalVisualState.Resolve(ThermalDeviceKind.Cpu, value);

            Assert.IsFalse(state.IsAvailable);
            Assert.AreEqual("-- °C", state.DisplayText);
            Assert.AreEqual(0d, state.Heat);
            Assert.IsTrue(state.PrimaryOpacity > 0d);
            Assert.AreEqual(0d, state.SecondaryOpacity);
            Assert.AreEqual(0d, state.TertiaryOpacity);
        }
    }

    [TestMethod]
    public void Cpu_temperature_increases_all_blue_light_layers_continuously()
    {
        var cold = ThermalVisualState.Resolve(ThermalDeviceKind.Cpu, 30);
        var warm = ThermalVisualState.Resolve(ThermalDeviceKind.Cpu, 62.5);
        var hot = ThermalVisualState.Resolve(ThermalDeviceKind.Cpu, 95);

        Assert.IsTrue(cold.IsAvailable);
        Assert.AreEqual("63 °C", warm.DisplayText);
        Assert.IsTrue(cold.PrimaryOpacity < warm.PrimaryOpacity && warm.PrimaryOpacity < hot.PrimaryOpacity);
        Assert.IsTrue(cold.SecondaryOpacity < warm.SecondaryOpacity && warm.SecondaryOpacity < hot.SecondaryOpacity);
        Assert.IsTrue(cold.TertiaryOpacity < warm.TertiaryOpacity && warm.TertiaryOpacity < hot.TertiaryOpacity);
        Assert.IsTrue(cold.Scale < warm.Scale && warm.Scale < hot.Scale);
    }

    [TestMethod]
    public void Gpu_temperature_expands_heat_layers_from_core_outward()
    {
        var cold = ThermalVisualState.Resolve(ThermalDeviceKind.Gpu, 30);
        var warm = ThermalVisualState.Resolve(ThermalDeviceKind.Gpu, 60);
        var hot = ThermalVisualState.Resolve(ThermalDeviceKind.Gpu, 90);

        Assert.IsTrue(cold.PrimaryOpacity > 0d);
        Assert.AreEqual(0d, cold.SecondaryOpacity);
        Assert.IsTrue(warm.SecondaryOpacity > cold.SecondaryOpacity);
        Assert.IsTrue(hot.SecondaryOpacity > warm.SecondaryOpacity);
        Assert.IsTrue(hot.TertiaryOpacity > warm.TertiaryOpacity);
        Assert.IsTrue(cold.Scale < warm.Scale && warm.Scale < hot.Scale);
    }

    [TestMethod]
    public void Device_temperature_ranges_are_clamped()
    {
        Assert.AreEqual(0d, ThermalVisualState.Resolve(ThermalDeviceKind.Cpu, -20).Heat);
        Assert.AreEqual(1d, ThermalVisualState.Resolve(ThermalDeviceKind.Cpu, 140).Heat);
        Assert.AreEqual(0d, ThermalVisualState.Resolve(ThermalDeviceKind.Gpu, -20).Heat);
        Assert.AreEqual(1d, ThermalVisualState.Resolve(ThermalDeviceKind.Gpu, 140).Heat);
    }
}
