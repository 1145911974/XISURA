namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class GpuTelemetryMotionTests
{
    [TestMethod]
    public void Telemetry_animates_monitor_without_mutating_frequency_draft()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src/Jiaolong.ControlCenter/Prototype/Controls/GpuWorkspaceV2.xaml.cs"))) root = root.Parent;
        Assert.IsNotNull(root);
        var code = File.ReadAllText(Path.Combine(root.FullName, "src/Jiaolong.ControlCenter/Prototype/Controls/GpuWorkspaceV2.xaml.cs"));
        var methodStart = code.IndexOf("public void ApplyTelemetry(", StringComparison.Ordinal);
        var methodEnd = code.IndexOf("public void ", methodStart + 1, StringComparison.Ordinal);
        var method = code[methodStart..methodEnd];
        StringAssert.Contains(method, "ApplyMonitorTargets(snapshot)");
        Assert.IsFalse(method.Contains("CoreRail.SetValue"));
        Assert.IsFalse(method.Contains("CoreValueBox.Value ="));
        StringAssert.Contains(code, "StopMonitorMotion()");
    }
}
