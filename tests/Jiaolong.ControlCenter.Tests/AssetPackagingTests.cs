using System.Xml.Linq;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class AssetPackagingTests
{
    [TestMethod]
    public void Svg_icon_assets_are_copied_to_build_and_publish_outputs()
    {
        var project = XDocument.Load(SourcePath("src", "Jiaolong.ControlCenter", "Jiaolong.ControlCenter.csproj"));
        var iconContent = project
            .Descendants("Content")
            .Single(element => string.Equals(
                (string?)element.Attribute("Include"),
                @"Assets\Icons\*.svg",
                StringComparison.OrdinalIgnoreCase));

        Assert.AreEqual("PreserveNewest", (string?)iconContent.Attribute("CopyToOutputDirectory"));
        Assert.AreEqual("PreserveNewest", (string?)iconContent.Attribute("CopyToPublishDirectory"));
    }

    [TestMethod]
    public void Brand_assets_are_copied_to_build_and_publish_outputs()
    {
        var project = XDocument.Load(SourcePath("src", "Jiaolong.ControlCenter", "Jiaolong.ControlCenter.csproj"));
        foreach (var include in new[]
        {
            @"Assets\Brand\JiaolongMark.svg",
            @"Assets\Brand\MechrevoJiaolongWordmark.svg",
            @"Assets\Brand\MechrevoTechWordmark.svg",
        })
        {
            var content = project
                .Descendants("Content")
                .Single(element => string.Equals(
                    (string?)element.Attribute("Include"),
                    include,
                    StringComparison.OrdinalIgnoreCase));

            Assert.AreEqual("PreserveNewest", (string?)content.Attribute("CopyToOutputDirectory"), include);
            Assert.AreEqual("PreserveNewest", (string?)content.Attribute("CopyToPublishDirectory"), include);
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
