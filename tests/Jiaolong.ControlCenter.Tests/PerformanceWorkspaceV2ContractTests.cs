using System.Xml.Linq;
using Jiaolong_ControlCenter.ViewModels;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class PerformanceWorkspaceV2ContractTests
{
    [TestMethod]
    public void Replacement_workspace_preserves_the_window_integration_contract()
    {
        Type? workspace = typeof(PerformanceViewModel).Assembly.GetType(
            "Jiaolong_ControlCenter.Prototype.Controls.PerformanceWorkspaceV2");

        Assert.IsNotNull(workspace);
        Assert.IsNotNull(workspace.GetMethod("AttachSession"));
        Assert.IsNotNull(workspace.GetMethod("ApplyState"));
        Assert.IsNotNull(workspace.GetMethod("ApplyTelemetry"));
        Assert.IsNotNull(workspace.GetMethod("ShowPresetPreview"));
        Assert.IsNotNull(workspace.GetMethod("ApplyAcceptancePreview"));
        Assert.AreEqual(typeof(Task<bool>), workspace.GetMethod("ApplyCustomPresetAsync")?.ReturnType);
        string code = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "Jiaolong.ControlCenter", "Prototype", "Controls", "PerformanceWorkspaceV2.xaml.cs"));
        Assert.IsFalse(code.Contains("confirmation.ShowAsync", StringComparison.Ordinal), "Saved performance presets apply directly through the rollback transaction.");
        StringAssert.Contains(code, "NativeMode = nativeMode");
        StringAssert.Contains(code, "result.State != CommandState.Applied");
    }

    [TestMethod]
    public void Acceptance_routes_use_deterministic_turbo_visuals_without_touching_normal_runtime()
    {
        string window = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "Jiaolong.ControlCenter", "Prototype", "PrototypeWindow.xaml.cs"));

        StringAssert.Contains(window, "PerformanceWorkspaceV2Preview.ApplyAcceptancePreview()");
        StringAssert.Contains(window, "RequestModeVisuals(PrototypePerformanceMode.Turbo, animate: false)");
        StringAssert.Contains(window, "acceptancePage is not null");
    }

    [TestMethod]
    public void Parameter_row_matches_the_approved_pixel_contract()
    {
        string root = FindRepositoryRoot();
        string row = File.ReadAllText(Path.Combine(
            root, "src", "Jiaolong.ControlCenter", "Controls", "PerformanceParameterRowV2.xaml"));
        string rail = File.ReadAllText(Path.Combine(
            root, "src", "Jiaolong.ControlCenter", "Controls", "PerformanceTuningRailV2.xaml"));
        string code = File.ReadAllText(Path.Combine(
            root, "src", "Jiaolong.ControlCenter", "Controls", "PerformanceParameterRowV2.xaml.cs"));
        int handler = code.IndexOf("private void OnValueBoxTextChanged", StringComparison.Ordinal);
        string textHandler = code[handler..code.IndexOf("private void OnValueBoxLostFocus", handler, StringComparison.Ordinal)];
        StringAssert.Contains(textHandler, "ValueBox.FocusState == FocusState.Unfocused");

        StringAssert.Contains(row, "<ColumnDefinition Width=\"157\" />");
        StringAssert.Contains(row, "Width=\"98\"");
        StringAssert.Contains(row, "FontSize=\"20\"");
        StringAssert.Contains(row, "<TextBox");
        StringAssert.Contains(row, "TextAlignment=\"Center\"");
        Assert.IsFalse(row.Contains("<NumberBox", StringComparison.Ordinal));
        StringAssert.Contains(rail, "Height=\"6\"");
        StringAssert.Contains(rail, "Width=\"14\" Height=\"14\"");
        StringAssert.Contains(rail, "Margin=\"16,0\"");
        Assert.IsFalse(rail.Contains("x:Name=\"LimitMarker\"", StringComparison.Ordinal));
        Assert.IsFalse(rail.Contains("x:Name=\"LimitText\"", StringComparison.Ordinal));

        string workspace = File.ReadAllText(Path.Combine(
            root, "src", "Jiaolong.ControlCenter", "Prototype", "Controls", "PerformanceWorkspaceV2.xaml"));
        StringAssert.Contains(workspace, "Background=\"{StaticResource PerformanceV2CardBrush}\"");
        StringAssert.Contains(workspace, "DisplayScale=\"0.001\"");
        StringAssert.Contains(workspace, "Unit=\"GHz\"");
    }

    [TestMethod]
    public void V2_advanced_cpu_keeps_all_three_sections_visible_in_one_surface()
    {
        string root = FindRepositoryRoot();
        string workspace = File.ReadAllText(Path.Combine(
            root, "src", "Jiaolong.ControlCenter", "Prototype", "Controls", "AdvancedCpuTuningWorkspaceV2.xaml"));
        string code = File.ReadAllText(Path.Combine(
            root, "src", "Jiaolong.ControlCenter", "Prototype", "Controls", "AdvancedCpuTuningWorkspaceV2.xaml.cs"));

        StringAssert.Contains(workspace, "功耗与电流");
        StringAssert.Contains(workspace, "核心与自动加速");
        StringAssert.Contains(workspace, "<Grid Padding=\"0\" RowSpacing=\"16\">");
        StringAssert.Contains(workspace, "逐核曲线细调");
        StringAssert.Contains(workspace, "x:Name=\"AdvancedSmuPanel\"");
        StringAssert.Contains(workspace, "x:Name=\"AdvancedPboPanel\"");
        StringAssert.Contains(workspace, "<shared:ParameterHelpButton");
        StringAssert.Contains(workspace, "Background=\"{StaticResource PerformanceV2CardBrush}\"");
        Assert.IsFalse(workspace.Contains("TabView", StringComparison.Ordinal));
        Assert.IsFalse(workspace.Contains("NavigationView", StringComparison.Ordinal));
        Assert.IsFalse(workspace.Contains("Text=\"?\"", StringComparison.Ordinal));
        Assert.IsFalse(workspace.Contains("OffContent=\"关闭\"", StringComparison.Ordinal));
        Assert.IsFalse(workspace.Contains("OnContent=\"开启\"", StringComparison.Ordinal));
        Assert.IsFalse(workspace.Contains("Width=\"1380\"", StringComparison.Ordinal));
        Assert.IsFalse(workspace.Contains("x:Name=\"CollapseButton\"", StringComparison.Ordinal));
        Assert.IsFalse(workspace.Contains("<ScrollViewer", StringComparison.Ordinal));
        StringAssert.Contains(code, "ReadDraft");
        Assert.IsFalse(code.Contains("PerCoreOcClockMhz", StringComparison.Ordinal));
        StringAssert.Contains(code, "PerCoreCurveOptimizer");
    }

    [TestMethod]
    public void V2_preset_selector_keeps_save_and_use_actions_separate()
    {
        string workspace = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "Jiaolong.ControlCenter", "Prototype", "Controls", "PerformanceWorkspaceV2.xaml"));
        string code = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "Jiaolong.ControlCenter", "Prototype", "Controls", "PerformanceWorkspaceV2.xaml.cs"));
        string toolbar = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "Jiaolong.ControlCenter", "Controls", "PagePresetToolbar.xaml"));

        StringAssert.Contains(toolbar, "Content=\"保存预设\"");
        StringAssert.Contains(toolbar, "Content=\"使用预设\"");
        StringAssert.Contains(toolbar, "<local:ModePresetPicker");
        StringAssert.Contains(workspace, "SelectedKeyChanged=\"OnPresetKeyChanged\"");
        Assert.IsFalse(toolbar.Contains("MenuFlyout", StringComparison.Ordinal));

        string picker = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "Jiaolong.ControlCenter", "Controls", "ModePresetPicker.xaml"));
        string pickerCode = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "Jiaolong.ControlCenter", "Controls", "ModePresetPicker.xaml.cs"));
        StringAssert.Contains(picker, "TargetType=\"FlyoutPresenter\"");
        StringAssert.Contains(picker, "Property=\"MaxWidth\" Value=\"620\"");
        StringAssert.Contains(picker, "6 个模式 · 每个模式 3 个预设");
        foreach (var name in new[] { "Slot1Summary", "Slot2Summary", "Slot3Summary" })
            StringAssert.Contains(picker, $"x:Name=\"{name}\"");
        StringAssert.Contains(pickerCode, "SetSlotSummary");
        StringAssert.Contains(pickerCode, "SetActivePreset");
    }

    [TestMethod]
    public void V2_visual_tokens_are_centralized_in_one_resource_dictionary()
    {
        string root = FindRepositoryRoot();
        string resourcesPath = Path.Combine(
            root, "src", "Jiaolong.ControlCenter", "Prototype", "PerformanceV2Resources.xaml");
        Assert.IsTrue(File.Exists(resourcesPath));
        string resources = File.ReadAllText(resourcesPath);
        string theme = File.ReadAllText(Path.Combine(
            root, "src", "Jiaolong.ControlCenter", "Prototype", "PrototypeThemeResources.xaml"));

        string[] requiredKeys =
        [
            "PerformanceV2CardBrush", "PerformanceV2CardBorderBrush", "PerformanceV2AccentBrush",
            "PerformanceV2MutedTextBrush", "PerformanceV2CardCornerRadius", "PerformanceV2CardShadow",
            "PerformanceV2TrackHeight", "PerformanceV2ThumbSize"
        ];
        foreach (string key in requiredKeys)
            StringAssert.Contains(resources, $"x:Key=\"{key}\"");

        StringAssert.Contains(resources, "x:Key=\"PerformanceV2TrackHeight\">6</x:Double>");
        StringAssert.Contains(resources, "x:Key=\"PerformanceV2ThumbSize\">14</x:Double>");
        StringAssert.Contains(resources, "x:Key=\"PerformanceV2CardBrush\" ResourceKey=\"PrototypeControlAcrylicBrush\"");
        StringAssert.Contains(theme, "Source=\"/Prototype/PerformanceV2Resources.xaml\"");
    }

    [TestMethod]
    public void V2_default_state_keeps_native_controls_without_nested_cards_or_negative_alignment()
    {
        string root = FindRepositoryRoot();
        string workspace = File.ReadAllText(Path.Combine(
            root, "src", "Jiaolong.ControlCenter", "Prototype", "Controls", "PerformanceWorkspaceV2.xaml"));

        string[] controls =
        [
            "TemperatureRow", "SustainedPowerRow", "BurstPowerRow", "FrequencyRow",
            "CurveOptimizerRow", "BoostToggle", "CpuBoundary", "PresetToolbar"
        ];
        foreach (string control in controls)
            StringAssert.Contains(workspace, $"x:Name=\"{control}\"");

        StringAssert.Contains(workspace, "Background=\"{StaticResource PerformanceV2CardBrush}\"");
        StringAssert.Contains(workspace, "BorderBrush=\"{StaticResource PerformanceV2CardBorderBrush}\"");
        StringAssert.Contains(workspace, "CornerRadius=\"{StaticResource PerformanceV2CardCornerRadius}\"");
        StringAssert.Contains(workspace, "x:Name=\"PerformancePageScrollViewer\"");
        StringAssert.Contains(workspace, "VerticalScrollBarVisibility=\"Hidden\"");
        StringAssert.Contains(workspace, "x:Name=\"AdvancedButton\" Grid.Row=\"3\" Style=\"{StaticResource PerformanceV2AdvancedButtonStyle}\"");
        StringAssert.Contains(workspace, "x:Name=\"AdvancedWorkspace\" Grid.Row=\"5\"");
        string code = File.ReadAllText(Path.Combine(
            root, "src", "Jiaolong.ControlCenter", "Prototype", "Controls", "PerformanceWorkspaceV2.xaml.cs"));
        Assert.IsFalse(code.Contains("DefaultContentGrid.Visibility", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("AdvancedButton.Visibility", StringComparison.Ordinal));
        Assert.IsFalse(workspace.Contains("Margin=\"-", StringComparison.Ordinal));
        Assert.IsFalse(workspace.Contains("参数说明与安全边界", StringComparison.Ordinal));
        Assert.IsFalse(workspace.Contains("OffContent=\"关闭\"", StringComparison.Ordinal));
        Assert.IsFalse(workspace.Contains("OnContent=\"开启\"", StringComparison.Ordinal));
        StringAssert.Contains(workspace, "Text=\"未连接\"");
        StringAssert.Contains(workspace, "Text=\"不可用\"");
    }

    [TestMethod]
    public void V2_slider_and_help_use_recommendation_and_hover_contracts()
    {
        string root = FindRepositoryRoot();
        string rail = File.ReadAllText(Path.Combine(root, "src", "Jiaolong.ControlCenter", "Controls", "PerformanceTuningRailV2.xaml"));
        string help = File.ReadAllText(Path.Combine(root, "src", "Jiaolong.ControlCenter", "Controls", "ParameterHelpButton.xaml"));

        StringAssert.Contains(rail, "x:Name=\"RecommendedMarker\"");
        Assert.IsTrue(
            rail.IndexOf("x:Name=\"RecommendedMarker\"", StringComparison.Ordinal) <
            rail.IndexOf("x:Name=\"CurrentThumb\"", StringComparison.Ordinal));
        Assert.IsFalse(rail.Contains("LimitMarker", StringComparison.Ordinal));
        Assert.IsFalse(rail.Contains("CurrentGlow", StringComparison.Ordinal));
        Assert.IsFalse(rail.Contains("FocusRing", StringComparison.Ordinal));
        var helpXaml = XDocument.Parse(help);
        var tooltip = helpXaml.Descendants().Single(element => element.Name.LocalName == "ToolTip");
        Assert.AreEqual("Bottom", (string?)tooltip.Attribute("Placement"));
        Assert.AreEqual("340", (string?)tooltip.Attribute("Width"));
        Assert.AreEqual("340", (string?)tooltip.Attribute("MaxWidth"));
        Assert.AreEqual("14", (string?)tooltip.Attribute("CornerRadius"));
        Assert.IsTrue(helpXaml.Descendants().Any(element => element.Name.LocalName == "SystemBackdropElement"));
        Assert.IsFalse(help.Contains("Flyout", StringComparison.Ordinal));
        Assert.IsFalse(help.Contains("PointerEntered", StringComparison.Ordinal));
    }

    [TestMethod]
    public void V2_core_status_is_read_only_complete_and_future_ready()
    {
        string workspace = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "Jiaolong.ControlCenter", "Prototype", "Controls", "PerformanceWorkspaceV2.xaml"));
        string code = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "Jiaolong.ControlCenter", "Prototype", "Controls", "PerformanceWorkspaceV2.xaml.cs"));

        StringAssert.Contains(workspace, "x:Name=\"CpuBoundary\"");
        StringAssert.Contains(workspace, "x:Name=\"BoostToggle\"");
        StringAssert.Contains(workspace, "x:Name=\"AdvancedWorkspace\"");
        StringAssert.Contains(workspace, "Text=\"未连接\"");
        StringAssert.Contains(workspace, "Text=\"不可用\"");
        Assert.IsFalse(workspace.Contains("x:Name=\"CoreTopology\"", StringComparison.Ordinal));
        Assert.IsFalse(workspace.Contains("x:Name=\"LogicalProcessorValueText\"", StringComparison.Ordinal));
        StringAssert.Contains(code, "UpdateCoreTopology(state?.EnabledCoreCount)");
        StringAssert.Contains(code, "AdvancedWorkspace.SetCoreCount(enabledCores)");
        StringAssert.Contains(code, "ApplyPresetRecommendationsAsync");
        StringAssert.Contains(code, "DefaultPerformancePresets.CreateDraft(key)");
        StringAssert.Contains(code, "SetRecommendedValueAnimatedAsync");
        StringAssert.Contains(workspace, "Title=\"曲线优化\"");
        StringAssert.Contains(workspace, "使用已保存的性能预设后才提交");
        StringAssert.Contains(code, "OnSavePresetClick");
        StringAssert.Contains(code, "OnUsePresetClick");
    }

    [TestMethod]
    public void Advanced_cpu_header_uses_vertical_summary_separators()
    {
        string workspace = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "Jiaolong.ControlCenter", "Prototype", "Controls", "PerformanceWorkspaceV2.xaml"));
        Assert.AreEqual(2, workspace.Split("Text=\"|\"").Length - 1);
        Assert.IsFalse(workspace.Contains("Text=\"未展开\"", StringComparison.Ordinal));
        StringAssert.Contains(workspace, "x:Name=\"AdvancedStateText\" Visibility=\"Collapsed\"");
    }

    [TestMethod]
    public void Preset_toolbar_uses_one_animated_status_that_hides_two_seconds_after_save()
    {
        string root = FindRepositoryRoot();
        string workspace = File.ReadAllText(Path.Combine(root, "src", "Jiaolong.ControlCenter", "Prototype", "Controls", "PerformanceWorkspaceV2.xaml"));
        string code = File.ReadAllText(Path.Combine(root, "src", "Jiaolong.ControlCenter", "Prototype", "Controls", "PerformanceWorkspaceV2.xaml.cs"));
        string toolbar = File.ReadAllText(Path.Combine(root, "src", "Jiaolong.ControlCenter", "Controls", "PagePresetToolbar.xaml"));
        string toolbarCode = File.ReadAllText(Path.Combine(root, "src", "Jiaolong.ControlCenter", "Controls", "PagePresetToolbar.xaml.cs"));

        Assert.IsFalse(workspace.Contains("UnsavedStatePanel", StringComparison.Ordinal));
        Assert.IsFalse(workspace.Contains("FeedbackText", StringComparison.Ordinal));
        StringAssert.Contains(workspace, "shared:PagePresetToolbar");
        StringAssert.Contains(toolbar, "x:Name=\"PresetStatusHost\"");
        StringAssert.Contains(toolbar, "x:Name=\"PresetStatusText\"");
        StringAssert.Contains(toolbarCode, "ShowSavedStatusAsync");
        StringAssert.Contains(toolbarCode, "HideStatusAsync");
        StringAssert.Contains(toolbarCode, "TimeSpan.FromSeconds(2)");
        StringAssert.Contains(toolbarCode, "statusVersion");
        StringAssert.Contains(toolbarCode, "TransitionAsync");
        StringAssert.Contains(toolbarCode, "Storyboard");
        StringAssert.Contains(toolbarCode, "AddAnimation");
        StringAssert.Contains(toolbarCode, "PresetStatusHost, \"Opacity\"");
        StringAssert.Contains(toolbarCode, "PresetPickerTranslation, \"X\"");
        Assert.AreEqual(1, toolbar.Split("有未保存的更改").Length - 1);
        Assert.IsFalse(code.Contains("此槽位尚未保存", StringComparison.Ordinal));
    }

    [TestMethod]
    public void V2_boundary_and_power_plan_use_live_editor_contracts()
    {
        string root = FindRepositoryRoot();
        string boundary = File.ReadAllText(Path.Combine(root, "src", "Jiaolong.ControlCenter", "Controls", "CpuOperatingBoundaryV2.xaml"));
        string markup = File.ReadAllText(Path.Combine(root, "src", "Jiaolong.ControlCenter", "Prototype", "Controls", "PerformanceWorkspaceV2.xaml"));
        string workspace = File.ReadAllText(Path.Combine(root, "src", "Jiaolong.ControlCenter", "Prototype", "Controls", "PerformanceWorkspaceV2.xaml.cs"));
        string reader = File.ReadAllText(Path.Combine(root, "src", "Jiaolong.ControlCenter", "Services", "WindowsPowerSchemeReader.cs"));

        Assert.IsFalse(boundary.Contains("GlowPath", StringComparison.Ordinal));
        StringAssert.Contains(workspace, "RefreshBoundaryFromEditor()");
        Assert.IsFalse(workspace.Contains("$\"方案 {scheme:N}\"", StringComparison.Ordinal));
        StringAssert.Contains(markup, "x:Name=\"PowerPlanSelector\"");
        StringAssert.Contains(markup, "SelectionChanged=\"OnPowerPlanSelectionChanged\"");
        StringAssert.Contains(workspace, "WindowsPowerSchemeReader.ReadSchemes");
        StringAssert.Contains(workspace, "windowsPowerSchemeDraftId = selectedScheme");
        StringAssert.Contains(workspace, "presetStore.LoadAsync(ControlPageId.Performance, key");
        StringAssert.Contains(workspace, "requireComplete: true");
        StringAssert.Contains(workspace, "if (beforeApply is not null && !await beforeApply())");
        StringAssert.Contains(workspace, "if (!dirty && selectedSavedDraft is null)");
        int selectionStart = workspace.IndexOf("private void OnPowerPlanSelectionChanged", StringComparison.Ordinal);
        int selectionEnd = workspace.IndexOf("private void SetInteractionAvailability", selectionStart, StringComparison.Ordinal);
        Assert.IsTrue(selectionStart >= 0 && selectionEnd > selectionStart);
        Assert.IsFalse(workspace[selectionStart..selectionEnd].Contains("ExecuteAsync", StringComparison.Ordinal));
        Assert.IsFalse(reader.Contains("PowerSetActiveScheme", StringComparison.Ordinal));
    }

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src", "Jiaolong.ControlCenter")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException("找不到仓库根目录。");
    }
}
