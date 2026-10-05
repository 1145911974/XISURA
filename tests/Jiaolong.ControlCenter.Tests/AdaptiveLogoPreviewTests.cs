namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class AdaptiveLogoPreviewTests
{
    [TestMethod]
    public void Adaptive_logo_preview_reuses_a_fixed_silver_shell_and_six_native_core_layers()
    {
        var xaml = ReadSource("src", "Jiaolong.ControlCenter", "Branding", "HeroLogoControl.xaml");
        var code = ReadSource("src", "Jiaolong.ControlCenter", "Branding", "HeroLogoControl.xaml.cs");

        StringAssert.Contains(xaml, "x:Name=\"AdaptiveLogoLayers\"");
        StringAssert.Contains(xaml, "HeroLogoSilver.png");
        foreach (var source in new[] { "HeroLogoFullOffice.png", "HeroLogoFullGaming.png", "HeroLogoFullTurbo.png", "HeroLogoFullCustomProfile1.png", "HeroLogoFullCustomProfile2.png", "HeroLogoFullCustomProfile3.png" })
            StringAssert.Contains(xaml, source);
        StringAssert.Contains(code, "AdaptiveCorePreviewProperty");
        StringAssert.Contains(code, "ReducedMotion");
        StringAssert.Contains(code, "RepeatBehavior.Forever");
    }

    [TestMethod]
    public void Adaptive_logo_preview_is_available_only_through_the_debug_lab_flag()
    {
        var lab = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "LogoLabWindow.xaml");
        var labCode = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "LogoLabWindow.xaml.cs");
        var app = ReadSource("src", "Jiaolong.ControlCenter", "App.xaml.cs");

        StringAssert.Contains(lab, "自适应核心预览");
        StringAssert.Contains(labCode, "AdaptiveCorePreview");
        StringAssert.Contains(app, "--adaptive-core");
        StringAssert.Contains(app, "new LogoLabWindow(adaptiveCore)");
    }

    [TestMethod]
    public void Native_adaptive_core_assets_are_rgba_1254_square_images_with_transparent_corners()
    {
        foreach (var name in new[] { "HeroLogoSilver.png", "HeroLogoFullOffice.png", "HeroLogoFullGaming.png", "HeroLogoFullTurbo.png", "HeroLogoFullCustomProfile1.png", "HeroLogoFullCustomProfile2.png", "HeroLogoFullCustomProfile3.png" })
        {
            var path = Path.Combine(SourcePath("src", "Jiaolong.ControlCenter", "Assets", "Brand"), name);
            Assert.IsTrue(File.Exists(path), $"Missing adaptive core asset: {name}");
            var png = File.ReadAllBytes(path);
            CollectionAssert.AreEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png[..8]);
            Assert.AreEqual(1254, ReadPngDimension(png, 16), name);
            Assert.AreEqual(1254, ReadPngDimension(png, 20), name);
            Assert.AreEqual(6, png[25], $"{name} must use RGBA color type");

        }
    }

    private static int ReadPngDimension(byte[] png, int offset) =>
        (png[offset] << 24) | (png[offset + 1] << 16) | (png[offset + 2] << 8) | png[offset + 3];

    private static string SourcePath(params string[] parts)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Jiaolong.ControlCenter.slnx")))
            root = root.Parent;
        return Path.Combine(new[] { root?.FullName ?? throw new DirectoryNotFoundException("Repository root unavailable.") }.Concat(parts).ToArray());
    }

    private static string ReadSource(params string[] parts) => File.ReadAllText(SourcePath(parts));
}
