using System;
using System.IO;
using System.Linq;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class ControlPageDesignContractTests
{
    [TestMethod]
    public void Performance_gpu_fan_and_lighting_pages_expose_separate_save_and_use_actions()
    {
        var toolbar = ReadSource("src", "Jiaolong.ControlCenter", "Controls", "PagePresetToolbar.xaml");
        var pages = new[]
        {
            ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "PerformanceWorkspaceV2.xaml"),
            ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "GpuWorkspaceV2.xaml"),
            ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "FanWorkspaceV2.xaml"),
            ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "LightingWorkspaceV2.xaml")
        };

        foreach (var page in pages)
            StringAssert.Contains(page, "shared:PagePresetToolbar");
        AssertSaveAndUseActions(toolbar, "共享预设工具栏");
    }

    [TestMethod]
    public void Fan_page_keeps_mechanical_parameters_above_node_table_and_supports_ten_nodes()
    {
        var fan = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "FanWorkspaceV2.xaml");
        var curve = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "FanCurveWorkspaceV2.xaml");
        var code = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "FanCurveWorkspaceV2.xaml.cs");

        StringAssert.Contains(fan, "FanCurveWorkspaceV2");
        StringAssert.Contains(fan, "控制策略");
        StringAssert.Contains(curve, "x:Name=\"AddNodeButton\"");
        StringAssert.Contains(curve, "x:Name=\"DeleteButton\"");
        StringAssert.Contains(curve, "x:Name=\"TemperatureBox\"");
        StringAssert.Contains(curve, "x:Name=\"TargetBox\"");
        StringAssert.Contains(code, "至少两个节点");
    }

    [TestMethod]
    public void Auto_page_hosts_the_new_strategy_workspace_without_the_old_fake_pipeline()
    {
        var workspace = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "PrototypePageWorkspace.xaml");
        var automation = Section(workspace, "AutomationPage", "SettingsPage");
        var newPage = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "AutomationWorkspaceV2.xaml");

        StringAssert.Contains(automation, "AutomationWorkspaceV2");
        StringAssert.Contains(newPage, "当前判定");
        StringAssert.Contains(newPage, "调度策略");
        Assert.IsFalse(automation.Contains("决策引擎实时在线", StringComparison.Ordinal));
        Assert.IsFalse(automation.Contains("固件回读一致通过", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Lighting_page_does_not_offer_unverified_spatial_or_per_key_effects_as_available_controls()
    {
        var workspace = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "PrototypePageWorkspace.xaml");
        var lighting = Section(workspace, "LightingPage", "AutomationPage");

        foreach (var forbiddenTag in new[] { "PerKey", "Zone", "WaveScan", "Scan", "Ripple", "Snake", "ChassisStrip", "AmbientLight" })
        {
            var tagIndex = lighting.IndexOf($"Tag=\"{forbiddenTag}\"", StringComparison.Ordinal);
            if (tagIndex < 0) continue;

            var controlEnd = lighting.IndexOf("/>", tagIndex, StringComparison.Ordinal);
            if (controlEnd < 0) controlEnd = lighting.Length;
            var control = lighting[tagIndex..controlEnd];
            Assert.IsTrue(control.Contains("IsEnabled=\"False\"", StringComparison.Ordinal), $"灯光能力 {forbiddenTag} 不能作为可用控件。");
        }

        Assert.IsFalse(lighting.Contains("单键设置", StringComparison.Ordinal));
        Assert.IsFalse(lighting.Contains("分区控制", StringComparison.Ordinal));
        Assert.IsFalse(lighting.Contains("机身灯带", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Advanced_cpu_tuning_is_one_expanded_surface_without_pseudo_tab_selection()
    {
        var workspace = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "PerformanceWorkspace.xaml");
        var advanced = workspace[workspace.IndexOf("x:Name=\"AdvancedCpuTuningPanel\"", StringComparison.Ordinal)..];

        StringAssert.Contains(advanced, "x:Name=\"AdvancedSmuPanel\"");
        StringAssert.Contains(advanced, "x:Name=\"AdvancedPboPanel\"");
        StringAssert.Contains(advanced, "逐核心调节");
        Assert.IsFalse(advanced.Contains("<TabView", StringComparison.Ordinal));
        Assert.IsFalse(advanced.Contains("SelectedItem=", StringComparison.Ordinal));
        Assert.IsFalse(advanced.Contains("AdvancedSmuTab", StringComparison.Ordinal));
        Assert.IsFalse(advanced.Contains("AdvancedPboTab", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Home_adaptive_mode_is_a_local_toggle_and_does_not_navigate()
    {
        var markup = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "HomeHero.xaml");
        var code = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "HomeHero.xaml.cs");

        StringAssert.Contains(markup, "AutomationProperties.Name=\"自适应模式\"");
        StringAssert.Contains(code, "AdaptiveModeChanged?.Invoke(enabled)");
        var handlerStart = code.IndexOf("private void OnAutomaticModeChanged", StringComparison.Ordinal);
        var handlerEnd = code.IndexOf("\n    }", handlerStart, StringComparison.Ordinal);
        var handler = code[handlerStart..handlerEnd];
        Assert.IsFalse(handler.Contains("Navigate", StringComparison.Ordinal));
        Assert.IsFalse(handler.Contains("ShowPage", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Rough_page_telemetry_does_not_use_design_fallback_values_as_live_data()
    {
        var code = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "PrototypePageWorkspace.xaml.cs");

        foreach (var fallback in new[] { "?? 72.0", "?? 56.0", "?? 2730.0", "?? 6.2", "?? 12.0", "?? 168", "?? 1520" })
            Assert.IsFalse(code.Contains(fallback, StringComparison.Ordinal), $"发现伪实时回退值：{fallback}");
    }

    private static void AssertSaveAndUseActions(string source, string pageName)
    {
        Assert.IsTrue(source.Contains("Text=\"保存当前\"", StringComparison.Ordinal), $"{pageName}缺少独立保存预设动作。");
        Assert.IsTrue(source.Contains("Text=\"保存并应用\"", StringComparison.Ordinal), $"{pageName}缺少独立使用预设动作。");
    }

    private static string NextPage(string page)
    {
        return page switch
        {
            "GpuPage" => "FanPage",
            "FanPage" => "LightingPage",
            "LightingPage" => "AutomationPage",
            _ => throw new ArgumentOutOfRangeException(nameof(page), page, null)
        };
    }

    private static string Section(string source, string name, string nextName)
    {
        var start = source.IndexOf($"x:Name=\"{name}\"", StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, start, $"找不到页面 {name}。");
        var end = source.IndexOf($"x:Name=\"{nextName}\"", start + 1, StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, end, $"找不到页面边界 {nextName}。");
        return source[start..end];
    }

    private static string ReadSource(params string[] parts)
    {
        var root = FindRepositoryRoot();
        return File.ReadAllText(Path.Combine(new[] { root }.Concat(parts).ToArray()));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Jiaolong.ControlCenter.slnx")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new DirectoryNotFoundException("无法定位蛟龙仓库根目录。");
    }
}
