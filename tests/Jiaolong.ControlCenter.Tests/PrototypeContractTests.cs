using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using Jiaolong_ControlCenter.Branding;
using Jiaolong_ControlCenter.Prototype;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class PrototypeContractTests
{
    [TestMethod]
    public void Performance_modes_use_the_approved_semantic_palette()
    {
        CollectionAssert.AreEqual(
            new[] { "#1677FF", "#FF8A1F", "#FF3141", "#9A5CFF" },
            PrototypeModePalette.All.Select(x => x.AccentHex).ToArray());
    }

    [TestMethod]
    public void Mode_themes_define_distinct_flow_anchors_and_gradual_motion()
    {
        CollectionAssert.AreEqual(
            new[] { (-12d, 0d), (4d, -4d), (18d, 2d), (-2d, 8d) },
            PrototypeModePalette.All.Select(x => (x.FlowX, x.FlowY)).ToArray());

        var standard = PrototypeMotionProfile.Resolve(reducedMotion: false);
        var reduced = PrototypeMotionProfile.Resolve(reducedMotion: true);

        Assert.AreEqual(TimeSpan.FromMilliseconds(260), standard.Mode);
        Assert.AreEqual(TimeSpan.FromMilliseconds(190), standard.Page);
        Assert.AreEqual(TimeSpan.FromMilliseconds(120), standard.Control);
        Assert.AreEqual(TimeSpan.FromMilliseconds(80), standard.IndicatorExit);
        Assert.AreEqual(TimeSpan.FromMilliseconds(100), standard.IndicatorEnter);
        Assert.AreEqual(0, reduced.Translation);
    }

    [TestMethod]
    public void Mode_transition_plan_uses_target_theme_for_brushes_and_flow()
    {
        var office = ModeVisualCatalog.All.Single(scene => scene.Kind == ModeSceneKind.OrbitalFocus);
        var turbo = ModeVisualCatalog.All.Single(scene => scene.Kind == ModeSceneKind.DigitalFaultPlanes);
        var plan = new ModeTransitionController().Begin(office, turbo, PrototypeMotionProfile.Resolve(reducedMotion: false));

        Assert.AreSame(office, plan.Current);
        Assert.AreSame(turbo, plan.Target);
        Assert.AreEqual(turbo.Theme.FlowX, plan.Target.Theme.FlowX);
        Assert.AreEqual(turbo.Theme.FlowY, plan.Target.Theme.FlowY);
        CollectionAssert.AreEqual(
            new[]
            {
                ("ModeAccentBrush", turbo.Theme.AccentHex, (byte)0xFF),
                ("ModeQuickActiveTintBrush", turbo.Theme.AccentHex, (byte)0x32),
                ("ModeGlowBrush", turbo.Theme.GlowHex, (byte)0xFF),
                ("ModeDeepBrush", turbo.Theme.DeepHex, (byte)0xFF),
                ("ModeSelectionAccentBrush", turbo.Theme.AccentHex, (byte)0x72),
                ("ModeNavSelectionBrush", turbo.Theme.AccentHex, (byte)0x4A),
                ("ModePanelBrush", turbo.Theme.AccentHex, (byte)0x24),
                ("ModeAccentFadeBrush", turbo.Theme.AccentHex, (byte)0x00),
                ("ModeTraceTailBrush", turbo.Theme.AccentHex, (byte)0x55),
            },
            plan.BrushTargets.Select(target => (target.Key, target.Hex, target.Alpha)).ToArray());
    }

    [TestMethod]
    public void Latest_transition_wins_and_reduced_motion_snaps_to_target()
    {
        var coordinator = new ModeTransitionController();
        var office = ModeVisualCatalog.All.Single(scene => scene.Kind == ModeSceneKind.OrbitalFocus);
        var gaming = ModeVisualCatalog.All.Single(scene => scene.Kind == ModeSceneKind.FacetArena);
        var custom = ModeVisualCatalog.ForCustomProfile("Profile3");
        var first = coordinator.Begin(office, gaming, PrototypeMotionProfile.Resolve(reducedMotion: false));
        var latest = coordinator.Begin(gaming, custom, PrototypeMotionProfile.Resolve(reducedMotion: true));

        Assert.IsFalse(coordinator.IsCurrent(first.Version));
        Assert.IsTrue(coordinator.IsCurrent(latest.Version));
        Assert.IsTrue(latest.Snap);
        Assert.AreEqual(TimeSpan.Zero, latest.Duration);
        Assert.AreSame(custom, latest.Target);
        Assert.AreEqual(custom.Theme.FlowX, latest.Target.Theme.FlowX);
        Assert.AreEqual(custom.Theme.FlowY, latest.Target.Theme.FlowY);
    }

    [TestMethod]
    public void Reduced_motion_plan_does_not_start_mode_visual_storyboard()
    {
        var office = ModeVisualCatalog.All.Single(scene => scene.Kind == ModeSceneKind.OrbitalFocus);
        var gaming = ModeVisualCatalog.All.Single(scene => scene.Kind == ModeSceneKind.FacetArena);
        var plan = new ModeTransitionController().Begin(office, gaming, PrototypeMotionProfile.Resolve(reducedMotion: true));

        Assert.IsFalse(plan.ShouldAnimateVisuals);
    }

    [TestMethod]
    public void Prototype_window_applies_plan_without_raw_animation_flag()
    {
        var method = typeof(PrototypeWindow).GetMethod(
            "ApplyModeTransitionPlan",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.IsNotNull(method);
        CollectionAssert.AreEqual(
            new[] { typeof(ModeTransitionPlan) },
            method.GetParameters().Select(parameter => parameter.ParameterType).ToArray());

        var office = ModeVisualCatalog.All.Single(scene => scene.Kind == ModeSceneKind.OrbitalFocus);
        var gaming = ModeVisualCatalog.All.Single(scene => scene.Kind == ModeSceneKind.FacetArena);
        var coordinator = new ModeTransitionController();
        Assert.IsTrue(coordinator.Begin(office, gaming, PrototypeMotionProfile.Resolve(reducedMotion: false)).ShouldAnimateVisuals);
        Assert.IsFalse(coordinator.Begin(gaming, office, PrototypeMotionProfile.Resolve(reducedMotion: true)).ShouldAnimateVisuals);
    }

    [TestMethod]
    public void Mode_bar_uses_centered_indicators_and_shared_expandable_cards()
    {
        var modeBar = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "HomeModeBar.xaml");
        var modeBarCode = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "HomeModeBar.xaml.cs");
        var theme = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "PrototypeThemeResources.xaml");
        var hero = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "HomeHero.xaml");
        var shell = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "PrototypeWindow.xaml");
        var modeBarDocument = XDocument.Parse(modeBar);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XNamespace controls = "using:Jiaolong_ControlCenter.Prototype.Controls";
        var rootGrid = modeBarDocument.Root!.Element(presentation + "Border")!.Element(presentation + "Grid")!;

        StringAssert.Contains(modeBar, "Width=\"729\" Height=\"168\"");
        var columns = rootGrid.Element(presentation + "Grid.ColumnDefinitions")!.Elements(presentation + "ColumnDefinition").ToArray();
        CollectionAssert.AreEqual(new[] { "*", "1", "*", "1", "*", "1", "*" }, columns.Select(column => (string?)column.Attribute("Width") ?? "*").ToArray());
        var cards = rootGrid.Elements(presentation + "Border").Where(element => element.Attribute(xaml + "Name") is not null).ToArray();
        CollectionAssert.AreEqual(new[] { "0", "2", "4", "6" }, cards.Select(card => (string?)card.Attribute("Grid.Column")).ToArray());
        Assert.IsTrue(cards.All(card => (string?)card.Attribute("Margin") == "4"));
        CollectionAssert.AreEqual(new[] { "1", "3", "5" }, rootGrid.Elements(presentation + "Border").Except(cards).Select(divider => (string?)divider.Attribute("Grid.Column")).ToArray());
        StringAssert.Contains(modeBar, "<Border Background=\"{StaticResource PrototypeControlAcrylicBrush}\"");
        StringAssert.Contains(theme, "<Setter Property=\"Width\" Value=\"158\" />");
        StringAssert.Contains(theme, "<Setter Property=\"Height\" Value=\"148\" />");
        StringAssert.Contains(hero, "Style=\"{StaticResource PrototypeStrongCoolingToggleStyle}\"");
        StringAssert.Contains(modeBar, "RenderTransformOrigin=\"0.5,0.5\"");
        foreach (var profile in new[] { "Profile1", "Profile2", "Profile3" })
        {
            var button = modeBarDocument.Descendants(controls + "PrototypeButton")
                .Single(element => (string?)element.Attribute("Tag") == profile);
            Assert.AreEqual($"Mode.Custom.{profile}", (string?)button.Attribute("AutomationProperties.AutomationId"));
        }
        foreach (var indicator in new[] { "TurboNormalIndicator", "TurboQuietIndicator", "TurboExtremeIndicator", "CustomProfile1Indicator", "CustomProfile2Indicator", "CustomProfile3Indicator" })
            StringAssert.Contains(modeBar, $"x:Name=\"{indicator}\"");
        Assert.IsFalse(modeBar.Contains("x:Name=\"TurboIndicator\"", StringComparison.Ordinal));
        Assert.IsFalse(modeBar.Contains("x:Name=\"CustomIndicator\"", StringComparison.Ordinal));
        Assert.IsFalse(modeBarCode.Contains("= TurboIndicator", StringComparison.Ordinal));
        Assert.IsFalse(modeBarCode.Contains("= CustomIndicator", StringComparison.Ordinal));
        StringAssert.Contains(modeBar, "Text=\"狂飙\"");
        StringAssert.Contains(modeBar, "x:Name=\"TurboNormalIndicator\" Width=\"46\"");
        StringAssert.Contains(modeBar, "Text=\"静音狂飙\"");
        StringAssert.Contains(modeBar, "Text=\"极限狂飙\"");
        StringAssert.Contains(modeBar, "x:Name=\"TurboHeaderButton\"");
        StringAssert.Contains(modeBar, "x:Name=\"TurboOptionGrid\"");
        StringAssert.Contains(modeBar, "x:Name=\"TurboQuietButton\" Grid.Column=\"0\"");
        StringAssert.Contains(modeBar, "x:Name=\"TurboExtremeButton\" Grid.Column=\"1\"");
        foreach (var surface in new[] { "OfficeSelectionSurface", "GamingSelectionSurface", "TurboSelectionSurface", "CustomSelectionSurface" })
            StringAssert.Contains(modeBar, $"x:Name=\"{surface}\"");
        StringAssert.Contains(modeBarCode, "AddDouble(storyboard, pair.Value, \"Opacity\"");
        StringAssert.Contains(modeBarCode, "StartMainExit");
        StringAssert.Contains(modeBarCode, "TransitionCardSurfaces");
        Assert.IsFalse(modeBarCode.Contains("pair.Value.Background, \"Opacity\"", StringComparison.Ordinal));
        foreach (var buttonName in new[] { "TurboHeaderButton", "TurboQuietButton", "TurboExtremeButton" })
        {
            var button = modeBarDocument.Descendants(controls + "PrototypeButton")
                .Single(element => (string?)element.Attribute(xaml + "Name") == buttonName);
            Assert.IsTrue(double.Parse((string)button.Attribute("MinHeight")!) >= 44);
            Assert.IsFalse((string?)button.Attribute("Opacity") == "0");
            Assert.IsFalse(string.IsNullOrWhiteSpace((string?)button.Attribute("AutomationProperties.AutomationId")));
        }
        foreach (var name in new[] { "TurboMotionIcon", "TurboMotionLabel", "CustomMotionIcon", "CustomMotionLabel", "TurboOptions", "CustomOptions" })
            StringAssert.Contains(modeBar, $"x:Name=\"{name}\"");
        foreach (var removed in new[] { "TurboCollapsed", "TurboExpanded", "CustomCollapsed", "CustomExpanded" })
            Assert.IsFalse(modeBar.Contains($"x:Name=\"{removed}\"", StringComparison.Ordinal));
        Assert.AreEqual(6, modeBarDocument.Descendants(controls + "PrototypeButton")
            .Count(button => double.TryParse((string?)button.Attribute("MinHeight"), out var height) && height >= 44));
        StringAssert.Contains(modeBar, "Text=\"预设一\"");
        StringAssert.Contains(modeBar, "Text=\"预设二\"");
        StringAssert.Contains(modeBar, "Text=\"预设三\"");
        Assert.IsFalse(modeBar.Contains("Text=\"普通\"", StringComparison.Ordinal));
        Assert.IsFalse(theme.Contains("Target=\"TierRoot.Background\"", StringComparison.Ordinal));
        Assert.IsFalse(shell.Contains("TurboModeOverlay", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Mode_hover_glow_follows_icon_alpha_instead_of_a_backing_shape()
    {
        var xaml = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "HomeModeBar.xaml");
        var code = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "HomeModeBar.xaml.cs");

        foreach (var host in new[] { "OfficeIconGlowHost", "GamingIconGlowHost", "TurboIconGlowHost", "CustomIconGlowHost" })
            StringAssert.Contains(xaml, $"x:Name=\"{host}\"");
        Assert.IsFalse(xaml.Contains("<RadialGradientBrush", StringComparison.Ordinal));
        StringAssert.Contains(code, "icon.GetAlphaMask()");
        StringAssert.Contains(code, "compositor.CreateDropShadow()");
        StringAssert.Contains(code, "dropShadow.Mask");
        StringAssert.Contains(code, "BlurRadius = 16f");
        StringAssert.Contains(code, "Offset = Vector3.Zero");
        StringAssert.Contains(code, "ElementCompositionPreview.SetElementChildVisual");
    }

    [TestMethod]
    public void All_mode_cards_share_full_height_surfaces_and_expanded_headers_share_one_baseline()
    {
        var modeBar = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "HomeModeBar.xaml");
        var code = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "HomeModeBar.xaml.cs");
        var document = XDocument.Parse(modeBar);
        XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        foreach (var name in new[] { "OfficeCard", "GamingCard", "TurboCard", "CustomCard" })
        {
            var card = document.Descendants(p + "Border").Single(element => (string?)element.Attribute(x + "Name") == name);
            Assert.IsTrue(string.IsNullOrEmpty((string?)card.Attribute("BorderThickness")));
            var root = card.Elements(p + "Grid").Single();
            Assert.IsNull(root.Attribute("Height"));
            Assert.IsNull(root.Attribute("Width"));
        }

        StringAssert.Contains(code, "ExpandedIconY = 16");
        StringAssert.Contains(code, "ExpandedLabelY = -27");
        StringAssert.Contains(code, "CustomExpandedIconX = -36");
        StringAssert.Contains(code, "CustomExpandedLabelX = 24");
        StringAssert.Contains(code, "CustomExpandedLabelY = -27");
        StringAssert.Contains(modeBar, "x:Name=\"TurboOptions\" Height=\"50\" Margin=\"7,108,7,0\"");
        StringAssert.Contains(modeBar, "x:Name=\"CustomOptions\" Height=\"50\" Margin=\"5,108,5,0\"");
        StringAssert.Contains(code, "target.Parts.ExpandedLabelY");
        StringAssert.Contains(code, "parts.ExpandedLabelY");
    }

    [TestMethod]
    public void Requested_home_icons_use_the_automatic_flow_symbol()
    {
        var hero = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "HomeHero.xaml");
        var automation = ReadSource("src", "Jiaolong.ControlCenter", "Assets", "Icons", "NavAutomationFilled.svg");

        StringAssert.Contains(hero, "x:Name=\"AutomaticModeButton\"");
        StringAssert.Contains(hero, "NavAutomationFilled.svg");
        StringAssert.Contains(automation, "id=\"automation-trigger-cycle\"");
    }

    [TestMethod]
    public void Home_adaptive_switch_is_a_local_switch_and_keeps_its_product_name()
    {
        var hero = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "HomeHero.xaml");
        var heroCode = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "HomeHero.xaml.cs");
        var shell = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "PrototypeWindow.xaml.cs");

        StringAssert.Contains(hero, "Text=\"自适应模式\"");
        StringAssert.Contains(hero, "AutomationProperties.Name=\"自适应模式\"");
        Assert.IsFalse(heroCode.Contains("AutomaticModeRequested?.Invoke", StringComparison.Ordinal));
        Assert.IsFalse(shell.Contains("Hero.AutomaticModeRequested", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Hero_logo_has_no_rectangular_shadow_plate_over_the_background()
    {
        var hero = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "HomeHero.xaml");
        var heroCode = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "HomeHero.xaml.cs");

        StringAssert.Contains(hero, "Background=\"{StaticResource PrototypeControlAcrylicBrush}\"");
        StringAssert.Contains(hero, "x:Name=\"LogoVisual\"");
        Assert.IsFalse(hero.Contains("<ThemeShadow", StringComparison.Ordinal));
        StringAssert.Contains(heroCode, "LogoVisualHost.Translation = new Vector3(0, 0, 24)");
        StringAssert.Contains(hero, "x:Name=\"WaveLogoVisual\"");
    }

    [TestMethod]
    public void Checked_home_controls_use_frosted_surfaces_without_duplicate_window_overlays()
    {
        var theme = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "PrototypeThemeResources.xaml");
        var sidebar = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "HomeSidebar.xaml");
        var hero = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "HomeHero.xaml");
        var shell = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "PrototypeWindow.xaml");

        StringAssert.Contains(theme, "TintColor=\"#F7F9FC\"");
        StringAssert.Contains(theme, "TintOpacity=\"0.18\"");
        StringAssert.Contains(theme, "TintLuminosityOpacity=\"0.44\"");
        StringAssert.Contains(theme, "FallbackColor=\"#B0DDE2E8\"");
        StringAssert.Contains(theme, "x:Key=\"PrototypeEnabledInnerStrokeBrush\"");
        StringAssert.Contains(theme, "x:Name=\"QuickTint\"");
        StringAssert.Contains(theme, "x:Name=\"QuickGlow\"");
        StringAssert.Contains(theme, "x:Name=\"StrongCoolingAccent\"");
        StringAssert.Contains(theme, "Height=\"3\"");
        StringAssert.Contains(theme, "Background=\"{StaticResource ModeAccentBrush}\"");
        StringAssert.Contains(sidebar, "RowDefinition Height=\"61\"");
        StringAssert.Contains(sidebar, "ColumnDefinition Width=\"61\"");
        StringAssert.Contains(hero, "Style=\"{StaticResource PrototypeStrongCoolingToggleStyle}\"");
        Assert.IsFalse(shell.Contains("QuickOverlay", StringComparison.Ordinal));
        Assert.IsFalse(shell.Contains("StrongCoolingEnabledOverlay", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Hero_logo_uses_the_shared_reference_layer_renderer()
    {
        var renderer = ReadSource("src", "Jiaolong.ControlCenter", "Branding", "HeroLogoControl.xaml");
        var rendererCode = ReadSource("src", "Jiaolong.ControlCenter", "Branding", "HeroLogoControl.xaml.cs");
        var hero = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "HomeHero.xaml");
        var heroCode = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "HomeHero.xaml.cs");
        var windowCode = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "PrototypeWindow.xaml.cs");

        foreach (var mode in new[] { "Office", "Gaming", "Turbo" })
            StringAssert.Contains(renderer, $"HeroLogoFull{mode}.png");
        foreach (var profile in new[] { "Profile1", "Profile2", "Profile3" })
            StringAssert.Contains(renderer, $"HeroLogoFullCustom{profile}.png");
        Assert.AreEqual(6, renderer.Split("x:Name=\"FullLogo", StringSplitOptions.None).Length - 1);
        StringAssert.Contains(renderer, "AdaptiveLogoLayers");
        StringAssert.Contains(renderer, "HeroLogoSilver.png");
        Assert.IsFalse(renderer.Contains("HeroLogoNeutralShell.png", StringComparison.Ordinal));
        StringAssert.Contains(rendererCode, "AdaptiveCoreLayers");
        StringAssert.Contains(rendererCode, "FullLogoLayers");
        Assert.IsFalse(rendererCode.Contains("profile.CrystalOpacity", StringComparison.Ordinal));
        StringAssert.Contains(rendererCode, "CustomProfileProperty");
        StringAssert.Contains(rendererCode, "PrototypePerformanceMode.Custom => CustomProfile switch");
        Assert.IsFalse(renderer.Contains("HeroLogoGapBridge", StringComparison.Ordinal));
        Assert.IsFalse(renderer.Contains("<Path", StringComparison.Ordinal));
        Assert.IsFalse(renderer.Contains("<Polygon", StringComparison.Ordinal));
        StringAssert.Contains(rendererCode, "ModeProperty");
        StringAssert.Contains(rendererCode, "ProfileProperty");
        StringAssert.Contains(rendererCode, "TransitionDurationProperty");
        StringAssert.Contains(hero, "xmlns:branding=\"using:Jiaolong_ControlCenter.Branding\"");
        Assert.AreEqual(1, hero.Split("x:Name=\"LogoVisual\"", StringSplitOptions.None).Length - 1);
        Assert.IsFalse(hero.Contains("TwinCrystalWingGeometry", StringComparison.Ordinal));
        Assert.IsFalse(hero.Contains("ApprovedDragonSilver.png", StringComparison.Ordinal));
        StringAssert.Contains(heroCode, "HeroLogoProfileStore.LoadOrDefault");
        StringAssert.Contains(heroCode, "ApplyMode(PrototypeModeTheme theme, string turboTier, string? customProfile");
        StringAssert.Contains(windowCode, "plan.Target.CustomProfile");
    }

    [TestMethod]
    public void Performance_view_is_centered_and_accepts_mode_specific_five_axis_values()
    {
        var hero = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "HomeHero.xaml");
        var heroCode = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "HomeHero.xaml.cs");
        var windowCode = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "PrototypeWindow.xaml.cs")
            + ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "PrototypeWindow.Turbo.cs");

        StringAssert.Contains(hero, "x:Name=\"PerformanceRadar\"");
        StringAssert.Contains(hero, "AutomationProperties.AutomationId=\"PerformanceRadar\"");
        StringAssert.Contains(hero, "x:Name=\"PerformanceRadarGrid\"");
        StringAssert.Contains(hero, "Foreground=\"{ThemeResource TextSecondaryBrush}\"");
        foreach (var axis in new[] { "CpuValue", "GpuValue", "CoolingValue", "ResponseValue", "QuietValue" })
            StringAssert.Contains(hero, $"x:Name=\"{axis}\"");
        Assert.IsFalse(hero.Contains("x:Name=\"PerformancePentagon\"", StringComparison.Ordinal));
        StringAssert.Contains(heroCode, "ApplyPerformanceProfile(PerformanceRadarProfile profile, TimeSpan? duration = null)");
        StringAssert.Contains(heroCode, "profile = profile.Normalized();");
        StringAssert.Contains(heroCode, "CompositionTarget.Rendering += OnRadarRendering");
        StringAssert.Contains(hero, "x:Name=\"PerformanceRadarFrost\"");
        StringAssert.Contains(hero, "x:Name=\"PerformanceRadarShadow\"");
        StringAssert.Contains(windowCode, "Hero.ApplyPerformanceProfile(");
        StringAssert.Contains(windowCode, "Hero.SetHighContrast(highContrast);");
    }

    [TestMethod]
    public void Performance_radar_shape_and_color_share_one_timeline()
    {
        var code = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "HomeHero.xaml.cs");

        StringAssert.Contains(code, "radarColorFrom");
        StringAssert.Contains(code, "radarColorTarget");
        StringAssert.Contains(code, "LerpColor(radarColorFrom[index], radarColorTarget[index], eased)");
        StringAssert.Contains(code, "RadarEnergyBrush.GradientStops[index].Color = radarColors[index]");
        Assert.IsFalse(code.Contains("radarMaterialStoryboard", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("private void ApplyRadarMaterial", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Quick_menu_grid_keeps_bottom_clearance_for_last_row_border()
    {
        var sidebar = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "HomeSidebar.xaml");
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var grid = XDocument.Parse(sidebar).Descendants()
            .Single(element => (string?)element.Attribute(xaml + "Name") == "QuickSettingsGrid");

        Assert.IsTrue(double.Parse((string)grid.Attribute("Height")!) >= 213,
            "The quick-menu grid needs bottom clearance so DPI rounding cannot clip the last row border.");
    }

    [TestMethod]
    public void Logo_lab_reuses_the_renderer_and_is_debug_only()
    {
        var lab = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "LogoLabWindow.xaml");
        var labCode = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "LogoLabWindow.xaml.cs");
        var app = ReadSource("src", "Jiaolong.ControlCenter", "App.xaml.cs");

        StringAssert.Contains(lab, "branding:HeroLogoControl");
        StringAssert.Contains(lab, "Title=\"Jiaolong Logo Lab\"");
        StringAssert.Contains(lab, "<ComboBox");
        StringAssert.Contains(lab, "<NumberBox");
        StringAssert.Contains(lab, "<Slider");
        StringAssert.Contains(lab, "<CommandBar");
        foreach (var name in new[] { "Logo Lab 模式", "Logo X", "Logo Y", "Logo 尺寸", "水晶强度", "银色映光强度", "动画时长", "验证并导出", "恢复参考值", "截取四模式" })
            StringAssert.Contains(lab, $"AutomationProperties.Name=\"{name}\"");
        foreach (var view in new[] { "ReferenceImage", "Overlay", "DiffImage" })
            StringAssert.Contains(lab, view);
        StringAssert.Contains(labCode, "CurrentProfile = CurrentProfile with");
        StringAssert.Contains(labCode, "RenderTargetBitmap");
        StringAssert.Contains(labCode, "HeroLogoPixelComparer.Compare");
        StringAssert.Contains(labCode, "lastDiff.MeanChannelDelta <= 4");
        StringAssert.Contains(labCode, "HeroLogoProfileStore.SaveAtomic");
        StringAssert.Contains(labCode, "SpecialFolder.LocalApplicationData");
        Assert.IsFalse(labCode.Contains("ApplicationData.Current", StringComparison.Ordinal));
        StringAssert.Contains(labCode, "previousVisibility");
        StringAssert.Contains(labCode, "element.Visibility = Visibility.Visible");
        StringAssert.Contains(labCode, "element.Width = logicalSize");
        StringAssert.Contains(lab, "ThemeResource ApplicationPageBackgroundThemeBrush");
        Assert.IsFalse(lab.Contains("#FF0A0D12", StringComparison.Ordinal));
        StringAssert.Contains(app, "#if DEBUG");
        StringAssert.Contains(app, "--logo-lab");
        StringAssert.Contains(app, "new LogoLabWindow(adaptiveCore)");
    }

    [TestMethod]
    public void Homepage_mode_commands_do_not_render_diagnostic_status_banner()
    {
        var shell = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "PrototypeWindow.xaml");
        var code = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "PrototypeWindow.xaml.cs");

        Assert.IsFalse(shell.Contains("ServiceStatusDot", StringComparison.Ordinal));
        Assert.IsFalse(shell.Contains("ServiceStatusText", StringComparison.Ordinal));
        Assert.IsFalse(shell.Contains("PowerStatusText", StringComparison.Ordinal));
        Assert.IsFalse(shell.Contains("BiosStatusText", StringComparison.Ordinal));
        Assert.IsFalse(shell.Contains("BatteryStatusText", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("SetServiceStatus", StringComparison.Ordinal));
        StringAssert.Contains(code, "PrototypeModeCommandCoordinator");
        StringAssert.Contains(code, "outcome.Applied");
        StringAssert.Contains(code, "homeSession.ExecuteAsync(command, cancellationToken)");
        Assert.IsFalse(code.Contains("HomeModeBar.IsEnabled = false", StringComparison.Ordinal));
        StringAssert.Contains(code, "homeSessionTask = StartHomeSessionAsync()");
        StringAssert.Contains(code, "modeCommandTask = ApplyPendingModesAsync()");
        StringAssert.Contains(code, "await Task.WhenAll(pendingTasks)");

        var visualCommit = code.IndexOf("state.Mode = request.Mode;", StringComparison.Ordinal);
        var hardwareAwait = code.IndexOf("await modeCommands.ApplyAsync", StringComparison.Ordinal);
        Assert.IsTrue(hardwareAwait >= 0 && visualCommit > hardwareAwait,
            "Mode visuals must commit only after command success and hardware confirmation.");
        Assert.IsFalse(code.Contains("state.Mode = mode;", StringComparison.Ordinal));
        StringAssert.Contains(code, "!isModeCommandPending && snapshot.Controls.PerformanceMode");
        StringAssert.Contains(code, "confirmed.Controls.PerformanceMode == outcome.ContractMode");
    }

    [TestMethod]
    public void Window_chrome_and_branding_match_the_approved_contract()
    {
        var chrome = PrototypeWindowChrome.RequiredDwmAttributes;
        Assert.AreEqual(33, chrome.CornerPreferenceAttribute);
        Assert.AreEqual(DwmWindowCornerPreference.Round, chrome.CornerPreference);
        Assert.AreEqual(34, chrome.BorderColorAttribute);
        Assert.AreEqual(0x00242424u, chrome.BorderColor);

        var code = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "PrototypeWindow.xaml.cs");
        var chromeCode = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "PrototypeWindowChrome.cs");
        var shell = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "PrototypeWindow.xaml");
        var sidebar = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "HomeSidebar.xaml");
        var hero = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "HomeHero.xaml");
        var backdrop = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "ModeAmbientBackdrop.xaml");
        var wordmark = ReadSource("src", "Jiaolong.ControlCenter", "Assets", "Brand", "MechrevoTechWordmark.svg");

        Assert.IsFalse(code.Contains("SetBorderAndTitleBar(", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("PrototypeWindowChrome.RemoveSystemFrame", StringComparison.Ordinal));
        StringAssert.Contains(code, "SetTitleBar(WindowTitleBar)");
        StringAssert.Contains(shell, "x:Name=\"WindowTitleBar\"");
        StringAssert.Contains(shell, "Stretch=\"Uniform\"");
        Assert.IsFalse(shell.Contains("TitleBar.RightHeader", StringComparison.Ordinal));
        StringAssert.Contains(shell, "GradientStop Color=\"#FF010204\"");
        StringAssert.Contains(shell, "Canvas.Top=\"933\" Width=\"1380\" Height=\"112\"");
        StringAssert.Contains(shell, "x:Name=\"TopPageFadeMask\"");
        StringAssert.Contains(shell, "x:Name=\"BottomPageFadeMask\"");
        StringAssert.Contains(code, "SetPageFadeMasksVisible(destination != \"Home\")");
        Assert.IsFalse(shell.Contains("Click=\"OnMaximizeClick\"", StringComparison.Ordinal));
        Assert.IsFalse(shell.Contains("Click=\"OnMinimizeClick\"", StringComparison.Ordinal));
        Assert.IsFalse(shell.Contains("Click=\"OnCloseClick\"", StringComparison.Ordinal));
        Assert.IsFalse(shell.Contains("CornerRadius=\"24\"", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("ApplyContentCornerClip", StringComparison.Ordinal));
        Assert.IsFalse(shell.Contains("ModeTransitionVeil", StringComparison.Ordinal));
        StringAssert.Contains(shell, "Width=\"1672\"");
        StringAssert.Contains(shell, "Height=\"1045\"");
        StringAssert.Contains(shell, "Canvas.Left=\"292\"");
        StringAssert.Contains(shell, "Canvas.Left=\"901\"");
        StringAssert.Contains(shell, "Canvas.Top=\"158\" Width=\"609\" Height=\"835\"");
        StringAssert.Contains(shell, "Canvas.Top=\"88\" Width=\"729\" Height=\"728\"");
        StringAssert.Contains(shell, "Canvas.Top=\"832\" Width=\"729\" Height=\"168\"");
        StringAssert.Contains(backdrop, "Height=\"1045\"");
        Assert.IsFalse(shell.Contains("SidebarEdgeBlend", StringComparison.Ordinal));
        Assert.IsFalse(shell.Contains("BorderBrush=\"White\"", StringComparison.Ordinal));
        Assert.IsFalse(shell.Contains("x:Name=\"WindowOutline\"", StringComparison.Ordinal));
        Assert.IsFalse(shell.Contains("BorderBrush=\"#38DDE9F5\"", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("DwmExtendFrameIntoClientArea", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("NativeDwmMargins", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("DWMWA_CAPTION_COLOR", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("captionColor", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("ShouldCollapseNonClientFrame(message", StringComparison.Ordinal));
        StringAssert.Contains(sidebar, "Have you tried turning it");
        StringAssert.Contains(hero, "XISURA");
        Assert.IsFalse(hero.Contains("Series", StringComparison.Ordinal));
        StringAssert.Contains(wordmark, "fill=\"#FFFFFF\"");
        Assert.IsFalse(wordmark.Contains("gradient", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(wordmark.Contains("<filter", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void Window_delegates_frame_and_eight_resize_hits_to_overlapped_presenter()
    {
        var code = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "PrototypeWindow.xaml.cs");

        Assert.IsFalse(code.Contains("WmNcHitTest", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("TryGetResizeHitTest", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("CreateRoundRectRgn", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("SetWindowRgn", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("SetBorderAndTitleBar(", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("PrototypeWindowChrome.RemoveSystemFrame", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("DwmExtendFrameIntoClientArea", StringComparison.Ordinal));
        StringAssert.Contains(code, "WmSizing");
        StringAssert.Contains(code, "WmSysCommand");
        StringAssert.Contains(code, "ScMaximize");
        StringAssert.Contains(code, "ToggleFitToWorkArea");
        StringAssert.Contains(code, "IsResizable = true");
        StringAssert.Contains(code, "IsMaximizable = true");
    }

    [TestMethod]
    public void Native_resize_path_keeps_only_the_aspect_ratio_hook()
    {
        var code = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "PrototypeWindow.xaml.cs");
        StringAssert.Contains(code, "WmSizing");
        Assert.IsFalse(code.Contains("BeginCustomResize", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("UpdateCustomResize", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("TryGetResizeHitTest", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("WmEnterSizeMove", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("WmExitSizeMove", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("isInteractiveResize", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("pendingChromeRefresh", StringComparison.Ordinal));
        StringAssert.Contains(code, "DefSubclassProc(hwnd, message, wParam, lParam)");
        StringAssert.Contains(code, "RemoveSizingHook();");
    }

    [TestMethod]
    public void Native_window_uses_edge_resize_and_preserves_current_default_size()
    {
        var window = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "PrototypeWindow.xaml");
        var chrome = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "PrototypeWindowChrome.cs");
        var windowCode = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "PrototypeWindow.xaml.cs");

        Assert.IsFalse(window.Contains("ResizeHitTargets", StringComparison.Ordinal));
        Assert.IsFalse(window.Contains("OnResizeGripPointerPressed", StringComparison.Ordinal));
        Assert.IsFalse(chrome.Contains("WmNcHitTest", StringComparison.Ordinal));
        StringAssert.Contains(chrome, "ConstrainSizingRect");
        StringAssert.Contains(windowCode, "InstallSizingHook();");
        StringAssert.Contains(windowCode, "SetWindowSubclass(hwnd, sizingWindowProcedure");
        StringAssert.Contains(windowCode, "return DefSubclassProc(hwnd, message, wParam, lParam);");
        Assert.IsFalse(windowCode.Contains("previousWindowProcedure", StringComparison.Ordinal));
        Assert.IsFalse(windowCode.Contains("tray.HandleWindowMessage(message", StringComparison.Ordinal));
        Assert.IsFalse(windowCode.Contains("SetBorderAndTitleBar(", StringComparison.Ordinal));
        Assert.IsFalse(windowCode.Contains("SetWindowRgn", StringComparison.Ordinal));
        Assert.IsFalse(window.Contains("x:Name=\"WindowOutline\"", StringComparison.Ordinal));
        StringAssert.Contains(chrome, "DefaultClientSize { get; } = new(1966, 1229)");
    }

    [TestMethod]
    public void Home_brand_wordmark_asset_is_packaged_and_self_contained()
    {
        var wordmark = ReadSource("src", "Jiaolong.ControlCenter", "Assets", "Brand", "MechrevoJiaolongWordmark.svg");
        var project = ReadSource("src", "Jiaolong.ControlCenter", "Jiaolong.ControlCenter.csproj");

        StringAssert.Contains(project, "<Content Include=\"Assets\\Brand\\MechrevoJiaolongWordmark.svg\" CopyToOutputDirectory=\"PreserveNewest\" CopyToPublishDirectory=\"PreserveNewest\" />");
        StringAssert.Contains(wordmark, "viewBox=\"0 0 225 56\"");
        StringAssert.Contains(wordmark, "fill=\"#F7F9FC\"");
        Assert.IsFalse(wordmark.Contains("<text", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(wordmark.Contains("font-family", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(wordmark.Contains("gradient", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(wordmark.Contains("<filter", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void Home_branding_uses_the_requested_accessible_Xisura_wordmark()
    {
        var hero = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "HomeHero.xaml");

        StringAssert.Contains(hero, "Source=\"ms-appx:///Assets/Brand/XisuraWordmark.svg\"");
        StringAssert.Contains(hero, "AutomationProperties.AutomationId=\"HeroBrandWordmark\"");
        StringAssert.Contains(hero, "AutomationProperties.Name=\"XISURA\"");
        StringAssert.Contains(hero, "Canvas.Left=\"72\"");
        StringAssert.Contains(hero, "Canvas.Top=\"460\"");
        StringAssert.Contains(hero, "Width=\"465\"");
        StringAssert.Contains(hero, "Height=\"45\"");
        StringAssert.Contains(hero, "Canvas.Top=\"518\"");
        Assert.IsFalse(hero.Contains("Series", StringComparison.Ordinal));

    }

    [TestMethod]
    public void Home_hero_components_share_one_horizontal_centerline()
    {
        var hero = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "HomeHero.xaml");

        Assert.AreEqual(124.5, HeroLogoProfile.Default.Left, 0.001);
        StringAssert.Contains(hero, "Canvas.Left=\"205.5\" Canvas.Top=\"9\"");
        StringAssert.Contains(hero, "Canvas.Left=\"225.5\" Canvas.Top=\"610\"");
        StringAssert.Contains(hero, "Canvas.Left=\"72\" Canvas.Top=\"460\"");
        StringAssert.Contains(hero, "Canvas.Left=\"72\" Canvas.Top=\"518\" Width=\"465\"");
    }

    [TestMethod]
    public void Dwm_chrome_failure_reports_the_failed_attribute()
    {
        var success = PrototypeWindowChrome.EvaluateDwmResults(0, 0);
        var borderFailure = PrototypeWindowChrome.EvaluateDwmResults(unchecked((int)0x80004005), 0);
        var cornerFailure = PrototypeWindowChrome.EvaluateDwmResults(0, unchecked((int)0x80004005));

        Assert.IsTrue(success.Succeeded);
        Assert.IsNull(success.Diagnostic);
        Assert.IsFalse(borderFailure.Succeeded);
        StringAssert.Contains(borderFailure.Diagnostic!, "border-color");
        Assert.IsFalse(cornerFailure.Succeeded);
        StringAssert.Contains(cornerFailure.Diagnostic!, "corner-preference");
    }

    [TestMethod]
    public void Window_chrome_subscription_attaches_once_and_detaches_once()
    {
        var subscription = new WindowChromeSubscription();

        Assert.IsTrue(subscription.TryAttach());
        Assert.IsFalse(subscription.TryAttach());
        Assert.IsTrue(subscription.IsAttached);
        Assert.IsTrue(subscription.TryDetach());
        Assert.IsFalse(subscription.TryDetach());
        Assert.IsFalse(subscription.IsAttached);
    }

    [TestMethod]
    public void Monitor_titles_share_a_32px_icon_slot_and_12px_text_gap()
    {
        var monitor = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "MonitorPanel.xaml");

        StringAssert.Contains(monitor, "Width=\"32\"");
        StringAssert.Contains(monitor, "Height=\"32\"");
        StringAssert.Contains(monitor, "Spacing=\"12\"");
        StringAssert.Contains(monitor, "Source=\"{x:Bind IconSource, Mode=OneTime}\"");
    }

    [TestMethod]
    public void Home_monitor_uses_two_primary_cards_and_one_compact_detail_rail()
    {
        var grid = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "HomeMonitorGrid.xaml");
        var panel = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "MonitorPanel.xaml");
        var panelCode = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "MonitorPanel.xaml.cs");
        var gridCode = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "HomeMonitorGrid.xaml.cs");
        var windowCode = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "PrototypeWindow.xaml.cs");

        Assert.IsFalse(grid.Contains("Text=\"系统监控\"", StringComparison.Ordinal));
        foreach (var name in new[] { "CpuMonitorCard", "GpuMonitorCard", "MemoryDetail", "StorageDetail", "FanDetail" })
            StringAssert.Contains(grid, $"x:Name=\"{name}\"");
        foreach (var text in new[] { "C 盘", "所有盘", "CPU 风扇", "GPU 风扇" })
            StringAssert.Contains(grid, $"Text=\"{text}\"");

        StringAssert.Contains(grid, "DeviceKind=\"Cpu\"");
        StringAssert.Contains(grid, "DeviceKind=\"Gpu\"");
        StringAssert.Contains(panel, "x:Name=\"TemperatureValue\"");
        StringAssert.Contains(panel, "Text=\"-- °C\"");
        StringAssert.Contains(panel, "Text=\"最近 60 秒\"");
        foreach (var guide in new[] { "100", "50", "0" })
            StringAssert.Contains(panel, $"Text=\"{guide}\"");
        Assert.IsFalse(panel.Contains("TrendEndpoint", StringComparison.Ordinal));
        StringAssert.Contains(panel, "x:Name=\"UsageValue\"");
        StringAssert.Contains(panel, "Text=\"-- %\"");
        StringAssert.Contains(panel, "LinearGradientBrush");
        StringAssert.Contains(panel, "x:Name=\"MetricCapsule\"");
        StringAssert.Contains(gridCode, "ApplyTemperatures(double? cpuTemperatureC, double? gpuTemperatureC)");
        StringAssert.Contains(gridCode, "ApplyTelemetry(HomeTelemetrySnapshot snapshot)");
        StringAssert.Contains(windowCode, "HomeControlSession");
        StringAssert.Contains(windowCode, "OnHomeTelemetryUpdated");
        var sessionCode = ReadSource("src", "Jiaolong.ControlCenter", "Services", "HomeControlSession.cs");
        StringAssert.Contains(sessionCode, "ConsumeControlStateAsync");
        StringAssert.Contains(sessionCode, "ControlStateRefreshInterval");
        StringAssert.Contains(sessionCode, "controlStateGate");
        Assert.IsFalse(panel.Contains("<ProgressRing", StringComparison.Ordinal));
        StringAssert.Contains(grid, "x:Name=\"MemorySegments\"");
        Assert.IsFalse(grid.Contains("剩余 17.0 GB", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Monitor_cards_use_the_approved_temperature_rail_without_touching_trend()
    {
        var panel = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "MonitorPanel.xaml");
        var panelCode = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "MonitorPanel.xaml.cs");
        var state = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "PrototypeState.cs");
        var grid = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "HomeMonitorGrid.xaml");
        var gridCode = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "HomeMonitorGrid.xaml.cs");
        var windowCode = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "PrototypeWindow.xaml.cs");

        foreach (var name in new[] { "TemperatureRail", "TemperatureMarker", "TemperatureWallMarker", "TemperatureStatus", "TemperaturePeak" })
            StringAssert.Contains(panel, $"x:Name=\"{name}\"");
        foreach (var tick in new[] { "40°C", "60°C", "80°C" })
            StringAssert.Contains(panel, $"Text=\"{tick}\"");

        Assert.IsTrue(panel.Split("<GradientStop", StringSplitOptions.None).Length - 1 >= 10);
        Assert.IsFalse(panel.Contains("Text=\"85°C\"", StringComparison.Ordinal));
        Assert.IsFalse(panel.Contains("TemperatureTarget", StringComparison.Ordinal));
        StringAssert.Contains(panelCode, "MaximumTemperatureC");
        StringAssert.Contains(panelCode, "temperatureWallC");
        StringAssert.Contains(panelCode, "ApplyTemperatureWall");
        StringAssert.Contains(panelCode, "TemperatureWallMarker");
        StringAssert.Contains(panelCode, "currentTemperature >= temperatureWallC");
        StringAssert.Contains(panelCode, "Canvas.SetLeft(TemperatureMarker");
        StringAssert.Contains(panelCode, "Canvas.SetLeft(TemperatureWallMarker");
        StringAssert.Contains(state, "TemperatureWallC");
        StringAssert.Contains(gridCode, "ApplyTemperatureWall(double wallC)");
        StringAssert.Contains(windowCode, "plan.Target.Theme.TemperatureWallC");

        var curveCode = ReadSource("src", "Jiaolong.ControlCenter", "Controls", "TelemetryCurve.xaml.cs");
        StringAssert.Contains(panel, "<shared:TelemetryCurve");
        StringAssert.Contains(grid, "<shared:TelemetryCurve");
        StringAssert.Contains(curveCode, "Viewport.Clip = new RectangleGeometry");
        StringAssert.Contains(curveCode, "new Rect(0, 0, ActualWidth, ActualHeight)");

        Assert.IsFalse(panel.Contains("ThermalVisualHost", StringComparison.Ordinal));
        Assert.IsFalse(panelCode.Contains("CpuThermalVisualAlpha.png", StringComparison.Ordinal));
        Assert.IsFalse(panelCode.Contains("GpuThermalVisualAlpha.png", StringComparison.Ordinal));
        Assert.IsFalse(panelCode.Contains("CreateMaskBrush", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Monitor_layout_keeps_temperature_groups_compact_and_gives_fans_the_trend_material()
    {
        var panel = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "MonitorPanel.xaml");
        var grid = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "HomeMonitorGrid.xaml");

        StringAssert.Contains(panel, "x:Name=\"ThermalField\" Width=\"312\" Height=\"142\" Padding=\"8,0,8,0\" HorizontalAlignment=\"Center\" VerticalAlignment=\"Center\"");
        StringAssert.Contains(panel, "<Grid.ColumnDefinitions><ColumnDefinition Width=\"112\" /><ColumnDefinition Width=\"184\" /></Grid.ColumnDefinitions>");
        StringAssert.Contains(panel, "<StackPanel Grid.Column=\"0\" Spacing=\"8\" VerticalAlignment=\"Center\">");
        StringAssert.Contains(panel, "<StackPanel Grid.Column=\"1\" Spacing=\"6\" VerticalAlignment=\"Center\">");
        StringAssert.Contains(panel, "x:Name=\"TemperatureStatus\" Text=\"温度未知\" Foreground=\"#B8FFFFFF\" FontSize=\"14\" FontWeight=\"SemiBold\"");
        StringAssert.Contains(panel, "x:Name=\"TemperaturePeak\" Typography.NumeralAlignment=\"Tabular\" Text=\"最高 --°C\" Foreground=\"#B8FFFFFF\" FontSize=\"14\" FontWeight=\"SemiBold\"");
        StringAssert.Contains(panel, "<Grid x:Name=\"MetricCapsule\" Grid.Row=\"3\"");
        StringAssert.Contains(panel, "BorderThickness=\"1,0,0,0\"");
        Assert.IsFalse(panel.Contains("x:Name=\"MetricCapsule\" Grid.Row=\"3\" Background=", StringComparison.Ordinal));
        Assert.IsFalse(panel.Contains("x:Name=\"MetricCapsule\" Grid.Row=\"3\" BorderBrush=", StringComparison.Ordinal));

        foreach (var name in new[] { "CpuFanCurve", "GpuFanCurve" })
            StringAssert.Contains(grid, $"x:Name=\"{name}\"");
        StringAssert.Contains(grid, "Stroke=\"#32FFFFFF\" StrokeThickness=\"1\" StrokeDashArray=\"2,7\"");
        Assert.IsFalse(grid.Contains("#1677FF", StringComparison.Ordinal));
        Assert.IsFalse(grid.Contains("#3A9DFF", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Home_monitor_lower_regions_use_real_data_and_displayed_value_interpolation()
    {
        var grid = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "HomeMonitorGrid.xaml");
        var gridCode = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "HomeMonitorGrid.xaml.cs");
        var panelCode = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "MonitorPanel.xaml.cs");
        var theme = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "PrototypeThemeResources.xaml");

        StringAssert.Contains(grid, "x:Name=\"MemorySegmentBars\"");
        StringAssert.Contains(grid, "Background=\"{StaticResource ModeMeterGradientBrush}\"");
        Assert.IsFalse(grid.Contains("Background=\"{StaticResource ModeGlowBrush}\" Margin=\"-3\"", StringComparison.Ordinal));
        StringAssert.Contains(theme, "x:Key=\"ModeMeterGradientBrush\"");
        StringAssert.Contains(theme, "x:Key=\"ModeSelectionBrush\"");
        StringAssert.Contains(theme, "x:Key=\"ModeSelectionAccentBrush\"");
        StringAssert.Contains(theme, "ModeSelectionAccentBrush");
        StringAssert.Contains(theme, "x:Key=\"ModeAccentFadeBrush\"");
        var meterGradient = theme[theme.IndexOf("x:Key=\"ModeMeterGradientBrush\"", StringComparison.Ordinal)..];
        meterGradient = meterGradient[..meterGradient.IndexOf("</LinearGradientBrush>", StringComparison.Ordinal)];
        StringAssert.Contains(meterGradient, "ModeDeepBrush");
        StringAssert.Contains(meterGradient, "ModeAccentBrush");
        Assert.IsFalse(meterGradient.Contains("ModeGlowBrush", StringComparison.Ordinal));
        StringAssert.Contains(grid, "x:Name=\"SystemDriveFillScale\"");
        StringAssert.Contains(grid, "x:Name=\"AllDrivesFillScale\"");
        StringAssert.Contains(grid, "x:Name=\"SystemDriveRemaining\"");
        StringAssert.Contains(grid, "<ColumnDefinition Width=\"0.8*\" /><ColumnDefinition Width=\"1.45*\" /><ColumnDefinition Width=\"1.05*\" />");
        StringAssert.Contains(grid, "<RowDefinition Height=\"*\" /><RowDefinition Height=\"*\" />");
        StringAssert.Contains(grid, "<Grid Grid.Row=\"1\" RowSpacing=\"10\" VerticalAlignment=\"Center\">");
        StringAssert.Contains(grid, "<Grid Grid.Row=\"2\" RowSpacing=\"10\" VerticalAlignment=\"Center\">");
        Assert.IsFalse(grid.Contains("C 12,42", StringComparison.Ordinal));
        Assert.IsFalse(grid.Contains("C 12,34", StringComparison.Ordinal));
        StringAssert.Contains(gridCode, "private readonly Stopwatch telemetryClock = new();");
        StringAssert.Contains(gridCode, "CpuFanCurve.UpdateSample(snapshot.CpuFanRpm, snapshot.CapturedAtUtc)");
        StringAssert.Contains(gridCode, "GpuFanCurve.UpdateSample(snapshot.GpuFanRpm, snapshot.CapturedAtUtc)");
        StringAssert.Contains(grid, "Margin=\"18,0\"");
        StringAssert.Contains(gridCode, "displayedTelemetry");
        StringAssert.Contains(gridCode, "InterpolateTelemetry(");
        StringAssert.Contains(gridCode, "ScaleX =");
        StringAssert.Contains(gridCode, "SetReadout(SystemDriveRemaining, FormatRemaining(snapshot.SystemDriveUsedGb, snapshot.SystemDriveTotalGb));");
        StringAssert.Contains(gridCode, "segment.Children.Count > 1");
        StringAssert.Contains(gridCode, "segment.Children[1] is Border fill");
        Assert.IsFalse(gridCode.Contains("segment.Children[2] is Border fill", StringComparison.Ordinal));
        StringAssert.Contains(gridCode, "ApplyDetailValues(");
        StringAssert.Contains(panelCode, "ApplyDetailValues(string detail1, string detail2)");
        StringAssert.Contains(panelCode, "displayedTemperatureC");
        StringAssert.Contains(panelCode, "SetReadout(TemperatureValue, FormatTemperature(displayedTemperatureC))");
    }

    [TestMethod]
    public void Monitor_capture_does_not_promote_the_window_to_topmost()
    {
        var capture = ReadSource(".superpowers", "qa", "Capture-MonitorThermal.ps1");
        var motionCapture = ReadSource(".superpowers", "qa", "Capture-MotionProfile.ps1");
        var window = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "PrototypeWindow.xaml.cs");

        Assert.IsFalse(capture.Contains("[IntPtr](-1)", StringComparison.Ordinal));
        Assert.IsFalse(motionCapture.Contains("[IntPtr](-1)", StringComparison.Ordinal));
        Assert.IsFalse(window.Contains("IntPtr(-1)", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Prototype_window_prefers_a_secondary_display_when_available()
    {
        var window = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "PrototypeWindow.xaml.cs");

        StringAssert.Contains(window, "EnumDisplayMonitors");
        StringAssert.Contains(window, "TryGetSecondaryWorkArea");
        StringAssert.Contains(window, "DisplayAreaFallback.Primary");
        StringAssert.Contains(window, "MoveToSecondaryDisplay");
        StringAssert.Contains(window, "DispatcherQueue.TryEnqueue");
    }

    [TestMethod]
    public void Monitor_temperature_markers_retarget_smoothly_when_mode_wall_changes()
    {
        var panelCode = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "MonitorPanel.xaml.cs");

        StringAssert.Contains(panelCode, "private readonly Stopwatch temperatureWallClock");
        StringAssert.Contains(panelCode, "private double temperatureProgressFrom");
        StringAssert.Contains(panelCode, "private double wallTemperatureProgressFrom");
        StringAssert.Contains(panelCode, "temperatureWallClock.Restart();");
        StringAssert.Contains(panelCode, "temperatureProgressFrom = currentRailProgress;");
        StringAssert.Contains(panelCode, "wallTemperatureProgressFrom = currentWallProgress;");
        StringAssert.Contains(panelCode, "currentRailProgress = Lerp(temperatureProgressFrom, targetTemperatureProgress, eased);");
        StringAssert.Contains(panelCode, "currentWallProgress = Lerp(wallTemperatureProgressFrom, targetWallProgress, eased);");
        StringAssert.Contains(panelCode, "if (ReducedMotion || !CanAnimate())");
    }

    [TestMethod]
    public void Home_layout_uses_the_full_16_by_10_height_with_even_right_column_spacing()
    {
        var shell = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "PrototypeWindow.xaml");
        var sidebar = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "HomeSidebar.xaml");
        var grid = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "HomeMonitorGrid.xaml");
        var modeBar = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "HomeModeBar.xaml");
        var theme = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "PrototypeThemeResources.xaml");

        StringAssert.Contains(shell, "Canvas.Top=\"88\"");
        StringAssert.Contains(shell, "Canvas.Top=\"832\"");
        StringAssert.Contains(grid, "Height=\"728\"");
        StringAssert.Contains(grid, "Height=\"438\"");
        StringAssert.Contains(grid, "Height=\"272\"");
        StringAssert.Contains(modeBar, "Height=\"168\"");
        StringAssert.Contains(sidebar, "Canvas.Top=\"738\"");
        StringAssert.Contains(sidebar, "Canvas.Top=\"790\"");
        StringAssert.Contains(theme, "<Setter Property=\"Height\" Value=\"74\" />");
        StringAssert.Contains(modeBar, "Margin=\"0,38,0,0\"");
        StringAssert.Contains(modeBar, "Margin=\"0,92,0,0\"");
        var hero = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "HomeHero.xaml");
        StringAssert.Contains(hero, "Canvas.Left=\"225.5\" Canvas.Top=\"610\"");
    }

    [TestMethod]
    public void Home_components_match_the_approved_reference_contract()
    {
        var hero = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "HomeHero.xaml");
        var grid = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "HomeMonitorGrid.xaml");
        var modeBar = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "HomeModeBar.xaml");

        StringAssert.Contains(hero, "XISURA");
        Assert.IsFalse(hero.Contains("Series", StringComparison.Ordinal));
        StringAssert.Contains(hero, "branding:HeroLogoControl");
        foreach (var icon in new[] { "HomeMonitorCpuFilled.svg", "HomeMonitorGpuFilled.svg", "HomeMonitorStorageFilled.svg", "HomeMonitorFanFilled.svg" })
            StringAssert.Contains(grid, icon);
        foreach (var icon in new[] { "HomeModeOfficeOutline.svg", "HomeModeGamingOutline.svg", "HomeModeTurboOutline.svg", "HomeModeCustomOutline.svg" })
            StringAssert.Contains(modeBar, icon);
        foreach (var card in new[] { "OfficeCard", "GamingCard", "TurboCard", "CustomCard" })
            StringAssert.Contains(modeBar, $"x:Name=\"{card}\"");
    }

    [TestMethod]
    public void Sidebar_uses_the_approved_filled_navigation_icon_family()
    {
        var sidebar = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "HomeSidebar.xaml");

        foreach (var name in new[] { "Home", "Performance", "Gpu", "Fan", "Lighting", "Automation", "Settings" })
            StringAssert.Contains(sidebar, $"Nav{name}Filled.svg");

        var navigation = sidebar[..sidebar.IndexOf("Canvas.Left=\"36\" Canvas.Top=\"738\"", StringComparison.Ordinal)];
        Assert.IsFalse(navigation.Contains("<FontIcon", StringComparison.Ordinal));
        StringAssert.Contains(sidebar, "Width=\"292\"");
        StringAssert.Contains(sidebar, "Height=\"1045\"");
        StringAssert.Contains(sidebar, "Canvas.Top=\"738\"");
        StringAssert.Contains(sidebar, "Canvas.Top=\"790\"");
    }

    [TestMethod]
    public void Window_surface_preserves_the_approved_aspect_ratio_without_cropping()
    {
        var shell = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "PrototypeWindow.xaml");

        StringAssert.Contains(shell, "<Viewbox Stretch=\"Uniform\">");
        StringAssert.Contains(shell, "x:Name=\"WindowSurface\"");
        StringAssert.Contains(shell, "Width=\"1672\"");
        StringAssert.Contains(shell, "Height=\"1045\"");
    }

    [TestMethod]
    public void Prototype_window_honors_user_and_system_reduced_motion_preferences()
    {
        var window = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "PrototypeWindow.xaml.cs");
        StringAssert.Contains(window, "userPreferences.ReduceMotion");
        StringAssert.Contains(window, "new MotionSettingsService().IsReducedMotionEnabled");
        StringAssert.Contains(window, "!new UISettings().AnimationsEnabled");
        StringAssert.Contains(window, "PrototypeMotionProfile.Resolve(reduceMotion)");
    }

    [TestMethod]
    public void Settings_do_not_expose_tray_or_reduced_motion_switches()
    {
        var prototypeSettings = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "PrototypePageWorkspace.xaml");
        var legacySettings = ReadSource("src", "Jiaolong.ControlCenter", "Pages", "SettingsPage.xaml");

        foreach (var source in new[] { prototypeSettings, legacySettings })
        {
            Assert.IsFalse(source.Contains("减少动画", StringComparison.Ordinal));
            Assert.IsFalse(source.Contains("MotionToggle", StringComparison.Ordinal));
            Assert.IsFalse(source.Contains("SettingsMotionToggle", StringComparison.Ordinal));
            Assert.IsFalse(source.Contains("TrayToggle", StringComparison.Ordinal));
            Assert.IsFalse(source.Contains("SettingsTrayToggle", StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public void Quick_menu_editor_is_large_enough_and_opens_inside_the_window()
    {
        var sidebar = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "HomeSidebar.xaml");
        var sidebarCodeBehind = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "HomeSidebar.xaml.cs");
        var editor = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "QuickMenuEditor.xaml");
        var editorCodeBehind = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "QuickMenuEditor.xaml.cs");

        StringAssert.Contains(sidebar, "Background=\"Transparent\"");
        StringAssert.Contains(sidebar, "BorderThickness=\"0\"");
        Assert.IsFalse(sidebar.Contains("Text=\"OSD\"", StringComparison.Ordinal));
        Assert.IsFalse(sidebar.Contains("Text=\"+\"", StringComparison.Ordinal));
        StringAssert.Contains(editor, "Width=\"600\"");
        StringAssert.Contains(editor, "Height=\"380\"");
        StringAssert.Contains(editor, "<ColumnDefinition Width=\"260\" />");
        StringAssert.Contains(editor, "<ColumnDefinition Width=\"1\" />");
        StringAssert.Contains(editor, "<ColumnDefinition Width=\"250\" />");
        StringAssert.Contains(editor, "ColumnSpacing=\"20\"");
        StringAssert.Contains(editor, "HorizontalScrollBarVisibility=\"Disabled\"");
        StringAssert.Contains(editor, "CanReorderItems=\"True\"");
        StringAssert.Contains(editor, "ItemWidth=\"68\"");
        StringAssert.Contains(editor, "TargetType=\"GridViewItem\"");
        StringAssert.Contains(editor, "Loaded=\"OnPreviewGridLoaded\"");
        Assert.IsFalse(sidebar.Contains("FlyoutPresenterStyle", StringComparison.Ordinal));
        StringAssert.Contains(sidebarCodeBehind, "quickMenuEditor");
        StringAssert.Contains(sidebarCodeBehind, "XamlRoot = XamlRoot");
        StringAssert.Contains(sidebarCodeBehind, "IsLightDismissEnabled = true");
        StringAssert.Contains(sidebarCodeBehind, "popup.Child = null");
        StringAssert.Contains(editor, "HorizontalAlignment=\"Stretch\"");
        StringAssert.Contains(editor, "Text=\"最多选择 9 项\"");
        StringAssert.Contains(editor, "Width=\"238\"");
        StringAssert.Contains(editor, "Height=\"238\"");
        StringAssert.Contains(editor, "Background=\"{StaticResource ModeQuickActiveTintBrush}\"");
        StringAssert.Contains(editor, "BorderBrush=\"{StaticResource ModeAccentBrush}\"");
        StringAssert.Contains(editor, "Width=\"61\"");
        StringAssert.Contains(editor, "Height=\"61\"");
        StringAssert.Contains(editor, "HorizontalScrollBarVisibility=\"Disabled\"");
        StringAssert.Contains(editor, "VerticalScrollBarVisibility=\"Auto\"");
        StringAssert.Contains(editor, "IsEnabled=\"{x:Bind IsSelectable, Mode=OneWay}\"");
        StringAssert.Contains(editor, "Tag=\"{x:Bind Item.Kind}\"");
        StringAssert.Contains(editor, "Loaded=\"OnOptionToggleLoaded\"");
        StringAssert.Contains(editor, "PrototypeQuickMenuCheckToggleStyle");
        StringAssert.Contains(editorCodeBehind, "RefreshSelectionAvailability");
        StringAssert.Contains(editorCodeBehind, "panel.MaximumRowsOrColumns = 3");
        StringAssert.Contains(editorCodeBehind, "Tag: QuickSettingKind kind");
        StringAssert.Contains(editorCodeBehind, "var enabled = toggle.IsChecked == true;");
        StringAssert.Contains(editorCodeBehind, "SynchronizeOptionToggle(toggle, useTransitions: true)");
        StringAssert.Contains(editorCodeBehind, "QuickMenuPreviewLayout.Create");
        StringAssert.Contains(editorCodeBehind, "QuickMenuPreviewLayout.ToLayout");
        StringAssert.Contains(sidebarCodeBehind, "quickMenuLayoutApplyQueued");
        StringAssert.Contains(sidebarCodeBehind, "DispatcherQueue.TryEnqueue");
        Assert.IsFalse(sidebarCodeBehind.Contains("ApplyQuickMenuLayout(value);", StringComparison.Ordinal));
        StringAssert.Contains(sidebarCodeBehind, "ApplyQuickMenuLayout(layout);");
        Assert.IsFalse(editor.Contains("Width=\"360\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Quick_menu_editor_uses_atomic_content_and_a_centered_popup_host()
    {
        var sidebar = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "HomeSidebar.xaml");
        var sidebarCodeBehind = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "HomeSidebar.xaml.cs");
        var editorCodeBehind = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "QuickMenuEditor.xaml.cs");

        Assert.IsFalse(sidebar.Contains("<Flyout", StringComparison.Ordinal));
        StringAssert.Contains(sidebarCodeBehind, "new Popup");
        StringAssert.Contains(sidebarCodeBehind, "PositionQuickMenuEditor");
        Assert.IsFalse(sidebarCodeBehind.Contains("QuickSettingsGrid.Children.Clear()", StringComparison.Ordinal));
        Assert.IsFalse(editorCodeBehind.Contains("Options.Clear()", StringComparison.Ordinal));
        Assert.IsFalse(editorCodeBehind.Contains("PreviewItems.Clear()", StringComparison.Ordinal));
        StringAssert.Contains(editorCodeBehind, "OptionsRepeater.ItemsSource = Options");
        StringAssert.Contains(editorCodeBehind, "PreviewGrid.ItemsSource = PreviewItems");
    }

    [TestMethod]
    public void Mode_indicator_enter_plan_is_available_only_after_its_exit_completes()
    {
        var coordinator = new ModeIndicatorTransitionCoordinator();
        var exit = coordinator.BeginMode(PrototypePerformanceMode.Gaming, PrototypeMotionProfile.Resolve(reducedMotion: false), animate: true);

        Assert.AreEqual(ModeIndicatorTransitionPhase.Exit, exit.Phase);
        Assert.AreEqual(TimeSpan.FromMilliseconds(80), exit.PhaseDuration);
        Assert.IsNull(coordinator.CompleteExit(exit.Version + 1));

        var enter = coordinator.CompleteExit(exit.Version);

        Assert.IsNotNull(enter);
        Assert.AreEqual(ModeIndicatorTransitionPhase.Enter, enter.Phase);
        Assert.AreEqual(TimeSpan.FromMilliseconds(100), enter.PhaseDuration);
    }

    [TestMethod]
    public void Mode_indicator_rejects_a_stale_interrupted_exit_completion()
    {
        var coordinator = new ModeIndicatorTransitionCoordinator();
        var motion = PrototypeMotionProfile.Resolve(reducedMotion: false);
        var interrupted = coordinator.BeginMode(PrototypePerformanceMode.Gaming, motion, animate: true);
        var latest = coordinator.BeginMode(PrototypePerformanceMode.Turbo, motion, animate: true);

        Assert.IsNull(coordinator.CompleteExit(interrupted.Version));
        Assert.IsFalse(coordinator.IsCurrent(interrupted));
        Assert.IsTrue(coordinator.IsCurrent(latest));
    }

    [TestMethod]
    public void Mode_indicator_reduced_motion_snaps_without_indicator_phases()
    {
        var plan = new ModeIndicatorTransitionCoordinator().BeginMode(
            PrototypePerformanceMode.Custom,
            PrototypeMotionProfile.Resolve(reducedMotion: true),
            animate: true);

        Assert.AreEqual(ModeIndicatorTransitionPhase.Snap, plan.Phase);
        Assert.AreEqual(TimeSpan.Zero, plan.ExitDuration);
        Assert.AreEqual(TimeSpan.Zero, plan.EnterDuration);
        Assert.AreEqual(TimeSpan.Zero, plan.ChildTotalDuration);
    }

    [TestMethod]
    public void Mode_indicator_persists_tier_and_profile_selection_across_reentry()
    {
        var coordinator = new ModeIndicatorTransitionCoordinator();
        var motion = PrototypeMotionProfile.Resolve(reducedMotion: false);
        var tier = coordinator.BeginChild("Quiet", motion, animate: true);
        Assert.AreEqual(TimeSpan.FromMilliseconds(180), tier.ChildTotalDuration);
        coordinator.BeginChild("Profile3", motion, animate: false);

        coordinator.BeginMode(PrototypePerformanceMode.Office, motion, animate: false);
        var turbo = coordinator.BeginMode(PrototypePerformanceMode.Turbo, motion, animate: false);
        coordinator.BeginMode(PrototypePerformanceMode.Office, motion, animate: false);
        var custom = coordinator.BeginMode(PrototypePerformanceMode.Custom, motion, animate: false);

        Assert.AreEqual("Quiet", turbo.TurboTier);
        Assert.AreEqual("Profile3", custom.CustomProfile);
    }

    [TestMethod]
    public void Mode_card_surface_plan_fades_old_selection_and_enters_target_on_one_timeline()
    {
        var plan = ModeCardSurfacePlan.Begin(
            PrototypePerformanceMode.Office,
            PrototypePerformanceMode.Turbo,
            animate: true,
            TimeSpan.FromMilliseconds(220));

        Assert.AreEqual(0d, plan.TargetOpacity(PrototypePerformanceMode.Office));
        Assert.AreEqual(1d, plan.TargetOpacity(PrototypePerformanceMode.Turbo));
        Assert.AreEqual(0d, plan.TargetOpacity(PrototypePerformanceMode.Custom));
        Assert.AreEqual(TimeSpan.FromMilliseconds(220), plan.Duration);
        Assert.IsFalse(plan.Snap);

        var reduced = ModeCardSurfacePlan.Begin(
            PrototypePerformanceMode.Turbo,
            PrototypePerformanceMode.Custom,
            animate: false,
            TimeSpan.FromMilliseconds(280));
        Assert.IsTrue(reduced.Snap);
        Assert.AreEqual(TimeSpan.Zero, reduced.Duration);
    }

    [TestMethod]
    public void Strong_cooling_motion_has_animated_enter_exit_and_reduced_motion_snap()
    {
        var enter = StrongCoolingMotion.Resolve(enabled: true, reducedMotion: false);
        var exit = StrongCoolingMotion.Resolve(enabled: false, reducedMotion: false);
        var reduced = StrongCoolingMotion.Resolve(enabled: false, reducedMotion: true);

        Assert.AreEqual(1d, enter.LayerOpacity);
        Assert.AreEqual(.18d, enter.AmbientOpacity);
        Assert.AreEqual(TimeSpan.FromMilliseconds(90), enter.LayerDuration);
        Assert.AreEqual(TimeSpan.FromMilliseconds(42), enter.Stagger);
        Assert.AreEqual(0d, exit.LayerOpacity);
        Assert.AreEqual(0d, exit.AmbientOpacity);
        Assert.AreEqual(TimeSpan.FromMilliseconds(70), exit.LayerDuration);
        Assert.AreEqual(TimeSpan.FromMilliseconds(28), exit.Stagger);
        Assert.AreEqual(TimeSpan.Zero, reduced.Duration);
    }

    [TestMethod]
    public void Strong_cooling_uses_selected_c_raster_layers_without_vector_frost()
    {
        var hero = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "HomeHero.xaml");
        var document = XDocument.Parse(hero);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var frost = document.Descendants()
            .Single(element => (string?)element.Attribute(xaml + "Name") == "StrongCoolingFreezeLayer");

        Assert.AreEqual(8, frost.Descendants(presentation + "Image").Count());
        for (var frame = 1; frame <= 8; frame++)
            StringAssert.Contains(hero, $"Assets/StrongCooling/Frames/Frame{frame:00}.png");
        Assert.IsFalse(frost.ToString().Contains("TranslateX", StringComparison.Ordinal));
        Assert.IsFalse(frost.ToString().Contains("TranslateY", StringComparison.Ordinal));
        Assert.AreEqual(0, frost.Descendants(presentation + "Path").Count());
        Assert.AreEqual(0, frost.Descendants(presentation + "Canvas").Count());
        Assert.IsFalse(hero.Contains("StrongCoolingFrostBranches", StringComparison.Ordinal));
        Assert.IsFalse(hero.Contains("StrongCoolingFrostVeil", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Approved_home_repair_uses_semantic_type_and_state_safe_layers()
    {
        var theme = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "PrototypeThemeResources.xaml");
        var hero = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "HomeHero.xaml");
        var modeBar = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "HomeModeBar.xaml");
        var monitor = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "MonitorPanel.xaml");

        foreach (var style in new[]
                 {
                     "PrototypeHeroToggleTextStyle", "PrototypeNavigationTextStyle", "PrototypeModeTextStyle",
                     "PrototypeTierTextStyle", "PrototypeDataLabelTextStyle", "PrototypeDataValueTextStyle",
                 })
            StringAssert.Contains(theme, $"x:Key=\"{style}\"");
        StringAssert.Contains(theme, "x:Name=\"NavSelectedHoverOverlay\"");
        StringAssert.Contains(modeBar, "x:Name=\"ModeIconScale\"");
        Assert.IsFalse(theme.Contains("ModeContentScale", StringComparison.Ordinal));
        StringAssert.Contains(theme, "x:Name=\"QuickTint\"");
        StringAssert.Contains(theme, "x:Name=\"QuickGlow\"");
        StringAssert.Contains(hero, "x:Name=\"HeroSelectionPlate\"");
        StringAssert.Contains(hero, "x:Name=\"RadarEnergyFill\"");
        StringAssert.Contains(hero, "x:Name=\"StrongCoolingFrame01\"");
        StringAssert.Contains(hero, "x:Name=\"StrongCoolingFrame08\"");
        Assert.IsFalse(hero.Contains("RenderTransformOrigin=\"1,1\"", StringComparison.Ordinal));
        Assert.IsFalse(hero.Contains("StrongCoolingFrostBloom", StringComparison.Ordinal));
        StringAssert.Contains(monitor, "Background=\"{StaticResource PrototypeControlAcrylicBrush}\"");
    }

    [TestMethod]
    public void Performance_workspace_has_fixed_profiles_and_scrollable_risk_controls()
    {
        var workspace = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "PerformanceWorkspaceV2.xaml");
        var picker = ReadSource("src", "Jiaolong.ControlCenter", "Controls", "ModePresetPicker.xaml");

        StringAssert.Contains(workspace, "x:Name=\"PerformancePageScrollViewer\"");
        StringAssert.Contains(workspace, "<shared:PagePresetToolbar");
        Assert.IsFalse(workspace.Contains("x:Name=\"ProfileScope\"", StringComparison.Ordinal));
        foreach (var profile in new[] { "Office", "Gaming", "Turbo", "Custom1", "Custom2", "Custom3" })
            StringAssert.Contains(picker, $"Tag=\"{profile}\"");
        foreach (var field in new[] { "TemperatureRow", "SustainedPowerRow", "BurstPowerRow", "FrequencyRow" })
            StringAssert.Contains(workspace, $"x:Name=\"{field}\"");
        StringAssert.Contains(workspace, "Title=\"曲线优化\"");
        StringAssert.Contains(workspace, "HelpEffect=");
        StringAssert.Contains(workspace, "<prototype:AdvancedCpuTuningWorkspaceV2");
        StringAssert.Contains(workspace, "UseRequested=\"OnUsePresetClick\"");
        Assert.IsFalse(workspace.Contains("高级曲线优化器", StringComparison.Ordinal));
        Assert.IsFalse(workspace.Contains("Expander", StringComparison.Ordinal));
        Assert.IsFalse(workspace.Contains("未接入", StringComparison.Ordinal));
        Assert.IsFalse(workspace.Contains("启用核心数\" Header", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Prototype_window_routes_performance_to_the_right_workspace_with_page_animation()
    {
        var shell = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "PrototypeWindow.xaml");
        var code = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "PrototypeWindow.xaml.cs");

        StringAssert.Contains(shell, "x:Name=\"PerformanceWorkspace\"");
        StringAssert.Contains(code, "destination == \"Performance\"");
        StringAssert.Contains(code, "ShowPage(\"Performance\")");
        StringAssert.Contains(code, "PrototypePageTransitionController");
        StringAssert.Contains(code, "BeginPageExitTransition");
        StringAssert.Contains(code, "BeginPageEnterTransition");
        Assert.IsFalse(code.Contains("TranslateX", StringComparison.Ordinal));
        StringAssert.Contains(code, "Opacity");
    }

    [TestMethod]
    public void Redundant_telemetry_labels_are_removed_while_connection_guards_remain()
    {
        var sidebar = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "HomeSidebar.xaml");
        var sidebarCode = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "HomeSidebar.xaml.cs");
        var windowCode = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "PrototypeWindow.xaml.cs");

        Assert.IsFalse(sidebar.Contains("TelemetryFreshnessText", StringComparison.Ordinal));
        Assert.IsFalse(sidebarCode.Contains("SetTelemetryFreshness", StringComparison.Ordinal));
        var tray = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "TrayQuickConsoleWindow.xaml");
        Assert.IsFalse(tray.Contains("TelemetryStatusText", StringComparison.Ordinal));
        StringAssert.Contains(windowCode, "TimeSpan.FromSeconds(1)");
        StringAssert.Contains(windowCode, "homeSession.Status != HomeSessionStatus.Connected");
    }

    private static string ReadSource(params string[] segments)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root.Parent is not null && !File.Exists(Path.Combine(root.FullName, "Jiaolong.ControlCenter.slnx"))) root = root.Parent;
        return File.ReadAllText(Path.Combine(new[] { root.FullName }.Concat(segments).ToArray()));
    }
}
