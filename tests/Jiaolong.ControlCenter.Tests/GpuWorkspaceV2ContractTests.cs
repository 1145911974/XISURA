using System.IO;
using System.Reflection;
using System.Xml.Linq;
using Jiaolong.Contracts.Models;
using Jiaolong.Contracts.Commands;
using Jiaolong_ControlCenter.Services;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class GpuWorkspaceV2ContractTests
{
    private static readonly XName XamlName = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml") + "Name";

    [TestMethod]
    public void Legacy_gpu_presets_drop_removed_fields_and_keep_supported_settings()
    {
        var workspace = Assembly.Load("Jiaolong.ControlCenter").GetType("Jiaolong_ControlCenter.Prototype.Controls.GpuWorkspaceV2", true)!;
        var model = workspace.GetNestedType("GpuWorkspacePreset", BindingFlags.NonPublic)!;
        var validate = workspace.GetMethod("ValidPreset", BindingFlags.NonPublic | BindingFlags.Static)!;
        const string legacy = "{\"CoreFrequencyLimitMhz\":2280,\"MemoryOffsetKhz\":1000,\"CoreOffsetKhz\":-1000,\"PowerPolicyMilliPercent\":80000,\"MuxMode\":\"discrete\",\"VoltageBoostPercent\":4294967295,\"DbUnlocked\":true}";
        var draft = System.Text.Json.JsonSerializer.Deserialize(legacy, model)!;
        Assert.IsTrue((bool)validate.Invoke(null, [draft])!);
        var saved = System.Text.Json.JsonSerializer.SerializeToElement(draft, model);
        Assert.AreEqual(2280, saved.GetProperty("CoreFrequencyLimitMhz").GetInt32());
        Assert.AreEqual(-1000, saved.GetProperty("CoreOffsetKhz").GetInt32());
        Assert.AreEqual("discrete", saved.GetProperty("MuxMode").GetString());
        Assert.IsFalse(saved.TryGetProperty("VoltageBoostPercent", out _));
        Assert.IsFalse(saved.TryGetProperty("DbUnlocked", out _));
        Assert.IsFalse(saved.TryGetProperty("PowerLimitWatts", out _));
        Assert.IsFalse(saved.TryGetProperty("PowerPolicyMilliPercent", out _));
        foreach (var json in new[] { "{\"VoltageBoostPercent\":20}", "{\"DbUnlocked\":true}" })
            Assert.IsFalse((bool)validate.Invoke(null, [System.Text.Json.JsonSerializer.Deserialize(json, model)])!);
    }

    [TestMethod]
    public void Approved_gpu_workspace_keeps_e2_structure_and_only_verified_output_modes()
    {
        var markup = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "GpuWorkspaceV2.xaml")
            + ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "GpuRouteDiagram.xaml")
            + ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "GpuVoltageFrequencyCurve.xaml");

        foreach (var required in new[] { "显卡", "混合输出", "独显直连", "频率调节", "核心偏移", "最近 60 秒", "实时工作点", "电压 / 频率曲线" })
            StringAssert.Contains(markup, required);
        foreach (var deferred in new[] { "动态功耗策略", "应用与回读", "安全保护" })
            Assert.IsFalse(markup.Contains(deferred, StringComparison.Ordinal));
        Assert.IsFalse(markup.Contains("集显输出", StringComparison.Ordinal));
        Assert.IsFalse(markup.Contains("智能切换", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Gpu_workspace_uses_real_telemetry_and_verified_commands_without_design_fallbacks()
    {
        var markup = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "GpuWorkspaceV2.xaml");
        var code = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "GpuWorkspaceV2.xaml.cs");

        foreach (var forbidden in new[] { "?? 72.0", "?? 56.0", "?? 2730.0", "?? 6.2", "?? 12.0", "?? 168", "?? 1520", "参数已应用" })
            Assert.IsFalse(code.Contains(forbidden, StringComparison.Ordinal), $"发现伪造数据或成功状态：{forbidden}");
        var presets = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "GpuWorkspaceV2.Presets.cs");
        var sharedCurve = ReadSource("src", "Jiaolong.ControlCenter", "Controls", "TelemetryCurve.xaml.cs");
        StringAssert.Contains(presets, "SetMuxModeCommand");
        StringAssert.Contains(presets, "session?.State");
        StringAssert.Contains(sharedCurve, "CompositionTarget.Rendering");
        StringAssert.Contains(markup, "<shared:TelemetryCurve");
        Assert.IsFalse(code.Contains("当前模式：等待回读", StringComparison.Ordinal));
        foreach (var removed in new[] { "GpuIdentityText", "ConnectionDot", "ConnectionText", "DriverVersionText", "MaxPowerText", "MemoryCapacityText" })
        {
            Assert.IsFalse(markup.Contains(removed, StringComparison.Ordinal));
            Assert.IsFalse(code.Contains(removed, StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public void Gpu_workspace_reports_active_windows_output_routes_without_claiming_mux_state()
    {
        var markup = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "GpuWorkspaceV2.xaml");
        var code = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "GpuWorkspaceV2.xaml.cs")
            + ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "GpuWorkspaceV2.TelemetryMotion.cs");

        StringAssert.Contains(markup, "x:Name=\"ActiveDisplayRouteText\"");
        StringAssert.Contains(code, "RefreshActiveDisplayRoute();");
        StringAssert.Contains(code, "TimeSpan.FromSeconds(1)");
        StringAssert.Contains(code, "ReadStatus(session?.State?.Controls.MuxMode)");

        var reader = ReadSource("src", "Jiaolong.ControlCenter", "Services", "WindowsDisplayRouteReader.cs");
        foreach (var marker in new[] { "GetDisplayConfigBufferSizes", "QueryDisplayConfig", "DisplayConfigGetDeviceInfo", "QueryOnlyActivePaths", "MUX 未读回", "MUX 设置读回" })
            StringAssert.Contains(reader, marker);
        Assert.IsFalse(reader.Contains("SetDisplayConfig", StringComparison.Ordinal));

        var readerType = Assembly.Load("Jiaolong.ControlCenter").GetType("Jiaolong_ControlCenter.Services.WindowsDisplayRouteReader", throwOnError: true)!;
        var status = readerType.GetMethod("ReadStatus", BindingFlags.Static | BindingFlags.NonPublic, null, Type.EmptyTypes, null)!.Invoke(null, null) as string;
        Assert.IsTrue(!string.IsNullOrWhiteSpace(status), "Windows 活动显示路径 API 应返回读回结果或明确的不可读状态。");
        StringAssert.Contains(status, "活动输出：");
        StringAssert.Contains(status, "MUX 未读回");

        var muxRead = readerType.GetMethod("ReadStatus", BindingFlags.Static | BindingFlags.NonPublic, null, [typeof(MuxMode?)], null)!;
        StringAssert.Contains(muxRead.Invoke(null, [MuxMode.Hybrid]) as string, "MUX 设置读回：混合输出");
        StringAssert.Contains(muxRead.Invoke(null, [MuxMode.Discrete]) as string, "MUX 设置读回：独显直连");
    }

    [TestMethod]
    public void Gpu_hardware_editors_are_locked_without_verified_transport_and_range()
    {
        var markup = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "GpuWorkspaceV2.xaml");
        var code = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "GpuWorkspaceV2.xaml.cs");

        StringAssert.Contains(markup, "x:Name=\"GpuCapabilityNoticeText\"");
        var presets = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "GpuWorkspaceV2.Presets.cs");
        foreach (var gate in new[] { "enabled && clockReady && !clockPending", "enabled && memoryReady && !memoryPending", "enabled && coreOffsetReady && !coreOffsetPending" })
            StringAssert.Contains(presets, gate);
        StringAssert.Contains(code, "SetTuningControls(false)");
        StringAssert.Contains(code, "CapabilityAvailable(snapshot, \"gpuFrequencyLimit\")");
        StringAssert.Contains(code, "MaximumMhz: > 0");
        StringAssert.Contains(code, "PresetToolbar.SetActionAvailability(true, false)");
        StringAssert.Contains(code, "CapabilityAvailable(snapshot, \"gpuVfCurve\")");
        StringAssert.Contains(presets, "activeSession?.State is not { } state");
        StringAssert.Contains(presets, "!CapabilityAvailable(state, \"muxMode\")");
        StringAssert.Contains(presets, "clock < range.MinimumMhz || clock > range.MaximumMhz");
    }

    [TestMethod]
    public void Mux_switch_is_independent_of_presets_and_reports_pending_restart()
    {
        var code = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "GpuWorkspaceV2.Presets.cs");
        string directSwitch = code[code.IndexOf("private async void OnMuxModeRequested", StringComparison.Ordinal)..code.IndexOf("private sealed record GpuWorkspacePreset", StringComparison.Ordinal)];
        StringAssert.Contains(directSwitch, "new SetMuxModeCommand(Guid.NewGuid(), mode, true)");
        StringAssert.Contains(directSwitch, "RequiredUserAction.Restart");
        StringAssert.Contains(directSwitch, "当前生效：");
        StringAssert.Contains(directSwitch, "重启电脑后生效");
        Assert.IsFalse(directSwitch.Contains("IsFollowingPreset", StringComparison.Ordinal));
        Assert.IsFalse(directSwitch.Contains("SaveGpuPresetAsync", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("actions.Add((\"输出模式\"", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("MuxMode = muxDraftMode", StringComparison.Ordinal));
        StringAssert.Contains(code, "UpdateGpuActiveBadge(activeSession.State);");
    }

    [TestMethod]
    public void Shell_routes_gpu_to_the_approved_workspace()
    {
        var markup = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "PrototypeWindow.xaml");
        var code = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "PrototypeWindow.xaml.cs");

        StringAssert.Contains(markup, "GpuWorkspaceV2");
        StringAssert.Contains(code, "\"Gpu\" or \"GpuAdvanced\" => [GpuWorkspaceV2]");
        StringAssert.Contains(code, "GpuWorkspaceV2.ApplyTelemetry");
        StringAssert.Contains(code, "GpuWorkspaceV2.ApplyState");
    }

    [TestMethod]
    public void Output_route_uses_standardized_assets_derived_from_the_approved_mockup()
    {
        var route = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "GpuRouteDiagram.xaml");
        var code = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "GpuRouteDiagram.xaml.cs");
        var xaml = XDocument.Parse(route);

        Assert.IsTrue(xaml.Descendants().Any(element => (string?)element.Attribute(XamlName) == "ActiveDisplaysHost"));
        StringAssert.Contains(code, "LaptopNeutralFrame.png");
        StringAssert.Contains(code, "MonitorNeutralFrame.png");
        Assert.IsFalse((route + code).Contains("-v2.png", StringComparison.Ordinal));
        Assert.IsFalse((route + code).Contains("NvidiaEyeOfficial.png", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Gpu_overview_uses_clean_component_assets_and_live_values()
    {
        var markup = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "GpuWorkspaceV2.xaml");

        StringAssert.Contains(markup, "GpuOverviewChip.png");
        foreach (var glyph in new[] { "TemperatureGlyph", "PowerGlyph", "MemoryGlyph" })
            StringAssert.Contains(markup, glyph);
        StringAssert.Contains(markup, "UsageProgressPath");
        foreach (var field in new[] { "TemperatureText", "UsageText", "PowerText", "MemoryText", "MonitorText" })
            StringAssert.Contains(markup, field);
        var overviewMarkup = markup[..markup.IndexOf("x:Name=\"GpuMonitoringWorkspace\"", StringComparison.Ordinal)];
        Assert.IsFalse(overviewMarkup.Contains("OverviewCardSource.png", StringComparison.Ordinal));
        Assert.IsFalse(overviewMarkup.Contains("StrokeDashArray", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Gpu_tuning_cards_use_standard_rounding_shared_value_boxes_and_hover_help()
    {
        var workspace = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "GpuWorkspaceV2.xaml");
        var code = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "GpuWorkspaceV2.xaml.cs");
        var route = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "GpuRouteDiagram.xaml");
        var stepper = ReadSource("src", "Jiaolong.ControlCenter", "Controls", "GpuValueStepper.xaml");
        var stepperCode = ReadSource("src", "Jiaolong.ControlCenter", "Controls", "GpuValueStepper.xaml.cs");

        Assert.AreEqual(3, workspace.Split("shared:GpuValueStepper", StringSplitOptions.None).Length - 1);
        Assert.IsFalse(stepper.Contains("VerticalStepper", StringComparison.Ordinal));
        StringAssert.Contains(stepper, "PointerWheelChanged");
        StringAssert.Contains(stepperCode, "public event EventHandler<double>? ValueChanged;");
        Assert.IsFalse(workspace.Contains("温度上限 (°C)", StringComparison.Ordinal));
        StringAssert.Contains(stepper, "TextAlignment=\"Center\"");
        StringAssert.Contains(stepper, "Padding=\"0,4,0,0\"");
        var xaml = XDocument.Parse(workspace);
        foreach (var name in new[] { "CoreValueBox", "MemoryValueBox", "CoreOffsetValueBox" })
        {
            var valueBox = xaml.Descendants().Single(element => (string?)element.Attribute(XamlName) == name);
            Assert.AreEqual("GpuValueStepper", valueBox.Name.LocalName);
        }
        StringAssert.Contains(workspace, "Text=\"频率调节\"");
        Assert.IsFalse(workspace.Contains("x:Name=\"ThermalRail\"", StringComparison.Ordinal));
        Assert.IsFalse(workspace.Contains("x:Name=\"ThermalLimitValueBox\"", StringComparison.Ordinal));
        Assert.IsFalse(workspace.Contains("<Grid Visibility=\"Collapsed\"><shared:PerformanceTuningRailV2 x:Name=\"PowerRail\"", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("PowerRail.SetValue(powerWatts)", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("ThermalRail", StringComparison.Ordinal));
        StringAssert.Contains(workspace, "shared:ParameterHelpButton");
        StringAssert.Contains(code, "InitializeGpuHelp()");
        Assert.IsFalse(workspace.Contains("CornerRadius=\"8\"", StringComparison.Ordinal));
        var topology = System.Xml.Linq.XDocument.Parse(route).Descendants()
            .Single(element => element.Attributes().Any(attribute => attribute.Name.LocalName == "Name" && attribute.Value == "TopologyHost"));
        Assert.IsNull(topology.Attribute("Background"));
        Assert.IsNull(topology.Attribute("BorderThickness"));
    }

    [TestMethod]
    public void Gpu_power_and_offset_card_preserves_available_controls()
    {
        var xaml = XDocument.Parse(ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "GpuWorkspaceV2.xaml"));
        var card = xaml.Descendants().Single(element => (string?)element.Attribute(XamlName) == "GpuTuningCard");
        foreach (var name in new[] { "CoreRail", "CoreValueBox", "MemoryRail", "MemoryValueBox", "CoreOffsetValueBox", "CoreOffsetHelp" })
            Assert.IsTrue(card.Descendants().Any(element => (string?)element.Attribute(XamlName) == name));
        foreach (var removed in new[] { "VoltageBoostRail", "VoltageBoostValueBox", "DbUnlockToggle", "PowerRail", "PowerLimitValueBox" })
            Assert.IsFalse(xaml.Descendants().Any(element => (string?)element.Attribute(XamlName) == removed));
    }

    [TestMethod]
    public void Removed_gpu_controls_have_no_ui_help_or_preset_execution_path()
    {
        var markup = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "GpuWorkspaceV2.xaml");
        var code = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "GpuWorkspaceV2.xaml.cs")
            + ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "GpuWorkspaceV2.Help.cs")
            + ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "GpuWorkspaceV2.Presets.cs");
        foreach (var removed in new[] { "DynamicBoostCard", "DbUnlockToggle", "DynamicBoostHelp", "VoltageBoostHelp", "VoltageBoostRail", "SetGpuVoltageBoostCommand" })
            Assert.IsFalse((markup + code).Contains(removed, StringComparison.Ordinal), removed);
        StringAssert.Contains(code, "CoreOffsetValueBox.ValueChanged");
    }

    [TestMethod]
    public void Gpu_monitoring_and_tuning_share_one_card_with_standard_dividers()
    {
        var xaml = XDocument.Parse(ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "GpuWorkspaceV2.xaml"));
        var workspace = xaml.Descendants().Single(element => (string?)element.Attribute(XamlName) == "GpuMonitoringWorkspace");
        Assert.AreEqual("Border", workspace.Name.LocalName);
        Assert.AreEqual("512", (string?)workspace.Attribute("Height"));
        Assert.AreEqual("{StaticResource GpuCardStyle}", (string?)workspace.Attribute("Style"));
        Assert.AreEqual(0, workspace.Descendants().Count(element => (string?)element.Attribute("Style") == "{StaticResource GpuCardStyle}"));
        foreach (var name in new[] { "TrendCard", "RuntimeDiagnosticsCard", "GpuTuningCard" })
            Assert.AreEqual("Grid", workspace.Descendants().Single(element => (string?)element.Attribute(XamlName) == name).Name.LocalName);
        foreach (var name in new[] { "MonitoringTuningDivider", "HistoryOperatingDivider" })
        {
            var divider = workspace.Descendants().Single(element => (string?)element.Attribute(XamlName) == name);
            Assert.AreEqual("{StaticResource PrototypeDividerBrush}", (string?)divider.Attribute("Background"));
            Assert.IsTrue((string?)divider.Attribute("Width") == "1" || (string?)divider.Attribute("Height") == "1");
        }
        var card = workspace.Descendants().Single(element => (string?)element.Attribute(XamlName) == "GpuTuningCard");
        foreach (var (name, row) in new[] { ("CoreTuningGroup", "0"), ("CoreOffsetTuningGroup", "2"), ("MemoryTuningGroup", "4") })
        {
            var group = card.Descendants().Single(element => (string?)element.Attribute(XamlName) == name);
            Assert.AreEqual(row, (string?)group.Attribute("Grid.Row"));
            Assert.IsNull(group.Attribute("Width"));
            Assert.AreEqual(1, group.Descendants().Count(element => element.Name.LocalName == "GpuValueStepper"));
            Assert.AreEqual(1, group.Descendants().Count(element => element.Name.LocalName == "PerformanceTuningRailV2"));
        }
    }

    [TestMethod]
    public void Gpu_trend_chart_uses_three_real_series_and_elapsed_time_window()
    {
        var workspace = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "GpuWorkspaceV2.xaml");
        var code = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "GpuWorkspaceV2.xaml.cs");

        var workspaceXaml = XDocument.Parse(workspace);
        var trendCard = workspaceXaml.Descendants().Single(element => (string?)element.Attribute(XamlName) == "TrendCard");
        foreach (var curveName in new[] { "TemperatureTrendCurve", "UsageTrendCurve", "PowerTrendCurve" })
        {
            var curve = trendCard.Descendants().Single(element => (string?)element.Attribute(XamlName) == curveName);
            Assert.AreEqual("TelemetryCurve", curve.Name.LocalName);
        }
        var curveCode = ReadSource("src", "Jiaolong.ControlCenter", "Controls", "TelemetryCurve.xaml.cs");
        var curveMarkup = ReadSource("src", "Jiaolong.ControlCenter", "Controls", "TelemetryCurve.xaml");
        var series = ReadSource("src", "Jiaolong.ControlCenter", "Services", "TelemetryCurveSeries.cs");
        var window = ReadSource("src", "Jiaolong.ControlCenter", "Services", "TelemetryCurveWindow.cs");
        foreach (var label in new[] { "温度 (°C)", "GPU 利用率 (%)", "GPU 功耗 (W)", "60 秒前", "现在" })
            StringAssert.Contains(workspace, label);
        foreach (var telemetry in new[] { "GpuTemperatureC", "GpuUsagePercent", "GpuPowerWatts" })
            StringAssert.Contains(code, telemetry);
        StringAssert.Contains(code, "TemperatureTrendCurve.UpdateSample");
        StringAssert.Contains(code, "UsageTrendCurve.UpdateSample");
        StringAssert.Contains(code, "PowerTrendCurve.UpdateSample");
        StringAssert.Contains(curveCode, "private double windowSeconds = 60");
        StringAssert.Contains(curveCode, "CompositionTarget.Rendering += OnRendering");
        StringAssert.Contains(series, "/ .5");
        StringAssert.Contains(series, "TelemetryCurveWindow.Trim");
        StringAssert.Contains(window, "public static void Trim");
        var curveXaml = XDocument.Parse(curveMarkup);
        var halo = curveXaml.Descendants().Single(element => (string?)element.Attribute(XamlName) == "HeadHalo");
        Assert.AreEqual("14", (string?)halo.Attribute("Width"));
        Assert.AreEqual("14", (string?)halo.Attribute("Height"));
    }

    [TestMethod]
    public void Gpu_runtime_diagnostics_is_replaced_by_live_operating_point()
    {
        var workspace = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "GpuWorkspaceV2.xaml");
        var code = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "GpuWorkspaceV2.xaml.cs");

        StringAssert.Contains(workspace, "x:Name=\"RuntimeDiagnosticsCard\"");
        StringAssert.Contains(workspace, "x:Name=\"OperatingPointHelp\"");
        StringAssert.Contains(workspace, "ParameterName=\"实时工作点\"");
        Assert.IsFalse(workspace.Contains("RuntimeDiagnosticsCard\" Style=\"{StaticResource GpuCardStyle}\" Visibility=\"Collapsed\"", StringComparison.Ordinal));
        foreach (var field in new[] { "实时工作点", "核心电压", "核心频率", "P-State", "近期限制原因", "未读回" })
            StringAssert.Contains(workspace, field);
        StringAssert.Contains(code, "ApplyOperatingPointState(gpuVoltageMv, snapshot.GpuFrequencyMhz");
        Assert.IsFalse(code.Contains("DiagnosticCoreFrequencyText.Text", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Gpu_operating_point_card_is_visual_and_does_not_repeat_overview_telemetry()
    {
        var workspace = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "GpuWorkspaceV2.xaml");
        var code = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "GpuWorkspaceV2.xaml.cs");

        foreach (var marker in new[]
        {
            "Text=\"实时工作点\"",
            "x:Name=\"OperatingFrequencyCurve\"",
            "x:Name=\"OperatingVoltageCurve\"",
            "x:Name=\"GpuCoreVoltageText\"",
            "x:Name=\"GpuCoreFrequencyText\"",
            "x:Name=\"GpuPStateText\"",
            "x:Name=\"PerformanceLimitReasonText\""
        })
            StringAssert.Contains(workspace, marker);

        foreach (var removed in new[]
        {
            "DiagnosticCoreFrequencyText", "DiagnosticMemoryFrequencyText",
            "DiagnosticUsageText", "DiagnosticPowerText", "DiagnosticMemoryText"
        })
            Assert.IsFalse(workspace.Contains(removed, StringComparison.Ordinal));

        StringAssert.Contains(code, "ApplyOperatingPointState(");
        StringAssert.Contains(code, "OperatingFrequencyCurve.UpdateSample");
        StringAssert.Contains(code, "OperatingVoltageCurve.UpdateSample");
        StringAssert.Contains(code, "performanceLimitReason ?? \"驱动未提供限制原因\"");
        Assert.IsFalse(workspace.Contains("V/F 曲线未读回", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("OperatingPointUnavailableText", StringComparison.Ordinal));
        Assert.IsFalse(workspace.Contains("实时温度", StringComparison.Ordinal));
        Assert.IsFalse(workspace.Contains("降频阈值", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Gpu_operating_point_visual_prioritizes_the_graph_over_compact_status_blocks()
    {
        var workspace = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "GpuWorkspaceV2.xaml");
        var code = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "GpuWorkspaceV2.xaml.cs");

        var xaml = XDocument.Parse(workspace);
        var plot = xaml.Descendants().Single(element => (string?)element.Attribute(XamlName) == "OperatingPlot");
        Assert.AreEqual("Canvas", plot.Name.LocalName);
        Assert.AreEqual("570", (string?)plot.Attribute("Width"));
        Assert.AreEqual("164", (string?)plot.Attribute("Height"));
        StringAssert.Contains(workspace, "X1=\"582\" X2=\"582\"");
        StringAssert.Contains(workspace, "x:Name=\"PerformanceLimitStatusCircle\"");
        StringAssert.Contains(workspace, "x:Name=\"PerformanceLimitStatusGlyph\"");
        StringAssert.Contains(workspace, "Style=\"{StaticResource GpuCardStyle}\"");
        StringAssert.Contains(code, "OperatingPointLabel.Visibility");
    }

    [TestMethod]
    public void Gpu_operating_point_plot_matches_the_approved_axis_ticks()
    {
        var workspace = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "GpuWorkspaceV2.xaml");

        var xaml = XDocument.Parse(workspace);
        var curves = xaml.Descendants().Where(element => element.Name.LocalName == "TelemetryCurve")
            .Where(element => element.Attribute(XamlName) is not null)
            .ToDictionary(element => (string)element.Attribute(XamlName)!, element => element);
        Assert.IsTrue(curves.ContainsKey("OperatingFrequencyCurve"));
        Assert.IsTrue(curves.ContainsKey("OperatingVoltageCurve"));
        Assert.AreEqual("24", (string?)curves["OperatingFrequencyCurve"].Attribute("WindowSeconds"));
        Assert.AreEqual("3000", (string?)curves["OperatingFrequencyCurve"].Attribute("MaximumValue"));
        Assert.AreEqual("600", (string?)curves["OperatingVoltageCurve"].Attribute("MinimumValue"));
        Assert.AreEqual("1400", (string?)curves["OperatingVoltageCurve"].Attribute("MaximumValue"));
        foreach (var tick in new[] { "3000", "1500", "1400", "1000", "600" })
            StringAssert.Contains(workspace, $"Text=\"{tick}\"");
    }

    [TestMethod]
    public void Every_gpu_help_button_shares_its_label_vertical_centerline()
    {
        var workspace = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "GpuWorkspaceV2.xaml");

        var xaml = XDocument.Parse(workspace);
        var helpButtons = xaml.Descendants().Where(element => element.Name.LocalName == "ParameterHelpButton").ToArray();
        Assert.AreEqual(4, helpButtons.Length);
        var help = XDocument.Parse(ReadSource("src", "Jiaolong.ControlCenter", "Controls", "ParameterHelpButton.xaml"));
        Assert.AreEqual("Center", (string?)help.Root?.Attribute("VerticalAlignment"));
    }

    [TestMethod]
    public void Gpu_vf_curve_is_directly_stacked_without_an_advanced_disclosure()
    {
        var workspace = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "GpuWorkspaceV2.xaml");
        var code = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "GpuWorkspaceV2.xaml.cs");

        StringAssert.Contains(workspace, "<Grid.RowDefinitions><RowDefinition Height=\"126\"");
        StringAssert.Contains(workspace, "<RowDefinition Height=\"486\"");
        StringAssert.Contains(workspace, "<prototype:GpuVoltageFrequencyCurve x:Name=\"VoltageFrequencyCurve\" Grid.Row=\"3\"");
        foreach (var removed in new[] { "AdvancedHeader", "AdvancedClipHost", "高级控制", "Ryzen SMU", "PBO / 超频", "更多专业设置", "OnAdvancedHeaderClick" })
            Assert.IsFalse(workspace.Contains(removed, StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("OnAdvancedHeaderClick", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("advancedOpen", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Gpu_and_performance_share_toolbar_and_advanced_button_visuals()
    {
        var gpu = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "GpuWorkspaceV2.xaml");
        var performance = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "PerformanceWorkspaceV2.xaml");
        var resources = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "PerformanceV2Resources.xaml");
        var toolbar = ReadSource("src", "Jiaolong.ControlCenter", "Controls", "PagePresetToolbar.xaml");

        Assert.IsFalse(gpu.Contains("x:Key=\"GpuActionStyle\"", StringComparison.Ordinal));
        Assert.IsFalse(performance.Contains("x:Key=\"V2ActionButtonStyle\"", StringComparison.Ordinal));
        Assert.AreEqual(2, toolbar.Split("Style=\"{StaticResource PerformanceV2ActionButtonStyle}\"").Length - 1);
        Assert.IsFalse(gpu.Contains("PerformanceV2AdvancedButtonStyle", StringComparison.Ordinal));
        StringAssert.Contains(performance, "x:Name=\"AdvancedButton\" Grid.Row=\"3\" Style=\"{StaticResource PerformanceV2AdvancedButtonStyle}\"");
        StringAssert.Contains(resources, "VisualState x:Name=\"PointerOver\"");
        StringAssert.Contains(resources, "VisualState x:Name=\"Pressed\"");
        StringAssert.Contains(resources, "GeneratedDuration=\"{ThemeResource ControlStateTransitionDuration}\"");
    }

    [TestMethod]
    public void Gpu_and_performance_render_the_same_preset_toolbar_component()
    {
        var gpu = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "GpuWorkspaceV2.xaml");
        var performance = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "PerformanceWorkspaceV2.xaml");
        var toolbar = ReadSource("src", "Jiaolong.ControlCenter", "Controls", "PagePresetToolbar.xaml");
        var toolbarCode = ReadSource("src", "Jiaolong.ControlCenter", "Controls", "PagePresetToolbar.xaml.cs");

        Assert.AreEqual(1, gpu.Split("shared:PagePresetToolbar").Length - 1);
        Assert.AreEqual(1, performance.Split("shared:PagePresetToolbar").Length - 1);
        Assert.IsFalse(gpu.Contains("有未应用的更改", StringComparison.Ordinal));
        Assert.IsFalse(performance.Contains("PresetStatusHost", StringComparison.Ordinal));
        StringAssert.Contains(toolbar, "x:Name=\"PresetStatusHost\"");
        StringAssert.Contains(toolbarCode, "ShowSavedStatusAsync");
        StringAssert.Contains(toolbarCode, "TimeSpan.FromSeconds(2)");
        StringAssert.Contains(toolbarCode, "TransitionAsync(1, 0)");
        Assert.IsFalse(toolbarCode.Contains("PresetPicker.IsEnabled = followPreset;", StringComparison.Ordinal));
        StringAssert.Contains(toolbarCode, "public bool IsEditingPreset");
        StringAssert.Contains(toolbarCode, "AddAnimation(storyboard, PresetPickerTranslation, \"X\"");
        StringAssert.Contains(toolbarCode, "Duration = TimeSpan.FromMilliseconds(240)");
        StringAssert.Contains(toolbarCode, "CubicEase { EasingMode = EasingMode.EaseOut }");
        StringAssert.Contains(toolbar, "x:Name=\"PresetStatusTranslation\"");
        StringAssert.Contains(toolbar, "x:Name=\"PresetPickerTranslation\" X=\"162\" />");
        StringAssert.Contains(toolbar, "AutomationProperties.Name=\"随模式自动应用本页预设\"");
        StringAssert.Contains(toolbarCode, "StopTransition();");
        StringAssert.Contains(toolbarCode, "AddAnimation(storyboard, PresetPickerTranslation, \"X\"");
        Assert.IsFalse(toolbarCode.Contains("AnimateWidthAsync", StringComparison.Ordinal));
        Assert.IsFalse(toolbarCode.Contains("PresetStatusHost.Width", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Advanced_curve_is_native_dynamic_and_replaces_the_placeholder()
    {
        var workspace = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "GpuWorkspaceV2.xaml");
        var curve = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "GpuVoltageFrequencyCurve.xaml");
        var code = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "GpuVoltageFrequencyCurve.xaml.cs");

        StringAssert.Contains(workspace, "<prototype:GpuVoltageFrequencyCurve");
        Assert.IsFalse(workspace.Contains("协议尚未验证，曲线编辑保持锁定", StringComparison.Ordinal));
        foreach (var marker in new[] { "CurvePath", "ReadbackCurvePath", "LinearAnchorLayer", "BasicModeButton", "LinearModeButton", "ManualModeButton", "LinearOffsetBox", "LinearSelectedText", "TargetAnchor", "TargetVoltageBox", "TargetFrequencyBox", "目标平台", "x:Name=\"RestoreDefaultsButton\"", "恢复默认" })
            StringAssert.Contains(curve, marker);
        Assert.IsFalse((curve + code).Contains("NodeLayer", StringComparison.Ordinal));
        foreach (var removed in new[] { "SafeRegionPath", "ControlledLimitPath", "CreateDemoState", "AddNodeButton", "添加点", "900 mV", "推荐安全区域" })
            Assert.IsFalse((curve + code).Contains(removed, StringComparison.Ordinal));
        foreach (var marker in new[] { "ApplyState(", "MapVoltageToX(", "MapFrequencyToY(", "AnimationsEnabled" })
            StringAssert.Contains(code, marker);
    }

    [TestMethod]
    public async Task Advanced_curve_edits_a_draft_and_has_an_explicit_verified_factory_restore()
    {
        var curve = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "GpuVoltageFrequencyCurve.xaml");
        var code = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "GpuVoltageFrequencyCurve.xaml.cs");

        foreach (var marker in new[] { "AutomationProperties.Name=\"目标电压档位\"", "AutomationProperties.Name=\"目标频率\"", "AutomationProperties.Name=\"选中控制点频率偏移\"", "AutomationProperties.Name=\"手动节点频率偏移\"", "AutomationProperties.Name=\"恢复默认电压频率曲线\"" })
            StringAssert.Contains(curve, marker);
        foreach (var marker in new[] { "CurveChanged", "RestoreDefaultsRequested", "ResetOffsets(", "TryGetTargetRange", "ApplyTarget(", "ApplyLinearDelta(", "ApplyManualOffset(", "referenceNodes", "readbackNodes" })
            StringAssert.Contains(code, marker);
        var presets = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "GpuWorkspaceV2.Presets.cs");
        StringAssert.Contains(presets, "gpuPresetDirty");
        StringAssert.Contains(presets, "selectedGpuPreset");
        StringAssert.Contains(presets, "new SetGpuVfCurveCommand");
        StringAssert.Contains(presets, "latest.Nodes.Select(node => node.OffsetKhz).ToArray(), offsets, true");
        Assert.IsFalse(code.Contains("ApplyRequested", StringComparison.Ordinal));
        Assert.IsFalse(curve.Contains("应用电压频率曲线", StringComparison.Ordinal));
        Assert.IsFalse(curve.Contains("上一个曲线节点", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("with { VoltageMv", StringComparison.Ordinal));
        Assert.IsFalse(curve.Contains("应用成功", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("ApplyFrequencyLimitAsync", StringComparison.Ordinal));
        StringAssert.Contains(code, "CapabilityStatusText.Text = status;");
        StringAssert.Contains(code, "statusOverride = null;");
        for (int scenario = 0; scenario < 6; scenario++)
        {
            int originalCore = scenario == 1 ? 0 : 40_000;
            var original = new GpuVfState(Enumerable.Range(0, 127)
                .Select(index => new GpuVfNode(450 + index * 5, 1500 + index * 5, index == 0 ? 0 : 80_000)).ToArray(),
                -200_000, 200_000, null) { CoreOffsetKhz = originalCore, MemoryOffsetKhz = 25_000 };
            var current = original;
            var commands = new List<HardwareCommand>();
            Task<CommandResult> Execute(HardwareCommand command)
            {
                commands.Add(command);
                var outcome = CommandState.Applied;
                if (command is SetGpuCoreOffsetCommand core)
                {
                    Assert.IsTrue(core.ExpectedOffsetKhz == current.CoreOffsetKhz, "Write must use current hardware readback.");
                    Assert.IsNotNull(core.ExpectedVfOffsetsKhz);
                    if (scenario == 5 && core.OffsetKhz == originalCore)
                        current = current with { Nodes = current.Nodes.Select((node, index) =>
                            index == 0 ? node : node with { OffsetKhz = node.OffsetKhz + 5_000 }).ToArray() };
                    if (!current.Nodes.Select(node => node.OffsetKhz).SequenceEqual(core.ExpectedVfOffsetsKhz) ||
                        scenario == 3 && core.OffsetKhz == originalCore) outcome = CommandState.Rejected;
                    else current = current with
                    {
                        CoreOffsetKhz = core.OffsetKhz,
                        Nodes = current.Nodes.Select((node, index) => index == 0 ? node : node with
                        { OffsetKhz = node.OffsetKhz + core.OffsetKhz - core.ExpectedOffsetKhz }).ToArray()
                    };
                }
                else if (command is SetGpuVfCurveCommand curveCommand)
                {
                    CollectionAssert.AreEqual(current.Nodes.Select(node => node.OffsetKhz).ToArray(), curveCommand.ExpectedOffsetsKhz);
                    if (scenario >= 2)
                    {
                        outcome = CommandState.RolledBack;
                        if (scenario == 4) current = current with { Nodes = current.Nodes.Select((node, index) =>
                            index == 0 ? node : node with { OffsetKhz = node.OffsetKhz + 5_000 }).ToArray() };
                    }
                    else current = current with { Nodes = current.Nodes.Select((node, index) =>
                        node with { OffsetKhz = curveCommand.OffsetsKhz[index] }).ToArray() };
                }
                else Assert.Fail("Factory curve restore must not write unrelated hardware controls.");
                return Task.FromResult(new CommandResult(command.OperationId, outcome, null, RequiredUserAction.None, null, false));
            }
            var restore = await GpuCurveFactoryDefaults.RestoreAsync(original, Execute, () => current);
            Assert.AreEqual(scenario < 2, restore.Succeeded);
            Assert.AreEqual(25_000, current.MemoryOffsetKhz);
            if (scenario < 2)
            {
                Assert.AreEqual(0, current.CoreOffsetKhz);
                Assert.IsTrue(current.Nodes.All(node => node.OffsetKhz == 0));
                Assert.AreEqual(2, commands.Count);
            }
            else if (scenario == 2)
            {
                Assert.AreEqual(originalCore, current.CoreOffsetKhz);
                CollectionAssert.AreEqual(original.Nodes, current.Nodes);
                StringAssert.Contains(restore.Message, "原曲线已还原");
            }
            else StringAssert.Contains(restore.Message, "未完成");
            if (scenario == 4) Assert.AreEqual(2, commands.Count, "Concurrent changes must not be overwritten by recovery.");
            if (scenario == 5)
            {
                Assert.AreEqual(3, commands.Count);
                Assert.AreEqual(0, current.CoreOffsetKhz);
                Assert.AreEqual(45_000, current.Nodes[1].OffsetKhz, "A change during recovery must survive the atomic check.");
            }
        }
        var unknown = new GpuVfState([], -200_000, 200_000, "unavailable");
        var missing = await GpuCurveFactoryDefaults.RestoreAsync(unknown, _ => throw new AssertFailedException("Missing readback must not write"), () => unknown);
        Assert.IsFalse(missing.Succeeded);
    }

    [TestMethod]
    public void Advanced_curve_retargets_value_motion_and_respects_reduced_motion()
    {
        var code = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "GpuVoltageFrequencyCurve.xaml.cs");

        foreach (var marker in new[] { "AnimateStateAsync", "CompositionTarget.Rendering", "CurveAnimationDurationMs", "animationVersion", "AnimationsEnabled" })
            StringAssert.Contains(code, marker);
        Assert.IsFalse(code.Contains("Thread.Sleep", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("Task.Delay(CurveAnimationDurationMs", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Output_route_separates_display_cards_and_labels_scanout_truthfully()
    {
        var markup = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "GpuRouteDiagram.xaml");
        var code = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "GpuRouteDiagram.xaml.cs");

        StringAssert.Contains(markup, "<RowDefinition Height=\"11\" />");
        StringAssert.Contains(markup, "ActiveDisplaysHost\" Grid.Row=\"0\"");
        StringAssert.Contains(markup, "<Rectangle Grid.Row=\"1\"");
        StringAssert.Contains(markup, "<Border Grid.Row=\"2\"");
        StringAssert.Contains(code, "dGPU 扫描输出");
        StringAssert.Contains(code, "iGPU 扫描输出");
        StringAssert.Contains(code, "effectiveMode == MuxMode.Hybrid");
        StringAssert.Contains(code, "endpointX - 144");
        StringAssert.Contains(code, "EndpointIcon(display.Endpoint.IsInternal, color)");
        Assert.IsFalse(code.Contains("hasIntegrated && hasDiscrete &&", StringComparison.Ordinal));
        StringAssert.Contains(code, "实际渲染与转送活动未读回");
        StringAssert.Contains(code, "new Point(190, sourceY)");
        StringAssert.Contains(code, "new Point(286, trunkY)");
        StringAssert.Contains(code, "new Point(378, targetY)");
    }

    private static string ReadSource(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Jiaolong.ControlCenter.slnx")))
            directory = directory.Parent;
        var root = directory?.FullName ?? throw new DirectoryNotFoundException("无法定位蛟龙仓库根目录。");
        return File.ReadAllText(Path.Combine(new[] { root }.Concat(parts).ToArray()));
    }
}
