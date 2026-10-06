namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class AuditRemediationTests
{
    [TestMethod]
    public void Unknown_gpu_readback_keeps_a_target_editor_without_claiming_a_live_value()
    {
        string code = Read("Prototype/Controls/GpuWorkspaceV2.xaml.cs");
        string markup = Read("Prototype/Controls/GpuWorkspaceV2.xaml");
        Assert.IsFalse(code.Contains("CoreRail.SetValue(null)", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("CoreValueBox.Visibility = clockDirty", StringComparison.Ordinal));
        StringAssert.Contains(markup, "目标值 · 当前限制未读回");
        StringAssert.Contains(code, "MaximumMhz: > 0");
    }

    [TestMethod]
    public void Saved_performance_presets_refresh_the_adaptive_service_configuration()
    {
        string code = Read("Prototype/PrototypeWindow.Branding.cs");
        string handler = code[code.IndexOf("private void InvalidateSavedPerformancePreset", StringComparison.Ordinal)..];
        handler = handler[..handler.IndexOf("private (string Name", StringComparison.Ordinal)];
        StringAssert.Contains(handler, "SetAutomaticPresetReader(PerformanceWorkspace.ReadAutomaticServicePresetsAsync)");
    }

    [TestMethod]
    public void Fan_editing_and_help_remain_available_when_hardware_application_is_unavailable()
    {
        string code = Read("Prototype/Controls/FanWorkspaceV2.xaml.cs");
        string live = Read("Prototype/Controls/FanWorkspaceV2.FollowPreset.cs");
        string curve = Read("Prototype/Controls/FanCurveWorkspaceV2.xaml.cs");
        StringAssert.Contains(code, "UpdateFanPresetAvailability()");
        StringAssert.Contains(code, "HomeSessionStatus.Connected");
        Assert.IsFalse(code.Contains("SetActionAvailability(true, true)", StringComparison.Ordinal));
        Assert.IsFalse(live.Contains("FanCurveWorkspace.IsEnabled = curve", StringComparison.Ordinal));
        StringAssert.Contains(live, "FanCurveWorkspace.SetEditingEnabled(curve");
        StringAssert.Contains(curve, "TargetBox.IsEnabled = enabled");
        Assert.IsFalse(curve.Contains("CurveHelp.IsEnabled", StringComparison.Ordinal));
    }

    private static string Read(string relativePath)
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            var file = Path.Combine(folder.FullName, "src", "Jiaolong.ControlCenter", relativePath);
            if (File.Exists(file)) return File.ReadAllText(file);
        }
        throw new FileNotFoundException(relativePath);
    }
}
