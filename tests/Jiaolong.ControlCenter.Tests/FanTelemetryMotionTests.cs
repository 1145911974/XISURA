namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class FanTelemetryMotionTests
{
    [TestMethod]
    public void Fan_readouts_use_smoothed_frame_values_without_fabricating_unknown_rpm()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        const string path = "src/Jiaolong.ControlCenter/Prototype/Controls/FanWorkspaceV2.xaml.cs";
        while (root is not null && !File.Exists(Path.Combine(root.FullName, path))) root = root.Parent;
        Assert.IsNotNull(root);
        var source = File.ReadAllText(Path.Combine(root.FullName, path));
        StringAssert.Contains(source, "fanTemperatures[0].SetTarget");
        StringAssert.Contains(source, "targetCpuRpm.HasValue ? cpuRpm : null");
        StringAssert.Contains(source, "targetGpuRpm.HasValue ? gpuRpm : null");
        StringAssert.Contains(source, "{rpm:0} RPM");
        var xaml = File.ReadAllText(Path.Combine(root.FullName, path.Replace(".xaml.cs", ".xaml")));
        StringAssert.Contains(xaml, "Typography.NumeralAlignment=\"Tabular\"");
        Assert.IsFalse(xaml.Contains("CornerRadius=\"5\""));
        Assert.IsFalse(source.Contains("CpuRpmText.Text = Format(snapshot.CpuFanRpm"));
    }
}
