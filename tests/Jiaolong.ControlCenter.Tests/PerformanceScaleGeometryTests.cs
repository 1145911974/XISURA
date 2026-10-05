using System.Reflection;
using Jiaolong_ControlCenter.ViewModels;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class PerformanceScaleGeometryTests
{
    [TestMethod]
    public void Recommendation_marker_uses_transform_animation_and_respects_system_motion_setting()
    {
        string rail = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "Jiaolong.ControlCenter", "Controls", "PerformanceTuningRailV2.xaml.cs"));
        StringAssert.Contains(rail, "SetRecommendedValueAnimatedAsync");
        StringAssert.Contains(rail, "RecommendedMarker.RenderTransform");
        StringAssert.Contains(rail, "UISettings().AnimationsEnabled");
        StringAssert.Contains(rail, "ControlStateTransitionDuration");
    }

    [TestMethod]
    [DataRow(0d, 10d)]
    [DataRow(50d, 150d)]
    [DataRow(100d, 290d)]
    [DataRow(-20d, 10d)]
    [DataRow(120d, 290d)]
    public void Project_ClampsValueAndKeepsThumbInsideRail(double value, double expected)
    {
        Type? geometry = typeof(PerformanceViewModel).Assembly.GetType(
            "Jiaolong_ControlCenter.Controls.PerformanceScaleGeometry");

        Assert.IsNotNull(geometry);

        MethodInfo? project = geometry.GetMethod(
            "Project",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

        Assert.IsNotNull(project);

        double actual = (double)project.Invoke(null, [value, 0d, 100d, 300d, 20d])!;
        Assert.AreEqual(expected, actual, 0.000001d);
    }

    [TestMethod]
    public void Limit_label_moves_right_before_current_label_loses_its_thumb_anchor()
    {
        Type? geometry = typeof(PerformanceViewModel).Assembly.GetType(
            "Jiaolong_ControlCenter.Controls.PerformanceRailLabelGeometry");
        Assert.IsNotNull(geometry);

        MethodInfo? resolveLimit = geometry.GetMethod(
            "ResolveLimitLeft",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.IsNotNull(resolveLimit);

        double limitLeft = (double)resolveLimit.Invoke(null, [244d, 36d, 189d, 54d, 8d, 292d])!;
        Assert.AreEqual(251d, limitLeft, 0.000001d);

        MethodInfo? resolveCurrent = geometry.GetMethod(
            "ResolveCurrentLeft",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.IsNotNull(resolveCurrent);

        double currentLeft = (double)resolveCurrent.Invoke(null, [216d, 54d, limitLeft, 8d, 292d])!;
        Assert.AreEqual(189d, currentLeft, 0.000001d);
    }

    [TestMethod]
    public void Frequency_display_converts_ghz_without_changing_the_mhz_contract()
    {
        Type? display = typeof(PerformanceViewModel).Assembly.GetType(
            "Jiaolong_ControlCenter.Controls.PerformanceDisplayValue");
        Assert.IsNotNull(display);
        MethodInfo? toDisplay = display.GetMethod(
            "ToDisplay", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        MethodInfo? toRaw = display.GetMethod(
            "ToRaw", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.IsNotNull(toDisplay);
        Assert.IsNotNull(toRaw);

        Assert.AreEqual(4.7d, (double)toDisplay.Invoke(null, [4700d, 0.001d])!, 0.000001d);
        Assert.AreEqual(4700d, (double)toRaw.Invoke(null, [4.7d, 0.001d])!, 0.000001d);
    }

    [TestMethod]
    public void Cpu_arc_uses_value_to_real_limit_ratio()
    {
        Type? geometry = typeof(PerformanceViewModel).Assembly.GetType(
            "Jiaolong_ControlCenter.Controls.CpuOperatingBoundaryGeometry");
        Assert.IsNotNull(geometry);
        MethodInfo? progress = geometry.GetMethod(
            "ResolveProgress", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.IsNotNull(progress);

        double actual = (double)progress.Invoke(null, [68d, 95d])!;
        Assert.AreEqual(68d / 95d, actual, 0.000001d);
    }

    [TestMethod]
    public void Cpu_arc_is_unavailable_when_value_or_limit_is_missing()
    {
        Type? geometry = typeof(PerformanceViewModel).Assembly.GetType(
            "Jiaolong_ControlCenter.Controls.CpuOperatingBoundaryGeometry");
        Assert.IsNotNull(geometry);
        MethodInfo? progress = geometry.GetMethod(
            "ResolveProgress", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.IsNotNull(progress);

        Assert.IsNull(progress.Invoke(null, [null, 95d]));
        Assert.IsNull(progress.Invoke(null, [68d, null]));
    }

    [TestMethod]
    public void Cpu_boundary_uses_machine_fallback_limits_only_when_hardware_limits_are_missing()
    {
        Type? fallbacks = typeof(PerformanceViewModel).Assembly.GetType(
            "Jiaolong_ControlCenter.Controls.CpuOperatingBoundaryFallbacks");
        Assert.IsNotNull(fallbacks);
        MethodInfo? temperature = fallbacks.GetMethod(
            "ResolveTemperatureLimit", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        MethodInfo? power = fallbacks.GetMethod(
            "ResolvePowerLimit", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.IsNotNull(temperature);
        Assert.IsNotNull(power);

        Assert.AreEqual(100d, temperature.Invoke(null, [null]));
        Assert.AreEqual(75d, power.Invoke(null, [null]));
        Assert.AreEqual(93d, temperature.Invoke(null, [93d]));
        Assert.AreEqual(110d, power.Invoke(null, [110d]));
        MethodInfo? voltage = fallbacks.GetMethod(
            "ResolveVoltageLimit", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.IsNotNull(voltage);
        Assert.AreEqual(2d, voltage.Invoke(null, [null]));
        Assert.AreEqual(1.2d, voltage.Invoke(null, [1.2d]));

        Type? labels = typeof(PerformanceViewModel).Assembly.GetType(
            "Jiaolong_ControlCenter.Controls.CpuOperatingBoundaryText");
        Assert.IsNotNull(labels);
        MethodInfo? temperatureLabel = labels.GetMethod(
            "FormatTemperatureLimit", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        MethodInfo? powerLabel = labels.GetMethod(
            "FormatPowerLimit", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.IsNotNull(temperatureLabel);
        Assert.IsNotNull(powerLabel);
        Assert.AreEqual("参考上限 100°C", temperatureLabel.Invoke(null, [null]));
        Assert.AreEqual("上限 93°C", temperatureLabel.Invoke(null, [93d]));
        Assert.AreEqual("参考上限 75 W", powerLabel.Invoke(null, [null]));
        Assert.AreEqual("上限 70 W", powerLabel.Invoke(null, [70d]));
    }

    [TestMethod]
    [DataRow("normal", true)]
    [DataRow("connected", true)]
    [DataRow("unknown", false)]
    public void Cpu_boundary_maps_real_service_states_to_live_status(string state, bool expected)
    {
        Type? boundary = typeof(PerformanceViewModel).Assembly.GetType(
            "Jiaolong_ControlCenter.Controls.CpuOperatingBoundaryV2");
        Assert.IsNotNull(boundary);
        MethodInfo? isLive = boundary.GetMethod(
            "IsLiveState", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.IsNotNull(isLive);
        Assert.AreEqual(expected, isLive.Invoke(null, [state]));
    }

    [TestMethod]
    public void Cpu_voltage_limit_distinguishes_manual_automatic_and_unavailable_states()
    {
        Type? text = typeof(PerformanceViewModel).Assembly.GetType(
            "Jiaolong_ControlCenter.Controls.CpuOperatingBoundaryText");
        Assert.IsNotNull(text);
        MethodInfo? format = text.GetMethod(
            "FormatVoltageLimit", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.IsNotNull(format);

        Assert.AreEqual("上限 1200 mV", format.Invoke(null, [1.2d, false]));
        Assert.AreEqual("上限 2000 mV", format.Invoke(null, [2d, true]));
        Assert.AreEqual("刻度 2000 mV · 自动", format.Invoke(null, [null, true]));
        Assert.AreEqual("刻度 2000 mV", format.Invoke(null, [null, false]));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Jiaolong.ControlCenter.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("无法定位蛟龙仓库根目录。");
    }
}
