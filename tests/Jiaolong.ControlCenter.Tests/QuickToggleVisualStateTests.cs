using System.Xml.Linq;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class QuickToggleVisualStateTests
{
    [TestMethod]
    public void Quick_tiles_have_only_off_and_on_states_with_stable_translucent_active_material()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var document = XDocument.Load(SourcePath(
            "src", "Jiaolong.ControlCenter", "Prototype", "PrototypeThemeResources.xaml"));
        var style = document.Descendants(p + "Style")
            .Single(element => (string?)element.Attribute(x + "Key") == "PrototypeQuickToggleStyle");
        var checkedState = style.Descendants(p + "VisualState")
            .Single(state => (string?)state.Attribute(x + "Name") == "Checked");
        var checkStates = style.Descendants(p + "VisualStateGroup")
            .Single(group => (string?)group.Attribute(x + "Name") == "CheckStates");
        var commonStates = style.Descendants(p + "VisualStateGroup")
            .Single(group => (string?)group.Attribute(x + "Name") == "CommonStates")
            .Descendants(p + "VisualState")
            .ToDictionary(state => (string)state.Attribute(x + "Name")!);

        Assert.IsNotNull(document.Descendants().SingleOrDefault(element =>
            (string?)element.Attribute(x + "Key") == "ModeQuickActiveSurfaceBrush"));
        Assert.IsNotNull(document.Descendants().SingleOrDefault(element =>
            (string?)element.Attribute(x + "Key") == "ModeQuickActiveTintBrush"));
        Assert.IsFalse(style.Descendants(p + "VisualState").Any(state =>
            (string?)state.Attribute(x + "Name") is "Indeterminate" or "Disabled"));
        CollectionAssert.AreEquivalent(
            new[] { "Unchecked", "Checked" },
            checkStates.Descendants(p + "VisualState")
                .Select(state => (string)state.Attribute(x + "Name")!)
                .ToArray());
        Assert.AreEqual(
            "False",
            (string?)style.Descendants(p + "Setter").Single(setter =>
                (string?)setter.Attribute("Property") == "IsThreeState").Attribute("Value"));
        Assert.AreEqual(
            "#32000000",
            (string?)document.Descendants().Single(element =>
                (string?)element.Attribute(x + "Key") == "ModeQuickActiveTintBrush").Attribute("Color"));
        Assert.AreEqual(
            "{StaticResource ModeQuickActiveTintBrush}",
            (string?)style.Descendants().Single(element =>
                (string?)element.Attribute(x + "Name") == "QuickTint").Attribute("Background"));
        Assert.AreEqual(
            "{StaticResource ModeQuickActiveSurfaceBrush}",
            (string?)checkedState.Descendants(p + "Setter").Single(setter =>
                (string?)setter.Attribute("Target") == "QuickRoot.Background").Attribute("Value"));
        Assert.AreEqual(
            "1",
            (string?)checkedState.Descendants(p + "Setter").Single(setter =>
                (string?)setter.Attribute("Target") == "QuickTint.Opacity").Attribute("Value"));
        Assert.AreEqual(
            "0.96",
            (string?)checkedState.Descendants(p + "Setter").Single(setter =>
                (string?)setter.Attribute("Target") == "QuickGlow.Opacity").Attribute("Value"));

        foreach (var stateName in new[] { "Normal", "PointerOver", "Pressed" })
        {
            Assert.AreEqual(
                "1",
                (string?)commonStates[stateName].Descendants(p + "Setter").Single(setter =>
                    (string?)setter.Attribute("Target") == "QuickRoot.Opacity").Attribute("Value"));
        }
    }

    private static string SourcePath(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Jiaolong.ControlCenter.slnx")))
            directory = directory.Parent;

        return Path.Combine(new[]
        {
            directory?.FullName ?? throw new DirectoryNotFoundException("无法定位蛟龙仓库根目录。")
        }.Concat(parts).ToArray());
    }
}
