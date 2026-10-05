using System.IO;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class ModePresetPickerTests
{
    [TestMethod]
    public void Shared_controls_are_present_with_the_approved_public_contract()
    {
        var picker = ReadSource("src", "Jiaolong.ControlCenter", "Controls", "ModePresetPicker.xaml.cs");
        var help = ReadSource("src", "Jiaolong.ControlCenter", "Controls", "ParameterHelpButton.xaml.cs");
        var section = ReadSource("src", "Jiaolong.ControlCenter", "Controls", "ExpandableSection.xaml.cs");

        StringAssert.Contains(picker, "SelectedKeyProperty");
        StringAssert.Contains(picker, "SelectedKeyChanged");
        StringAssert.Contains(picker, "PresetKey.Create");
        StringAssert.Contains(help, "ParameterHelpContent");
        StringAssert.Contains(help, "CurrentValue");
        StringAssert.Contains(help, "RecommendedRange");
        StringAssert.Contains(section, "IsExpandedProperty");
        StringAssert.Contains(section, "SectionContentProperty");
    }

    [TestMethod]
    public void Preset_picker_uses_a_downward_two_column_panel_for_all_six_modes_and_three_slots()
    {
        var markup = ReadSource("src", "Jiaolong.ControlCenter", "Controls", "ModePresetPicker.xaml");
        var code = ReadSource("src", "Jiaolong.ControlCenter", "Controls", "ModePresetPicker.xaml.cs");

        StringAssert.Contains(markup, "x:Name=\"PresetFlyout\"");
        StringAssert.Contains(markup, "x:Name=\"ModeList\"");
        StringAssert.Contains(markup, "x:Name=\"SlotList\"");
        StringAssert.Contains(markup, "BottomEdgeAlignedLeft");
        Assert.AreEqual(6, CountOccurrences(markup, "Click=\"OnModeClick\""));
        Assert.AreEqual(3, CountOccurrences(markup, "Click=\"OnSlotClick\""));
        StringAssert.Contains(code, "ShowPicker");
        StringAssert.Contains(code, "IsDropDownOpen");
        StringAssert.Contains(code, "AutomationProperties.SetName");
        StringAssert.Contains(markup, "x:Name=\"PresetSurface\"");
        StringAssert.Contains(code, "PickerButton.XamlRoot?.Size.Width");
        StringAssert.Contains(code, "Math.Clamp(width * 0.36d, 470d, 510d)");
        StringAssert.Contains(code, "Math.Clamp(height * 0.32d, 330d, 350d)");
        StringAssert.Contains(markup, "Closing=\"OnFlyoutClosing\"");
        StringAssert.Contains(code, "AnimatePresetExitAsync");
        StringAssert.Contains(code, "SetActivePreset");
        StringAssert.Contains(code, "UpdateSlotStates");
        StringAssert.Contains(code, "confirmedActiveKey");
        Assert.IsFalse(code.Contains(".ToDictionary(mode => mode, _ => 1)", StringComparison.Ordinal), "Unverified presets must not start as active.");
        StringAssert.Contains(code, "confirmedActiveKey == PresetKey.Create");
        StringAssert.Contains(markup, "x:Name=\"Slot1EditingBadge\"");
        StringAssert.Contains(markup, "x:Name=\"Slot1UsingBadge\"");
        StringAssert.Contains(markup, "Text=\"正在编辑\"");
        StringAssert.Contains(markup, "Text=\"正在使用\"");
        StringAssert.Contains(markup, "Background=\"#C0363A43\"");
        StringAssert.Contains(markup, "Background=\"#C0208A55\"");
        StringAssert.Contains(markup, "<AcrylicBrush TintColor=\"#18181B\"");
        StringAssert.Contains(code, "PresetSurface.Opacity = 0d");
        StringAssert.Contains(code, "AnimatePresetEnterAsync");
        Assert.IsFalse(markup.Contains("MenuFlyout", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Parameter_help_is_a_constrained_downward_frosted_tooltip()
    {
        var markup = ReadSource("src", "Jiaolong.ControlCenter", "Controls", "ParameterHelpButton.xaml");

        StringAssert.Contains(markup, "ToolTip Placement=\"Bottom\"");
        StringAssert.Contains(markup, "CornerRadius=\"14\"");
        StringAssert.Contains(markup, "MaxHeight=\"360\"");
        StringAssert.Contains(markup, "CurrentValueText");
        StringAssert.Contains(markup, "RecommendedRangeText");
        StringAssert.Contains(markup, "ExtremeLimitText");
        StringAssert.Contains(markup, "RiskText");
    }

    [TestMethod]
    public void Expandable_section_uses_natural_downward_layout_and_keeps_content_in_place()
    {
        var markup = ReadSource("src", "Jiaolong.ControlCenter", "Controls", "ExpandableSection.xaml");

        StringAssert.Contains(markup, "Expander");
        StringAssert.Contains(markup, "ExpandDirection=\"Down\"");
        StringAssert.Contains(markup, "SectionContent");
        StringAssert.Contains(markup, "VerticalContentAlignment=\"Stretch\"");
        Assert.IsFalse(markup.Contains("Canvas.Top", StringComparison.Ordinal));
        Assert.IsFalse(markup.Contains("TranslateY", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Shared_theme_resources_define_the_frosted_control_primitives()
    {
        var theme = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "PrototypeThemeResources.xaml");

        StringAssert.Contains(theme, "ControlPresetPickerButtonStyle");
        StringAssert.Contains(theme, "ControlExpandableSectionStyle");
        var help = ReadSource("src", "Jiaolong.ControlCenter", "Controls", "ParameterHelpButton.xaml");
        StringAssert.Contains(help, "<SystemBackdropElement");
        StringAssert.Contains(help, "<DesktopAcrylicBackdrop");
    }

    private static string ReadSource(params string[] parts)
    {
        var root = FindRepositoryRoot();
        var path = Path.Combine(new[] { root }.Concat(parts).ToArray());
        return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
    }

    private static int CountOccurrences(string source, string value) =>
        source.Split(value, StringSplitOptions.None).Length - 1;

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Jiaolong.ControlCenter.slnx")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new DirectoryNotFoundException("无法定位蛟龙仓库根目录。");
    }
}
