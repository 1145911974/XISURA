namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class FanStrongCoolingWiringTests
{
    [TestMethod]
    public void Fan_button_routes_to_shared_command_and_readback_not_a_local_preview()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "src", "Jiaolong.ControlCenter"))) root = root.Parent;
        Assert.IsNotNull(root);
        string Read(string path) => File.ReadAllText(Path.Combine(root.FullName, "src", "Jiaolong.ControlCenter", path));
        var window = Read("Prototype/PrototypeWindow.xaml.cs");
        var workspace = Read("Prototype/Controls/PrototypePageWorkspace.xaml.cs");
        var fan = Read("Prototype/Controls/FanWorkspaceV2.xaml.cs");
        StringAssert.Contains(window, "PageWorkspace.StrongCoolingRequested += OnStrongCoolingChanged");
        StringAssert.Contains(window, "private void ApplySharedQuickSettingState");
        StringAssert.Contains(window, "PageWorkspace.ApplyStrongCoolingState(enabled, available)");
        StringAssert.Contains(window, "PageWorkspace.ApplyLidLogoState(enabled, available)");
        StringAssert.Contains(window, "PageWorkspace.LidLogoRequested += enabled => OnQuickSettingChanged(QuickSettingKind.LidLogo, enabled)");
        StringAssert.Contains(workspace, "FanWorkspace.StrongCoolingRequested");
        StringAssert.Contains(fan, "StrongCoolingRequested?.Invoke");
        StringAssert.Contains(fan, "if (syncingStrongCooling) return");
        Assert.IsFalse(fan.Contains("Temporary UI override"));
    }
}
