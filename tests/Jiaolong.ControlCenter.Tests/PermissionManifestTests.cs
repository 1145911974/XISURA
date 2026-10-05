using System.Xml.Linq;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class PermissionManifestTests
{
    [TestMethod]
    public void Debug_build_runs_without_elevation_while_release_keeps_hardware_permission()
    {
        string project = File.ReadAllText(SourcePath("src", "Jiaolong.ControlCenter", "Jiaolong.ControlCenter.csproj"));
        var debugManifest = XDocument.Load(SourcePath("src", "Jiaolong.ControlCenter", "app.debug.manifest"));
        XNamespace security = "urn:schemas-microsoft-com:asm.v3";

        StringAssert.Contains(project, "Condition=\"'$(Configuration)' == 'Debug'\"");
        StringAssert.Contains(project, "<ApplicationManifest>app.debug.manifest</ApplicationManifest>");
        Assert.AreEqual("asInvoker", debugManifest.Descendants(security + "requestedExecutionLevel").Single().Attribute("level")?.Value);
    }

    [TestMethod]
    public void Control_center_requests_administrator_token()
    {
        var manifest = XDocument.Load(SourcePath("src", "Jiaolong.ControlCenter", "app.manifest"));
        XNamespace security = "urn:schemas-microsoft-com:asm.v3";
        var requestedLevel = manifest
            .Descendants(security + "requestedExecutionLevel")
            .Single()
            .Attribute("level")
            ?.Value;

        Assert.AreEqual("requireAdministrator", requestedLevel);
        Assert.AreEqual("false", manifest
            .Descendants(security + "requestedExecutionLevel")
            .Single()
            .Attribute("uiAccess")
            ?.Value);
        Assert.IsNotNull(manifest.Descendants(security + "trustInfo").SingleOrDefault());
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
