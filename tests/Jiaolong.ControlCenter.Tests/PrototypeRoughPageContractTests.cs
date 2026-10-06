using System;
using System.IO;
using System.Text.RegularExpressions;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class PrototypeRoughPageContractTests
{
    [TestMethod]
    public void Prototype_shell_routes_every_non_home_page_through_the_rough_workspace()
    {
        var shell = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "PrototypeWindow.xaml");
        var code = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "PrototypeWindow.xaml.cs");

        StringAssert.Contains(shell, "PrototypePageWorkspace");
        StringAssert.Contains(code, "ShowPage(\"Performance\")");
        foreach (var destination in new[] { "Gpu", "Fan", "Lighting", "Automation", "Settings" })
            StringAssert.Contains(code, $"\"{destination}\"");
    }

    [TestMethod]
    public void Settings_tray_notice_matches_client_scheduler_lifecycle()
    {
        var automationCode = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "AutomationWorkspaceV2.xaml.cs");
        var worker = ReadSource("src", "Jiaolong.Service", "Home", "AdaptiveAutomationWorker.cs");
        StringAssert.Contains(automationCode, "启用后由系统服务持续判定");
        StringAssert.Contains(automationCode, "前台应用和空闲规则需要客户端在线");
        StringAssert.Contains(worker, "ClientContextMaxAge = TimeSpan.FromSeconds(5)");
        StringAssert.Contains(worker, "IsFresh(saved.ClientContext, now)");
        StringAssert.Contains(worker, "应用规则暂停。");
        StringAssert.Contains(worker, "空闲返回规则暂停。");
    }

    [TestMethod]
    public void Main_window_diagnostic_export_uses_the_service_and_user_file_picker()
    {
        var code = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "PrototypePageWorkspace.xaml.cs");

        StringAssert.Contains(code, "async void OnSettingsExport");
        StringAssert.Contains(code, "FileSavePicker");
        StringAssert.Contains(code, "PickSaveFileAsync");
        StringAssert.Contains(code, "ExportDiagnosticsAsync");
        StringAssert.Contains(code, "CopyExportToAsync");
        StringAssert.Contains(code, "copy.StagingCleanupConfirmed");
        StringAssert.Contains(code, "服务端暂存副本未能确认清理");
        StringAssert.Contains(code, "导出诊断包失败：");
        Assert.IsFalse(code.Contains("诊断摘要已导出", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Main_window_settings_only_reports_returned_hardware_and_service_state()
    {
        var xaml = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "PrototypePageWorkspace.xaml");
        var code = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "PrototypePageWorkspace.xaml.cs");

        StringAssert.Contains(code, "SettingsBoardText.Text = identity?.BoardProduct ?? \"未知\";");
        StringAssert.Contains(code, "SettingsBiosText.Text = identity?.BiosVersion ?? \"未知\";");
        StringAssert.Contains(code, "SettingsServiceConnectionText.Text = session?.Status == HomeSessionStatus.Connected");
        Assert.IsFalse(xaml.Contains("N.1.08MRV02", StringComparison.Ordinal));
        Assert.IsFalse(xaml.Contains("微软 WHQL 签名验证", StringComparison.Ordinal));
        Assert.IsFalse(xaml.Contains("1 小时 32 分", StringComparison.Ordinal));
        Assert.IsFalse(xaml.Contains("看门狗保护活跃", StringComparison.Ordinal));
        var compact = xaml[xaml.IndexOf("x:Name=\"SettingsRuntimeCard\"", StringComparison.Ordinal)..];
        StringAssert.Contains(compact, "x:Name=\"SettingsDetailsCards\"");
        StringAssert.Contains(compact, "x:Name=\"SettingsProjectSupportCard\" Background=\"{StaticResource PrototypeControlAcrylicBrush}\"");
        StringAssert.Contains(compact, "Text=\"最近配置\"");
        Assert.IsFalse(compact.Contains("未公开", StringComparison.Ordinal));
        Assert.IsFalse(compact.Contains("#FF0A0D13", StringComparison.Ordinal));
        StringAssert.Contains(code, "ToolTipService.SetToolTip(SettingsLastConfigurationText, status.LastConfigurationDetail)");
    }

    [TestMethod]
    public void Performance_workspace_receives_the_same_home_state_snapshot_as_the_shell()
    {
        var workspace = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "PerformanceWorkspace.xaml.cs");
        var shell = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "PrototypeWindow.xaml.cs");

        StringAssert.Contains(workspace, "public void ApplyState(HomeStateSnapshot snapshot)");
        StringAssert.Contains(workspace, "CapabilityState.Available");
        StringAssert.Contains(shell, "PerformanceWorkspace.ApplyState(snapshot)");
    }

    [TestMethod]
    public void Performance_workspace_uses_save_as_the_single_apply_action()
    {
        var workspace = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "PerformanceWorkspace.xaml");
        var code = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "PerformanceWorkspace.xaml.cs");

        Assert.IsFalse(workspace.Contains("x:Name=\"ApplyPerformanceButton\"", StringComparison.Ordinal));
        Assert.IsFalse(workspace.Contains("Click=\"OnApplyPerformanceClick\"", StringComparison.Ordinal));
        Assert.IsFalse(workspace.Contains("PerformanceHardwareStatusText", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("已接入 CPU 调节", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("当前值未提供读取接口", StringComparison.Ordinal));
        StringAssert.Contains(code, "private async void OnSavePresetClick");
        StringAssert.Contains(code, "ExecuteAsync");
        StringAssert.Contains(code, "OnRestorePresetClick");
    }

    [TestMethod]
    public void Performance_workspace_keeps_voltage_and_ac_dc_but_removes_the_trend_band()
    {
        var workspace = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "PerformanceWorkspace.xaml");
        var code = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "PerformanceWorkspace.xaml.cs");

        StringAssert.Contains(workspace, "x:Name=\"CpuVoltageText\"");
        StringAssert.Contains(workspace, "x:Name=\"FrequencyAcButton\"");
        StringAssert.Contains(workspace, "x:Name=\"FrequencyDcButton\"");
        Assert.IsFalse(workspace.Contains("CpuTrendBand", StringComparison.Ordinal));
        Assert.IsFalse(workspace.Contains("CpuTrendCanvas", StringComparison.Ordinal));
        StringAssert.Contains(code, "snapshot.CpuVoltageVolts");
        StringAssert.Contains(code, "snapshot.CpuTemperatureC");
        StringAssert.Contains(code, "AcMaxFrequencyMhz");
        StringAssert.Contains(code, "DcMaxFrequencyMhz");
    }

    [TestMethod]
    public void Performance_frequency_segment_presentation_reads_theme_brushes_from_application_resources()
    {
        var code = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "PerformanceWorkspace.xaml.cs");

        StringAssert.Contains(code, "Application.Current.Resources[\"ModeAccentBrush\"]");
        StringAssert.Contains(code, "Application.Current.Resources[\"PrototypeControlAcrylicBrush\"]");
        Assert.IsFalse(code.Contains("var activeBackground = (Brush)Resources[\"ModeAccentBrush\"]", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Performance_content_scrolls_without_changing_the_fixed_shell()
    {
        var workspace = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "PerformanceWorkspace.xaml");

        StringAssert.Contains(workspace, "x:Name=\"PerformanceContentScrollViewer\"");
        StringAssert.Contains(workspace, "VerticalScrollBarVisibility=\"Hidden\"");
        StringAssert.Contains(workspace, "HorizontalScrollBarVisibility=\"Hidden\"");
    }

    [TestMethod]
    public void Performance_workspace_uses_the_persistent_preset_store()
    {
        var code = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "PerformanceWorkspace.xaml.cs");

        StringAssert.Contains(code, "PerformancePresetStore");
        StringAssert.Contains(code, "LoadAsync");
        StringAssert.Contains(code, "SaveAsync");
    }

    [TestMethod]
    public void Rough_pages_use_shared_acrylic_surfaces_and_mode_accent()
    {
        var workspace = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "PrototypePageWorkspace.xaml");

        StringAssert.Contains(workspace, "PrototypeControlAcrylicBrush");
        StringAssert.Contains(workspace, "ModeAccentBrush");
        StringAssert.Contains(workspace, "CornerRadius=\"16\"");
        foreach (var page in new[] { "GpuPage", "FanPage", "LightingPage", "AutomationPage", "SettingsPage" })
            StringAssert.Contains(workspace, $"x:Name=\"{page}\"");
    }

    [TestMethod]
    public void Non_gpu_rough_pages_do_not_reintroduce_theme_overlay_or_visible_scrollbars()
    {
        var workspace = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "PrototypePageWorkspace.xaml");
        foreach (var (page, nextPage) in new[]
        {
            ("FanPage", "LightingPage"),
            ("LightingPage", "AutomationPage"),
            ("AutomationPage", "SettingsPage"),
            ("SettingsPage", "</UserControl>")
        })
        {
            var start = workspace.IndexOf($"<Grid x:Name=\"{page}\"", StringComparison.Ordinal);
            var end = workspace.IndexOf($"<Grid x:Name=\"{nextPage}\"", start, StringComparison.Ordinal);
            if (end < 0) end = workspace.IndexOf(nextPage, start, StringComparison.Ordinal);
            var segment = workspace.Substring(start, end - start);
            Assert.IsFalse(segment.Contains("ModePanelBrush", StringComparison.Ordinal));
            Assert.IsFalse(segment.Contains("VerticalScrollBarVisibility=\"Auto\"", StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public void Lighting_page_does_not_fake_current_keyboard_color()
    {
        var code = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "LightingWorkspaceV2.xaml.cs");
        StringAssert.Contains(code, "snapshot.Controls.KeyboardLightingElapsedSeconds");
        StringAssert.Contains(code, "DescribeHardwareStatus(appliedLightingPlan, appliedLightingSeconds)");
        StringAssert.Contains(code, "硬件读回");
        int start = code.IndexOf("private async void OnUsePreset", StringComparison.Ordinal);
        string use = code[start..code.IndexOf("public void SetPageActive", start, StringComparison.Ordinal)];
        Assert.IsFalse(use.Contains("confirmation.ShowAsync", StringComparison.Ordinal), "Saved lighting presets apply directly.");
        StringAssert.Contains(use, "await ApplySavedLightingAsync");
        StringAssert.Contains(use, "if (result.State == CommandState.Applied && result.Error is null)");
        StringAssert.Contains(use, "PresetToolbar.SetActivePreset(key)");
    }

    [TestMethod]
    public void Page_boundaries_keep_mux_and_fan_curve_out_of_performance_page()
    {
        var performance = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "PerformanceWorkspace.xaml");
        var workspace = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "PrototypePageWorkspace.xaml");

        Assert.IsFalse(performance.Contains("SetMuxModeCommand", StringComparison.Ordinal));
        Assert.IsFalse(performance.Contains("SetFanControlCommand", StringComparison.Ordinal));
        StringAssert.Contains(workspace, "独显直连");
        StringAssert.Contains(workspace, "六点曲线");
    }

    [TestMethod]
    public void Gpu_output_modes_exclude_unverified_integrated_output()
    {
        var workspace = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "PrototypePageWorkspace.xaml");

        StringAssert.Contains(workspace, "GpuHybridButton");
        StringAssert.Contains(workspace, "GpuDiscreteButton");
        Assert.IsFalse(workspace.Contains("GpuIntegratedButton", StringComparison.Ordinal));
        Assert.IsFalse(workspace.Contains("Tag=\"Integrated\"", StringComparison.Ordinal));
        Assert.IsFalse(workspace.Contains("集成输出", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Gpu_page_template_covers_reference_controls_without_theme_overlay()
    {
        var workspace = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "PrototypePageWorkspace.xaml");
        var gpuStart = workspace.IndexOf("<Grid x:Name=\"GpuPage\"", StringComparison.Ordinal);
        var fanStart = workspace.IndexOf("<Grid x:Name=\"FanPage\"", StringComparison.Ordinal);
        var gpu = workspace.Substring(gpuStart, fanStart - gpuStart);

        Assert.IsFalse(gpu.Contains("ModePanelBrush", StringComparison.Ordinal));
        StringAssert.Contains(gpu, "GpuGlassSurfaceBrush");
        Assert.IsFalse(gpu.Contains("PrototypeControlAcrylicBrush", StringComparison.Ordinal));
        Assert.IsFalse(gpu.Contains("服务范围待确认", StringComparison.Ordinal));
        foreach (var name in new[]
        {
            "GpuMonitorStatusDot", "GpuMonitorStatusText", "GpuPowerText", "GpuPageFanText", "GpuControlGrid",
            "GpuCoreFrequencyPanel", "GpuMemoryFrequencyPanel",
            "GpuFrequencyLimitBox", "GpuMemoryFrequencyBox"
        })
            StringAssert.Contains(gpu, $"x:Name=\"{name}\"");
        StringAssert.Contains(gpu, "x:Name=\"GpuControlGrid\"");
        foreach (var removed in new[] { "GpuPowerLimitPanel", "GpuPowerLimitBox", "GpuPowerUnlockToggle" })
            Assert.IsFalse(gpu.Contains(removed, StringComparison.Ordinal));
        foreach (var label in new[] { "显卡频率锁定", "显存频率固定", "重置", "应用" })
            StringAssert.Contains(gpu, label);
        foreach (var label in new[] { "GPU 使用率", "GPU 温度", "核心频率", "显存频率", "显存占用", "功耗", "风扇转速", "电压" })
            StringAssert.Contains(gpu, label);
    }

    [TestMethod]
    public void Gpu_page_maps_power_telemetry_and_semantic_monitor_status()
    {
        var workspace = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "GpuWorkspaceV2.xaml");
        var code = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "GpuWorkspaceV2.xaml.cs");
        StringAssert.Contains(workspace, "x:Name=\"PowerText\"");
        StringAssert.Contains(workspace, "x:Name=\"MonitorDot\"");
        StringAssert.Contains(code, "PowerTrendCurve.UpdateSample(snapshot.GpuPowerWatts, snapshot.CapturedAtUtc)");
        StringAssert.Contains(code, "MonitorText.Text = available ? \"连接正常\" : \"遥测不可用\"");
        StringAssert.Contains(code, "GpuPowerCeilingText.Text = powerCeilingText");
    }

    [TestMethod]
    public void Gpu_page_uses_one_route_surface_and_shared_telemetry_surface_without_nested_cards()
    {
        var workspace = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "PrototypePageWorkspace.xaml");
        var gpuStart = workspace.IndexOf("<Grid x:Name=\"GpuPage\"", StringComparison.Ordinal);
        var fanStart = workspace.IndexOf("<Grid x:Name=\"FanPage\"", StringComparison.Ordinal);
        var gpu = workspace.Substring(gpuStart, fanStart - gpuStart);

        // 4 张显卡输出设置卡片
        foreach (var mode in new[] { "独显直连", "混合输出", "集显优先", "自动切换" })
            StringAssert.Contains(gpu, mode);
        foreach (var btn in new[] { "GpuDiscreteButton", "GpuHybridButton", "GpuEcoButton", "GpuAutoButton" })
            StringAssert.Contains(gpu, $"x:Name=\"{btn}\"");

        // GPU 使用率趋势图与 3 组微型统计卡
        StringAssert.Contains(gpu, "GPU 使用率趋势");
        StringAssert.Contains(gpu, "x:Name=\"GpuTrendAreaPath\"");
        StringAssert.Contains(gpu, "x:Name=\"GpuTrendWavePath\"");
        foreach (var stat in new[] { "平均使用率", "峰值使用率", "最低使用率" })
            StringAssert.Contains(gpu, stat);
        foreach (var statName in new[] { "GpuAvgUsageText", "GpuPeakUsageText", "GpuMinUsageText" })
            StringAssert.Contains(gpu, $"x:Name=\"{statName}\"");

        StringAssert.Contains(gpu, "VerticalScrollBarVisibility=\"Hidden\"");
        Assert.IsFalse(gpu.Contains("Background=\"{StaticResource ModePanelBrush}\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Sidebar_uses_a_theme_indicator_transform_and_reduced_motion_switch()
    {
        var sidebar = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "HomeSidebar.xaml");
        var code = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "HomeSidebar.xaml.cs");

        StringAssert.Contains(sidebar, "x:Name=\"NavSelectionIndicator\"");
        StringAssert.Contains(sidebar, "Width=\"292\"");
        StringAssert.Contains(sidebar, "IsHitTestVisible=\"False\"");
        StringAssert.Contains(sidebar, "ModeAccentBrush");
        StringAssert.Contains(sidebar, "CompositeTransform");
        StringAssert.Contains(code, "ReducedMotion");
        StringAssert.Contains(code, "TranslateY");
        StringAssert.Contains(code, "(UIElement.RenderTransform).(CompositeTransform.TranslateY)");
        Assert.IsFalse(code.Contains("SetTarget(animation, transform)", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Sidebar_applies_the_same_interruptible_motion_to_all_navigation_and_quick_icons()
    {
        var sidebar = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "HomeSidebar.xaml");
        var code = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "HomeSidebar.xaml.cs");

        foreach (var icon in new[] { "NavHomeIcon", "NavPerformanceIcon", "NavGpuIcon", "NavFanIcon", "NavLightingIcon", "NavAutomationIcon", "NavSettingsIcon" })
            StringAssert.Contains(sidebar, $"x:Name=\"{icon}\"");
        StringAssert.Contains(code, "AttachIconMotion");
        StringAssert.Contains(code, "AnimateIcon");
        StringAssert.Contains(code, "ScaleX");
        StringAssert.Contains(code, "ScaleY");
        StringAssert.Contains(code, "CreateIcon(item)");
        StringAssert.Contains(code, "ReducedMotion");
    }

    [TestMethod]
    public void Prototype_window_uses_interruptible_fade_through_page_transition()
    {
        var code = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "PrototypeWindow.xaml.cs");

        StringAssert.Contains(code, "PrototypePageTransitionController");
        StringAssert.Contains(code, "pageTransitionStoryboard?.Stop");
        StringAssert.Contains(code, "BeginPageExitTransition");
        StringAssert.Contains(code, "BeginPageEnterTransition");
        StringAssert.Contains(code, "PageWorkspace.Show(page)");
        Assert.IsFalse(code.Contains("TranslateX", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("CompositeTransform", StringComparison.Ordinal));
        StringAssert.Contains(code, "Opacity");
        StringAssert.Contains(code, "plan.Snap");
    }

    [TestMethod]
    public void Performance_preset_selection_does_not_switch_the_current_mode()
    {
        var workspace = ReadPerformanceV2Markup();
        var code = ReadPerformanceV2Code();
        StringAssert.Contains(workspace, "SelectedKeyChanged=\"OnPresetKeyChanged\"");
        StringAssert.Contains(workspace, "SaveRequested=\"OnSavePresetClick\"");
        StringAssert.Contains(workspace, "UseRequested=\"OnUsePresetClick\"");
        StringAssert.Contains(code, "private async void OnUsePresetClick");
        Assert.IsFalse(code.Contains("confirmation.ShowAsync", StringComparison.Ordinal), "Saved performance presets apply directly.");
        StringAssert.Contains(code, "new SetCpuTuningBatchCommand");
        StringAssert.Contains(code, "已恢复本次修改");
        Assert.IsFalse(code.Contains("已完成项不会自动回滚", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Performance_page_uses_a_single_profile_selector_and_aligned_slider_rows()
    {
        var workspace = ReadPerformanceV2Markup();
        StringAssert.Contains(workspace, "x:Name=\"PerformancePageScrollViewer\"");
        StringAssert.Contains(workspace, "Grid Padding=\"18,30,18,28\"");
        StringAssert.Contains(workspace, "x:Name=\"DefaultContentGrid\" Grid.Row=\"1\" ColumnSpacing=\"16\"");
        StringAssert.Contains(workspace, "<shared:PerformanceParameterRowV2 x:Name=\"TemperatureRow\"");
        StringAssert.Contains(workspace, "<shared:PerformanceParameterRowV2 x:Name=\"FrequencyRow\"");
    }

    [TestMethod]
    public void Performance_page_uses_live_monitoring_and_always_visible_advanced_controls()
    {
        var workspace = ReadPerformanceV2Markup();
        var code = ReadPerformanceV2Code();
        var boundaryCode = ReadSource("src", "Jiaolong.ControlCenter", "Controls", "CpuOperatingBoundaryV2.xaml.cs");
        StringAssert.Contains(workspace, "x:Name=\"PerformancePageScrollViewer\"");
        StringAssert.Contains(workspace, "x:Name=\"AdvancedButton\"");
        StringAssert.Contains(workspace, "x:Name=\"AdvancedWorkspace\"");
        StringAssert.Contains(code, "ApplyTelemetry(HardwareSnapshot snapshot)");
        StringAssert.Contains(code, "CpuBoundary.ApplyTelemetry(snapshot)");
        StringAssert.Contains(code, "currentState = available ? snapshot.Controls.CpuTuning : null");
        StringAssert.Contains(boundaryCode, "temperatureValue = snapshot.CpuTemperatureC");
        StringAssert.Contains(boundaryCode, "powerValue = snapshot.CpuPowerWatts is int power ? power : null");
    }

    [TestMethod]
    public void Performance_page_uses_compact_live_tiles_and_preset_actions()
    {
        var workspace = ReadPerformanceV2Markup();
        var toolbar = ReadSource("src", "Jiaolong.ControlCenter", "Controls", "PagePresetToolbar.xaml");
        StringAssert.Contains(workspace, "shared:PagePresetToolbar");
        StringAssert.Contains(toolbar, "Content=\"保存当前\"");
        StringAssert.Contains(toolbar, "Content=\"保存并应用\"");
        StringAssert.Contains(workspace, "x:Name=\"TemperatureRow\"");
        StringAssert.Contains(workspace, "x:Name=\"CpuBoundary\"");
    }

    [TestMethod]
    public void Performance_page_contains_complete_advanced_smu_and_pbo_layout()
    {
        var advanced = ReadAdvancedCpuV2Markup();
        var code = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "AdvancedCpuTuningWorkspaceV2.xaml.cs");
        StringAssert.Contains(advanced, "功耗与电流");
        StringAssert.Contains(advanced, "核心与自动加速");
        StringAssert.Contains(advanced, "逐核曲线细调");
        StringAssert.Contains(advanced, "x:Name=\"AdvancedSmuPanel\"");
        StringAssert.Contains(advanced, "x:Name=\"AdvancedPboPanel\"");
        Assert.IsFalse(code.Contains("PerCoreOcClockMhz", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Performance_page_uses_two_independent_advanced_cpu_surfaces()
    {
        var workspace = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "PerformanceWorkspace.xaml");
        var advancedStart = workspace.IndexOf("x:Name=\"AdvancedCpuTuningPanel\"", StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, advancedStart);
        var advanced = workspace[advancedStart..];

        StringAssert.Contains(advanced, "x:Name=\"AdvancedCpuLimitsBand\"");
        StringAssert.Contains(advanced, "x:Name=\"AdvancedBoostBand\"");
        StringAssert.Contains(advanced, "x:Name=\"AdvancedCoreBand\"");
        StringAssert.Contains(advanced, "x:Name=\"AdvancedSmuPanel\" Grid.Column=\"0\"");
        StringAssert.Contains(advanced, "x:Name=\"AdvancedPboPanel\" Grid.Column=\"1\"");
        Assert.IsFalse(advanced.Contains("<Border Grid.Column=\"1\" Width=\"1\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Performance_page_keeps_advanced_values_close_to_their_labels()
    {
        var advanced = ReadAdvancedCpuV2Markup();
        var row = ReadPerformanceParameterRowV2();
        StringAssert.Contains(advanced, "<shared:ParameterHelpButton");
        StringAssert.Contains(advanced, "x:Name=\"AdvancedSmuPanel\"");
        StringAssert.Contains(row, "<TextBox");
        StringAssert.Contains(row, "TextAlignment=\"Center\"");
    }

    [TestMethod]
    public void Performance_page_aligns_advanced_values_in_fixed_field_columns()
    {
        var advanced = ReadAdvancedCpuV2Markup();
        var row = ReadPerformanceParameterRowV2();
        StringAssert.Contains(advanced, "x:Name=\"AdvancedSmuPanel\"");
        StringAssert.Contains(advanced, "x:Name=\"AdvancedPboPanel\"");
        StringAssert.Contains(row, "<Grid.ColumnDefinitions>");
        StringAssert.Contains(row, "x:Name=\"ValueBox\"");
    }

    [TestMethod]
    public void Performance_page_clears_number_box_editing_when_blank_content_is_pressed()
    {
        var workspace = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "PerformanceWorkspace.xaml");
        var code = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "PerformanceWorkspace.xaml.cs");

        StringAssert.Contains(workspace, "x:Name=\"PerformanceContentScrollViewer\"");
        StringAssert.Contains(workspace, "PointerPressed=\"OnPerformanceContentPointerPressed\"");
        StringAssert.Contains(code, "private void OnPerformanceContentPointerPressed");
        StringAssert.Contains(code, "PerformanceContentScrollViewer.Focus(FocusState.Programmatic);");
        StringAssert.Contains(code, "FindAncestor<NumberBox>");
    }

    [TestMethod]
    public void Performance_page_centers_number_box_template_input_and_leaves_bottom_safe_space()
    {
        var workspace = ReadPerformanceV2Markup();
        var row = ReadPerformanceParameterRowV2();
        StringAssert.Contains(workspace, "Grid Padding=\"18,30,18,28\"");
        StringAssert.Contains(workspace, "<shared:PerformanceParameterRowV2");
        StringAssert.Contains(row, "TextAlignment=\"Center\"");
        StringAssert.Contains(row, "VerticalContentAlignment=\"Center\"");
    }

    [TestMethod]
    public void Performance_page_hides_toggle_caption_and_centers_number_box_scroll_content()
    {
        var workspace = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "PerformanceWorkspace.xaml");
        var code = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "PerformanceWorkspace.xaml.cs");
        Assert.IsFalse(workspace.Contains("AdvancedOverclockToggle", StringComparison.Ordinal));
        Assert.IsFalse(workspace.Contains("OcClockBox", StringComparison.Ordinal));
        Assert.IsFalse(workspace.Contains("OcVoltageBox", StringComparison.Ordinal));
        StringAssert.Contains(code, "FindDescendant<ScrollViewer>(inputBox)");
        StringAssert.Contains(code, "inputScrollViewer.HorizontalContentAlignment = HorizontalAlignment.Center");
        StringAssert.Contains(code, "inputScrollViewer.VerticalContentAlignment = VerticalAlignment.Center");
    }

    [TestMethod]
    public void Performance_page_aligns_advanced_gap_with_power_plan_curve_gap()
    {
        var workspace = ReadPerformanceV2Markup();
        var advanced = ReadAdvancedCpuV2Markup();
        StringAssert.Contains(workspace, "x:Name=\"AdvancedButton\"");
        StringAssert.Contains(workspace, "x:Name=\"AdvancedWorkspace\"");
        StringAssert.Contains(advanced, "x:Name=\"AdvancedSmuPanel\"");
        StringAssert.Contains(advanced, "x:Name=\"AdvancedPboPanel\"");
    }

    [TestMethod]
    public void Performance_page_unifies_header_dropdown_surface_and_keeps_labels_visible()
    {
        var workspace = ReadPerformanceV2Markup();
        var toolbar = ReadSource("src", "Jiaolong.ControlCenter", "Controls", "PagePresetToolbar.xaml");
        StringAssert.Contains(workspace, "Text=\"性能控制\"");
        StringAssert.Contains(workspace, "<shared:PagePresetToolbar");
        StringAssert.Contains(toolbar, "local:ModePresetPicker");
        StringAssert.Contains(toolbar, "Content=\"保存当前\"");
        StringAssert.Contains(toolbar, "Content=\"保存并应用\"");
    }

    [TestMethod]
    public void Performance_page_uses_one_downward_menu_flyout_for_every_expandable_selector()
    {
        var toolbar = ReadSource("src", "Jiaolong.ControlCenter", "Controls", "PagePresetToolbar.xaml");
        var picker = ReadSource("src", "Jiaolong.ControlCenter", "Controls", "ModePresetPicker.xaml");
        StringAssert.Contains(toolbar, "local:ModePresetPicker");
        StringAssert.Contains(picker, "Flyout");
        StringAssert.Contains(picker, "6 个模式 · 每个模式 3 个预设");
        StringAssert.Contains(ReadPerformanceV2Markup(), "SelectedKeyChanged=\"OnPresetKeyChanged\"");
    }

    [TestMethod]
    public void Performance_advanced_cpu_help_and_recommendations_are_scoped_to_subsections()
    {
        var advanced = ReadAdvancedCpuV2Markup();
        StringAssert.Contains(advanced, "x:Name=\"AdvancedSmuPanel\"");
        StringAssert.Contains(advanced, "x:Name=\"AdvancedPboPanel\"");
        StringAssert.Contains(advanced, "<shared:ParameterHelpButton");
        StringAssert.Contains(advanced, "ParameterName=\"");
    }

    [TestMethod]
    public void Performance_page_offsets_advanced_panel_from_the_previous_viewport_edge()
    {
        var workspace = ReadPerformanceV2Markup();
        StringAssert.Contains(workspace, "x:Name=\"PerformancePageScrollViewer\"");
        StringAssert.Contains(workspace, "VerticalScrollBarVisibility=\"Hidden\"");
        StringAssert.Contains(workspace, "Grid Padding=\"18,30,18,28\"");
        StringAssert.Contains(workspace, "x:Name=\"AdvancedWorkspace\"");
    }

    [TestMethod]
    public void Performance_page_uses_five_horizontal_integer_metrics_and_explains_curve_controls()
    {
        var workspace = ReadPerformanceV2Markup();
        var code = ReadPerformanceV2Code();
        var advanced = ReadAdvancedCpuV2Markup();
        var boundaryCode = ReadSource("src", "Jiaolong.ControlCenter", "Controls", "CpuOperatingBoundaryV2.xaml.cs");
        StringAssert.Contains(workspace, "x:Name=\"TemperatureRow\"");
        StringAssert.Contains(workspace, "x:Name=\"SustainedPowerRow\"");
        StringAssert.Contains(code, "ApplyTelemetry(HardwareSnapshot snapshot)");
        StringAssert.Contains(code, "CpuBoundary.ApplyTelemetry(snapshot)");
        StringAssert.Contains(boundaryCode, "temperatureValue = snapshot.CpuTemperatureC");
        StringAssert.Contains(boundaryCode, "powerValue = snapshot.CpuPowerWatts is int power ? power : null");
        StringAssert.Contains(boundaryCode, "frequencyValue = cpuFrequencyDisplay.Update(snapshot.CpuFrequencyMhz)");
        StringAssert.Contains(boundaryCode, "voltageValue = snapshot.CpuVoltageVolts");
        StringAssert.Contains(advanced, "逐核曲线细调");
        StringAssert.Contains(advanced, "<shared:ParameterHelpButton");
    }

    [TestMethod]
    public void Performance_page_uses_equal_monitor_cards_crossing_markers_and_compact_policy_rows()
    {
        var workspace = ReadPerformanceV2Markup();
        var code = ReadPerformanceV2Code();
        var boundary = ReadSource("src", "Jiaolong.ControlCenter", "Controls", "CpuOperatingBoundaryV2.xaml.cs");
        StringAssert.Contains(workspace, "Text=\"CPU 运行边界\"");
        StringAssert.Contains(workspace, "x:Name=\"ConnectionText\"");
        StringAssert.Contains(workspace, "x:Name=\"CpuBoundary\"");
        StringAssert.Contains(code, "CpuBoundary.ApplyTelemetry(snapshot)");
        StringAssert.Contains(boundary, "snapshot.CpuTemperatureC");
        StringAssert.Contains(boundary, "snapshot.CpuPowerWatts");
    }

    [TestMethod]
    public void Performance_curve_has_axes_feedback_help_and_power_plan_discovery_includes_hidden_schemes()
    {
        var advanced = ReadAdvancedCpuV2Markup();
        var code = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "AdvancedCpuTuningWorkspaceV2.xaml.cs");
        StringAssert.Contains(advanced, "逐核曲线细调");
        StringAssert.Contains(ReadPerformanceV2Markup(), "沿用 BIOS");
        StringAssert.Contains(advanced, "<shared:ParameterHelpButton");
        StringAssert.Contains(code, "CpuCurveOptimizerMode");
    }

    [TestMethod]
    public void Performance_number_boxes_set_ranges_before_initial_values()
    {
        var workspace = ReadPerformanceV2Markup();
        var row = ReadPerformanceParameterRowV2();
        StringAssert.Contains(workspace, "Minimum=\"45\" Maximum=\"100\"");
        StringAssert.Contains(workspace, "RecommendedValue=\"75\"");
        StringAssert.Contains(row, "x:Name=\"ValueBox\"");
    }

    [TestMethod]
    public void Performance_monitor_is_one_compact_card_with_dividers_statuses_and_scales()
    {
        var workspace = ReadPerformanceV2Markup();
        var code = ReadPerformanceV2Code();
        StringAssert.Contains(workspace, "x:Name=\"ConnectionText\"");
        StringAssert.Contains(workspace, "x:Name=\"BoundarySourceText\"");
        StringAssert.Contains(workspace, "Text=\"等待读回\"");
        StringAssert.Contains(code, "ApplyTelemetry(HardwareSnapshot snapshot)");
        StringAssert.Contains(code, "CpuBoundary.ApplyTelemetry(snapshot)");
        StringAssert.Contains(ReadSource("src", "Jiaolong.ControlCenter", "Controls", "CpuOperatingBoundaryV2.xaml.cs"), "snapshot.CpuTemperatureC");
    }

    [TestMethod]
    public void Performance_editor_has_single_profile_caption_edge_scrollbar_and_dynamic_markers()
    {
        var workspace = ReadPerformanceV2Markup();
        var picker = ReadSource("src", "Jiaolong.ControlCenter", "Controls", "ModePresetPicker.xaml");
        var rail = ReadSource("src", "Jiaolong.ControlCenter", "Controls", "PerformanceTuningRailV2.xaml");
        StringAssert.Contains(workspace, "SelectedKeyChanged=\"OnPresetKeyChanged\"");
        StringAssert.Contains(picker, "6 个模式 · 每个模式 3 个预设");
        StringAssert.Contains(rail, "RecommendedMarker");
    }

    [TestMethod]
    public void Performance_help_explains_effect_troubleshooting_ranges_and_three_profiles()
    {
        var row = ReadPerformanceParameterRowV2();
        var workspace = ReadPerformanceV2Markup();
        StringAssert.Contains(workspace, "HelpEffect=");
        StringAssert.Contains(workspace, "HelpSafeRange=");
        StringAssert.Contains(workspace, "HelpRisk=");
        StringAssert.Contains(row, "ParameterHelpButton");
    }

    [TestMethod]
    public void Performance_recommendation_markers_follow_slider_track_geometry()
    {
        var rail = ReadSource("src", "Jiaolong.ControlCenter", "Controls", "PerformanceTuningRailV2.xaml");
        StringAssert.Contains(rail, "x:Name=\"RecommendedMarker\"");
        var code = ReadSource("src", "Jiaolong.ControlCenter", "Controls", "PerformanceTuningRailV2.xaml.cs");
        StringAssert.Contains(code, "RailCanvas.ActualWidth");
        StringAssert.Contains(code, "Canvas.SetLeft(RecommendedMarker, Project(RecommendedValue, width) - 1d)");
        Assert.IsFalse(rail.Contains("LimitMarker", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Performance_profile_status_is_reserved_for_save_feedback_and_number_boxes_support_wheel_input()
    {
        var code = ReadPerformanceV2Code();
        var toolbarCode = ReadSource("src", "Jiaolong.ControlCenter", "Controls", "PagePresetToolbar.xaml.cs");
        var workspace = ReadPerformanceV2Markup();
        StringAssert.Contains(code, "_ = PresetToolbar.ShowSavedStatusAsync()");
        StringAssert.Contains(code, "await PresetToolbar.ShowStatusAsync(\"保存失败，原预设未确认更改\")");
        StringAssert.Contains(toolbarCode, "TimeSpan.FromSeconds(2)");
        StringAssert.Contains(workspace, "SaveRequested=\"OnSavePresetClick\"");
        StringAssert.Contains(workspace, "UseRequested=\"OnUsePresetClick\"");
    }

    [TestMethod]
    public void Performance_lower_area_uses_two_equal_policy_cards_and_one_wide_optimizer_card()
    {
        var workspace = ReadPerformanceV2Markup();
        var code = ReadPerformanceV2Code();
        StringAssert.Contains(workspace, "x:Name=\"CpuBoundary\"");
        StringAssert.Contains(workspace, "x:Name=\"AdvancedWorkspace\"");
        StringAssert.Contains(workspace, "x:Name=\"AdvancedButton\"");
        StringAssert.Contains(code, "SetInteractionAvailability");
        StringAssert.Contains(code, "currentState is not null");
    }

    [TestMethod]
    public void Performance_power_plan_selector_keeps_text_and_border_light_across_interaction_states()
    {
        var workspace = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "PerformanceWorkspace.xaml");
        var code = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "PerformanceWorkspace.xaml.cs");

        StringAssert.Contains(workspace, "x:Name=\"PowerPlanSelector\"");
        StringAssert.Contains(workspace, "<MenuFlyout Placement=\"BottomEdgeAlignedLeft\" AreOpenCloseAnimationsEnabled=\"True\" />");
        StringAssert.Contains(code, "OnPowerPlanMenuItemClick");
        Assert.IsFalse(workspace.Contains("PointerEntered=\"OnPowerPlanPointerEntered\"", StringComparison.Ordinal));
        Assert.IsFalse(workspace.Contains("PointerExited=\"OnPowerPlanPointerExited\"", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("PowerPlanSelector.Foreground = new SolidColorBrush(Colors.Transparent)", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Performance_controls_use_mode_gradients_centered_markers_and_edge_fades()
    {
        var resources = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "PerformanceV2Resources.xaml");
        var workspace = ReadPerformanceV2Markup();
        var row = ReadPerformanceParameterRowV2();
        StringAssert.Contains(resources, "PerformanceV2TrackHeight");
        StringAssert.Contains(resources, "PerformanceV2CardBrush");
        StringAssert.Contains(workspace, "<shared:PerformanceParameterRowV2");
        StringAssert.Contains(row, "PerformanceTuningRailV2");
    }

    [TestMethod]
    public void Performance_boost_toggle_keeps_hover_and_pressed_geometry_stable()
    {
        var advanced = ReadAdvancedCpuV2Markup();
        var workspace = ReadPerformanceV2Markup();
        StringAssert.Contains(advanced, "核心与自动加速");
        StringAssert.Contains(advanced, "x:Name=\"AdvancedPboPanel\"");
        StringAssert.Contains(advanced, "<shared:ParameterHelpButton");
        StringAssert.Contains(workspace, "x:Name=\"AdvancedWorkspace\"");
    }

    private static string ReadPerformanceV2Markup() =>
        ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "PerformanceWorkspaceV2.xaml");

    private static string ReadPerformanceV2Code() =>
        ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "PerformanceWorkspaceV2.xaml.cs");

    private static string ReadAdvancedCpuV2Markup() =>
        ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "AdvancedCpuTuningWorkspaceV2.xaml");

    private static string ReadPerformanceParameterRowV2() =>
        ReadSource("src", "Jiaolong.ControlCenter", "Controls", "PerformanceParameterRowV2.xaml");
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
