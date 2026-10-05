using System.Reflection;
using Jiaolong.Contracts.Models;
using Jiaolong.Service.Home;

namespace Jiaolong.Service.Tests;

[TestClass]
public sealed class FanCeilingTests
{
    [TestMethod]
    public void Actual_ec_targets_follow_the_selected_strategy_and_reject_unsafe_sensors()
    {
        var method = typeof(WindowsHomeHardwareProvider).GetMethod("TryTargets", BindingFlags.NonPublic | BindingFlags.Static)!;
        var telemetry = new HardwareSnapshot(DateTimeOffset.UtcNow, "normal", 60, 50, null, null);
        var plan = new FanControlPlan([new(30, 100), new(100, 100)]) { MaximumRpm = 4200 };
        object?[] args = [plan, telemetry, (byte)0, (byte)0];
        Assert.IsTrue((bool)method.Invoke(null, args)!);
        Assert.AreEqual((byte)42, args[2]); Assert.AreEqual((byte)42, args[3]);
        args = [plan with { Strategy = "Fixed", FixedRpm = 5000, MaximumRpm = 4000 }, telemetry, (byte)0, (byte)0];
        Assert.IsTrue((bool)method.Invoke(null, args)!);
        Assert.AreEqual((byte)50, args[2]); Assert.AreEqual((byte)50, args[3]);
        args = [plan with { Strategy = "Auto", MaximumRpm = 4200 }, telemetry with { CpuFanRpm = 2200, GpuFanRpm = 2200 }, (byte)0, (byte)0];
        Assert.IsTrue((bool)method.Invoke(null, args)!);
        Assert.AreEqual((byte)42, args[2]); Assert.AreEqual((byte)42, args[3]);
        foreach (var unsafeTelemetry in new[]
        {
            telemetry with { CpuTemperatureC = 95 }, telemetry with { GpuTemperatureC = 87 },
            telemetry with { CpuTemperatureC = double.NaN },
            telemetry with { CapturedAtUtc = DateTimeOffset.UtcNow.AddSeconds(10) }
        })
        {
            args = [plan, unsafeTelemetry, (byte)0, (byte)0];
            Assert.IsFalse((bool)method.Invoke(null, args)!);
        }
        args = [plan with { MaximumRpm = null }, telemetry, (byte)0, (byte)0];
        Assert.IsTrue((bool)method.Invoke(null, args)!); Assert.AreEqual((byte)58, args[2]);
        args = [plan, telemetry with { CapturedAtUtc = DateTimeOffset.UtcNow.AddSeconds(-10) }, (byte)0, (byte)0];
        Assert.IsFalse((bool)method.Invoke(null, args)!);
        var strongReadback = typeof(WindowsHomeHardwareProvider).GetMethod("IsStrongCoolingEcConfirmed", BindingFlags.NonPublic | BindingFlags.Static)!;
        var ec = new FanEcControlState(DateTimeOffset.UtcNow, 0x80, 0x0A, 68, 68, null);
        Assert.IsTrue((bool)strongReadback.Invoke(null, [ec])!);
        Assert.IsFalse((bool)strongReadback.Invoke(null, [ec with { CpuTarget = 58 }])!);
        Assert.IsTrue((bool)strongReadback.Invoke(null, [ec with { Control = 4 }])!);
        Assert.IsFalse((bool)strongReadback.Invoke(null, [ec with { GpuTarget = 0 }])!);
        Assert.IsFalse((bool)strongReadback.Invoke(null, [ec with { CapturedAtUtc = DateTimeOffset.UtcNow.AddSeconds(-10) }])!);
    }

    [TestMethod]
    public void Automatic_ceiling_keeps_official_control_below_limit_and_releases_after_cooling()
    {
        var ceiling = new AutomaticFanCeiling();
        var telemetry = new HardwareSnapshot(DateTimeOffset.UtcNow, "normal", 60, 50, null, null)
        { CpuFanRpm = 2200, GpuFanRpm = 2200 };
        Assert.IsFalse(ceiling.Update(4200, telemetry));
        Assert.IsTrue(ceiling.Update(4200, telemetry with { CpuFanRpm = 4500 }));
        Assert.IsTrue(ceiling.Update(4200, telemetry with { CpuTemperatureC = 58 }));
        Assert.IsFalse(ceiling.Update(4200, telemetry with { CpuTemperatureC = 57 }));
        Assert.IsTrue(ceiling.Update(1800, telemetry));
        Assert.IsFalse(ceiling.Update(1800, telemetry with { CpuTemperatureC = 95 }));
        Assert.IsFalse(ceiling.Update(1800, telemetry with { GpuTemperatureC = 87 }));
        Assert.IsFalse(ceiling.Update(1800, telemetry with { CpuFanRpm = null }));
    }
}
