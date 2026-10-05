namespace Jiaolong.ControlCenter.Tests;

using Jiaolong_ControlCenter.Prototype.Controls;

[TestClass]
public sealed class FanWorkspaceV2ContractTests
{
    [TestMethod]
    public void Fan_route_mounts_the_airflow_workspace_and_hides_the_legacy_draft()
    {
        string host = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "PrototypePageWorkspace.xaml");

        StringAssert.Contains(host, "<local:FanWorkspaceV2 x:Name=\"FanWorkspace\"");
        StringAssert.Contains(host, "<Grid x:Name=\"LegacyFanHeader\" Grid.Row=\"0\" ColumnSpacing=\"14\" Visibility=\"Collapsed\"");
        StringAssert.Contains(host, "<ScrollViewer x:Name=\"LegacyFanContent\" Grid.Row=\"1\"");
        StringAssert.Contains(host, "HorizontalScrollBarVisibility=\"Disabled\" Visibility=\"Collapsed\"");
    }

    [TestMethod]
    public void Fan_motion_maps_visual_speed_and_smooths_interruptibly()
    {
        double previous = 0;
        for (int rpm = 250; rpm <= 7000; rpm += 250)
        {
            double speed = FanMotionMath.MapRpmToRevolutionsPerSecond(rpm);
            Assert.IsTrue(speed > previous);
            Assert.IsTrue(speed - previous < .2);
            previous = speed;
        }
        Assert.AreEqual(0d, FanMotionMath.MapRpmToRevolutionsPerSecond(null));
        Assert.AreEqual(0d, FanMotionMath.MapRpmToRevolutionsPerSecond(double.NaN));
        Assert.AreEqual(FanMotionMath.MapRpmToRevolutionsPerSecond(7000), FanMotionMath.MapRpmToRevolutionsPerSecond(9000));

        Assert.AreEqual(1d, FanMotionMath.NormalizeFanStrength(9000), .0001);
        Assert.AreEqual(0d, FanMotionMath.NormalizeFanStrength(null), .0001);
        double first = FanMotionMath.SmoothToward(0, 1, .1, .55);
        double second = FanMotionMath.SmoothToward(first, 1, .1, .55);
        Assert.IsTrue(first is > 0 and < 1);
        Assert.IsTrue(second > first && second < 1);
    }

    [TestMethod]
    public void Fan_page_keeps_unknown_telemetry_and_defers_hardware_writes()
    {
        string xaml = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "FanWorkspaceV2.xaml");
        string code = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "FanWorkspaceV2.xaml.cs");
        StringAssert.Contains(xaml, "-- RPM");
        StringAssert.Contains(xaml, "-- °C");
        Assert.IsFalse(xaml.Contains("安全保护", StringComparison.Ordinal));
        StringAssert.Contains(xaml, "7000");
        Assert.IsFalse(xaml.Contains("FanAirflowCanvas", StringComparison.Ordinal));
        StringAssert.Contains(code, "SmoothToward");
        StringAssert.Contains(code, "AnimationsEnabled");
        Assert.IsFalse(code.Contains("SendCommandAsync", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Fan_curve_preset_cannot_be_applied_without_a_hardware_transport()
    {
        string xaml = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "FanWorkspaceV2.xaml");
        string code = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "FanWorkspaceV2.xaml.cs");
        string curve = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "FanCurveWorkspaceV2.xaml.cs");

        StringAssert.Contains(xaml, "FanCurveWorkspaceV2");
        StringAssert.Contains(curve, "不是硬件安全保证");
        StringAssert.Contains(code, "FanCurveSafety.Assess(state)");
        StringAssert.Contains(code, "PresetToolbar.UseRequested += OnUsePreset");
        StringAssert.Contains(code, "new SetFanControlCommand");
        StringAssert.Contains(code, "RiskConfirmed: true");
        StringAssert.Contains(code, "session.ExecuteAsync(command");
        string use = code[code.IndexOf("private async void OnUsePreset", StringComparison.Ordinal)..];
        StringAssert.Contains(use, "if (warnings.Count > 0 && await dialog.ShowAsync()");
        int confirmation = use.IndexOf("await dialog.ShowAsync() != ContentDialogResult.Primary", StringComparison.Ordinal);
        Assert.IsTrue(confirmation >= 0 && confirmation < use.IndexOf("session.ExecuteAsync(command", StringComparison.Ordinal));
        StringAssert.Contains(use, "DefaultButton = ContentDialogButton.Close");
        StringAssert.Contains(use, "bool succeeded = result.State == CommandState.Applied && result.Error is null;");
        StringAssert.Contains(use, "UpdateFanActiveBadge(confirmed);");
    }

    private static string ReadSource(params string[] parts) => File.ReadAllText(Path.Combine([FindRepositoryRoot(), .. parts]));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
