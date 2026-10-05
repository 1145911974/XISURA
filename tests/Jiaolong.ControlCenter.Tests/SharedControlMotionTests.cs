using System.Xml.Linq;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class SharedControlMotionTests
{
    [TestMethod]
    public void Glass_and_advanced_templates_transition_their_existing_opacity_layers()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src/Jiaolong.ControlCenter/Prototype/PerformanceV2Resources.xaml"))) root = root.Parent;
        Assert.IsNotNull(root);
        var document = XDocument.Load(Path.Combine(root.FullName, "src/Jiaolong.ControlCenter/Prototype/PerformanceV2Resources.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XNamespace ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        foreach (string key in new[] { "ControlGlassButtonTemplate", "PerformanceV2AdvancedButtonStyle" })
        {
            var resource = document.Root!.Elements().Single(element => (string?)element.Attribute(x + "Key") == key);
            var common = resource.Descendants(ui + "VisualStateGroup").Single(element => (string?)element.Attribute(x + "Name") == "CommonStates");
            var transition = common.Element(ui + "VisualStateGroup.Transitions")?.Element(ui + "VisualTransition");
            Assert.IsNotNull(transition, key);
            Assert.AreEqual("{ThemeResource ControlStateTransitionDuration}", (string?)transition.Attribute("GeneratedDuration"));
            Assert.IsTrue(common.Descendants(ui + "Setter").All(setter => ((string?)setter.Attribute("Target"))?.EndsWith(".Opacity") == true));
        }
        string input = File.ReadAllText(Path.Combine(root.FullName, "src/Jiaolong.ControlCenter/Controls/PerformanceParameterRowV2.xaml"));
        string inputCode = File.ReadAllText(Path.Combine(root.FullName, "src/Jiaolong.ControlCenter/Controls/PerformanceParameterRowV2.xaml.cs"));
        StringAssert.Contains(input, "TextControlForegroundDisabled");
        StringAssert.Contains(input, "TextControlBackgroundDisabled");
        StringAssert.Contains(input, "TextControlBorderBrushDisabled");
        StringAssert.Contains(inputCode, "ControlStateTransitionDuration");
        StringAssert.Contains(inputCode, "editorAvailabilityStoryboard?.Stop()");
        var toolbar = File.ReadAllText(Path.Combine(root.FullName, "src/Jiaolong.ControlCenter/Controls/PagePresetToolbar.xaml.cs"));
        StringAssert.Contains(toolbar, "await TransitionAsync(.25, PresetPickerTranslation.X)");
        StringAssert.Contains(toolbar, "if (version != statusVersion) return;");
    }
}
