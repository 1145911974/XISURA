using Jiaolong_ControlCenter.Prototype;
using System.Xml.Linq;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class ModeVisualSystemTests
{
    [TestMethod]
    public void Catalog_maps_all_nine_locked_scenes_to_runtime_bundles_and_families()
    {
        Assert.AreEqual(9, ModeVisualCatalog.All.Count);
        Assert.AreEqual(4, ModeVisualCatalog.All.Count(scene => scene.Family == ModeSceneFamily.Flow));
        Assert.AreEqual(4, ModeVisualCatalog.All.Count(scene => scene.Family == ModeSceneFamily.Angular));
        Assert.AreEqual(1, ModeVisualCatalog.All.Count(scene => scene.Family == ModeSceneFamily.Volume));

        foreach (var scene in ModeVisualCatalog.All)
        {
            StringAssert.StartsWith(scene.PlateAsset, "ms-appx:///Assets/ModeScenes/");
            StringAssert.EndsWith(scene.PlateAsset, "/plate-16x10.png");
            StringAssert.EndsWith(scene.GuideAsset, "/guide.png");
            Assert.IsFalse(scene.PlateAsset.Contains("HomeBackdrop", StringComparison.Ordinal));
        }

        var catalogCode = File.ReadAllText(SourcePath(
            "src", "Jiaolong.ControlCenter", "Prototype", "ModeVisualCatalog.cs"));
        Assert.IsFalse(catalogCode.Contains("BaseAsset", StringComparison.Ordinal));
        Assert.IsFalse(catalogCode.Contains("HomeBackdrop", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Catalog_avoids_hyphenated_resource_qualifier_directories()
    {
        foreach (var scene in ModeVisualCatalog.All)
        {
            var folder = scene.PlateAsset.Split('/')[^2];
            Assert.IsFalse(folder.Contains('-', StringComparison.Ordinal), scene.PlateAsset);
        }
    }

    [TestMethod]
    [DataRow("office-orbital-focus", "office-constellation-flow", ModeTransitionKind.FlowMorph, 240)]
    [DataRow("game-facet-arena", "custom-preset-1-graphite-strata", ModeTransitionKind.AngularMorph, 240)]
    [DataRow("volume-synthetic", "game-volumetric-cloud", ModeTransitionKind.VolumeMorph, 240)]
    [DataRow("game-facet-arena", "game-volumetric-cloud", ModeTransitionKind.AngularVolume, 320)]
    [DataRow("custom-preset-2-liquid-jade", "game-volumetric-cloud", ModeTransitionKind.FlowVolume, 320)]
    [DataRow("office-orbital-focus", "turbo-digital-fault-planes", ModeTransitionKind.FlowAngular, 340)]
    public void Route_resolver_covers_same_cross_and_office_turbo_pairs(
        string fromKey, string toKey, ModeTransitionKind kind, int milliseconds)
    {
        var route = ModeTransitionRoute.Resolve(
            RouteScene(fromKey),
            RouteScene(toKey),
            reducedMotion: false);

        Assert.AreEqual(kind, route.Kind);
        Assert.AreEqual(TimeSpan.FromMilliseconds(milliseconds), route.Duration);
        Assert.IsTrue(route.Duration <= TimeSpan.FromMilliseconds(360));
    }

    private static ModeVisualScene RouteScene(string key)
    {
        var scene = ModeVisualCatalog.ForKey(key == "volume-synthetic" ? "game-volumetric-cloud" : key);
        return key == "volume-synthetic" ? scene with { Key = key } : scene;
    }

    [TestMethod]
    public void Final_motion_profile_keeps_cards_immediate_and_backgrounds_tightly_coupled()
    {
        var profile = ModeMotionProfile.Final;

        Assert.AreEqual(TimeSpan.FromMilliseconds(220), profile.CardDuration);
        Assert.AreEqual(TimeSpan.FromMilliseconds(420), profile.BackgroundDuration);

        var windowCode = File.ReadAllText(SourcePath(
            "src", "Jiaolong.ControlCenter", "Prototype", "PrototypeWindow.xaml.cs"));
        Assert.IsFalse(windowCode.Contains("JIAOLONG_MOTION_PREVIEW", StringComparison.Ordinal));
        Assert.IsTrue(
            windowCode.IndexOf("ModeAmbientBackdrop.ApplyTransition(plan)", StringComparison.Ordinal) <
            windowCode.IndexOf("HomeModeBar.ApplyMode", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Transition_controller_uses_final_background_duration()
    {
        var office = ModeVisualCatalog.ForKey("office-orbital-focus");
        var turbo = ModeVisualCatalog.ForKey("turbo-digital-fault-planes");
        var controller = new ModeTransitionController();

        var plan = controller.Begin(office, turbo, PrototypeMotionProfile.Resolve(false));

        Assert.AreEqual(TimeSpan.FromMilliseconds(420), plan.Duration);
    }

    [TestMethod]
    public void Background_target_is_visible_early_without_losing_final_continuity()
    {
        var route = ModeTransitionRoute.Resolve(
            ModeVisualCatalog.ForKey("office-orbital-focus"),
            ModeVisualCatalog.ForKey("turbo-digital-fault-planes"),
            reducedMotion: false,
            TimeSpan.FromMilliseconds(420));

        var early = ModeTransitionFrame.Resolve(route, .2f);
        var final = ModeTransitionFrame.Resolve(route, 1);

        Assert.IsTrue(early.TargetOpacity >= .2f, $"Target opacity was only {early.TargetOpacity} at 20% progress.");
        Assert.AreEqual(0f, final.CurrentOpacity);
        Assert.AreEqual(1f, final.TargetOpacity);
    }

    [TestMethod]
    public void Selector_randomizes_first_pick_then_never_repeats_a_pooled_scene()
    {
        var selector = new ModeBackgroundSelector(_ => 0);

        foreach (var mode in new[]
                 {
                     PrototypePerformanceMode.Office,
                     PrototypePerformanceMode.Gaming,
                     PrototypePerformanceMode.Turbo,
                 })
        {
            var first = selector.Select(mode, "Profile1");
            var second = selector.Select(mode, "Profile1");
            var third = selector.Select(mode, "Profile1");

            Assert.AreNotEqual(first.Key, second.Key);
            Assert.AreEqual(first.Key, third.Key);
        }

        Assert.AreEqual("custom-preset-2-liquid-jade", selector.Select(PrototypePerformanceMode.Custom, "Profile2").Key);
        Assert.AreEqual("custom-preset-2-liquid-jade", selector.Select(PrototypePerformanceMode.Custom, "Profile2").Key);
    }

    [TestMethod]
    [DataRow("Profile1", "custom-preset-1-graphite-strata", "#8C949F")]
    [DataRow("Profile2", "custom-preset-2-liquid-jade", "#27D980")]
    [DataRow("Profile3", "custom-preset-3-purple-filaments", "#9A5CFF")]
    public void Custom_profiles_resolve_to_independent_scenes_and_accents(string profile, string key, string accent)
    {
        var scene = ModeVisualCatalog.ForCustomProfile(profile);

        Assert.AreEqual(key, scene.Key);
        Assert.AreEqual(profile, scene.CustomProfile);
        Assert.AreEqual(accent, scene.Theme.AccentHex);
    }

    [TestMethod]
    public void Transition_is_latest_request_wins_bounded_and_reduced_motion_safe()
    {
        var controller = new ModeTransitionController();
        var office = ModeVisualCatalog.All.Single(scene => scene.Kind == ModeSceneKind.OrbitalFocus);
        var gaming = ModeVisualCatalog.All.Single(scene => scene.Kind == ModeSceneKind.FacetArena);
        var turbo = ModeVisualCatalog.All.Single(scene => scene.Kind == ModeSceneKind.DigitalFaultPlanes);

        var interrupted = controller.Begin(office, gaming, PrototypeMotionProfile.Resolve(false));
        var latest = controller.Begin(gaming, turbo, PrototypeMotionProfile.Resolve(false));

        Assert.IsFalse(controller.IsCurrent(interrupted.Version));
        Assert.IsTrue(controller.IsCurrent(latest.Version));
        Assert.AreEqual(TimeSpan.FromMilliseconds(420), latest.Duration);
        Assert.IsTrue(latest.Duration <= TimeSpan.FromMilliseconds(420));
        Assert.AreEqual(2, latest.MaximumLiveVisuals);

        var reduced = controller.Begin(turbo, office, PrototypeMotionProfile.Resolve(true));
        Assert.IsTrue(reduced.Snap);
        Assert.AreEqual(TimeSpan.Zero, reduced.Duration);

        var firstLoad = controller.Begin(null, office, PrototypeMotionProfile.Resolve(false));
        Assert.IsTrue(firstLoad.Snap);
        Assert.AreEqual(TimeSpan.Zero, firstLoad.Duration);
    }

    [TestMethod]
    public void Ambient_backdrop_uses_only_exact_current_and_target_plates()
    {
        var document = XDocument.Load(SourcePath(
            "src", "Jiaolong.ControlCenter", "Prototype", "Controls", "ModeAmbientBackdrop.xaml"));
        var code = File.ReadAllText(SourcePath(
            "src", "Jiaolong.ControlCenter", "Prototype", "Controls", "ModeAmbientBackdrop.xaml.cs"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var namedHosts = document.Descendants()
            .Select(element => (string?)element.Attribute(x + "Name"))
            .Where(name => name is "CurrentPlate" or "TargetPlate" or "ConvergenceBridge")
            .ToArray();

        CollectionAssert.AreEquivalent(
            new[] { "CurrentPlate", "TargetPlate" },
            namedHosts);
        StringAssert.Contains(code, "scene.PlateAsset");
        Assert.IsFalse(code.Contains("AddEllipse", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("AddPolygon", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("AddPolyline", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Ambient_backdrop_uses_full_duration_and_preserves_visible_plate_when_interrupted()
    {
        var code = File.ReadAllText(SourcePath(
            "src", "Jiaolong.ControlCenter", "Prototype", "Controls", "ModeAmbientBackdrop.xaml.cs"));

        StringAssert.Contains(code, "var duration = plan.Duration;");
        Assert.IsFalse(code.Contains("AddOpacity(backgroundStoryboard, CurrentPlate, 1, 0", StringComparison.Ordinal));
        StringAssert.Contains(code, "TargetPlate.Opacity >= .5");
        StringAssert.Contains(code, "PreserveVisiblePlate()");
        Assert.IsTrue(
            code.IndexOf("PreserveVisiblePlate();", StringComparison.Ordinal) <
            code.IndexOf("TargetPlate.Source = target.Image;", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("Math.Min(180", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Transition_frame_keeps_exact_endpoints_and_uses_staggered_local_trajectories()
    {
        var forward = ModeTransitionRoute.Resolve(
            ModeVisualCatalog.ForKey("office-orbital-focus"),
            ModeVisualCatalog.ForKey("turbo-digital-fault-planes"),
            reducedMotion: false);
        var reverse = ModeTransitionRoute.Resolve(
            ModeVisualCatalog.ForKey("turbo-digital-fault-planes"),
            ModeVisualCatalog.ForKey("office-orbital-focus"),
            reducedMotion: false);

        var start = ModeTransitionFrame.Resolve(forward, 0);
        var early = ModeTransitionFrame.Resolve(forward, .30f);
        var middle = ModeTransitionFrame.Resolve(forward, .5f);
        var reversedMiddle = ModeTransitionFrame.Resolve(reverse, .5f);
        var end = ModeTransitionFrame.Resolve(forward, 1);

        Assert.AreEqual(1f, start.CurrentOpacity);
        Assert.AreEqual(0f, start.TargetOpacity);
        Assert.AreEqual(0f, start.CurrentMaterialOpacity);
        Assert.AreEqual(0f, start.TargetMaterialOpacity);
        Assert.AreEqual(0f, start.CurrentDisplacement);
        Assert.AreEqual(0f, start.TargetDisplacement);
        Assert.AreEqual(1f, start.CurrentReveal);
        Assert.AreEqual(0f, start.TargetReveal);
        Assert.IsGreaterThan(.20f, early.TargetReveal);
        Assert.AreEqual(0f, end.CurrentOpacity, .0001f);
        Assert.AreEqual(1f, end.TargetOpacity, .0001f);
        Assert.AreEqual(1f, middle.CurrentOpacity + middle.TargetOpacity, .0001f);
        Assert.IsGreaterThan(0f, middle.CurrentMaterialOpacity);
        Assert.IsGreaterThan(0f, middle.TargetMaterialOpacity);
        Assert.AreEqual(0f, end.CurrentMaterialOpacity, .0001f);
        Assert.AreEqual(0f, end.TargetMaterialOpacity, .0001f);
        Assert.AreEqual(0f, end.CurrentReveal);
        Assert.AreEqual(1f, end.TargetReveal);
        Assert.AreEqual(forward.RevealSoftness, middle.RevealSoftness);
        Assert.AreEqual(forward.NoiseWeight, middle.NoiseWeight);
        Assert.AreEqual(0f, end.CurrentDisplacement, .0001f);
        Assert.AreEqual(0f, end.TargetDisplacement, .0001f);
        Assert.AreEqual(-middle.CurrentDisplacement, reversedMiddle.CurrentDisplacement, .0001f);
        Assert.AreEqual(-middle.TargetDisplacement, reversedMiddle.TargetDisplacement, .0001f);
        Assert.IsTrue(Math.Abs(middle.CurrentDisplacement) > 1);
        Assert.IsTrue(Math.Abs(middle.CurrentDisplacement) <= 18);
        Assert.AreNotEqual(Math.Abs(early.CurrentDisplacement), Math.Abs(early.TargetDisplacement), .0001f);
    }

    [TestMethod]
    public void Ambient_backdrop_uses_one_seam_free_xaml_sampling_path()
    {
        var xaml = File.ReadAllText(SourcePath(
            "src", "Jiaolong.ControlCenter", "Prototype", "Controls", "ModeAmbientBackdrop.xaml"));
        var code = File.ReadAllText(SourcePath(
            "src", "Jiaolong.ControlCenter", "Prototype", "Controls", "ModeAmbientBackdrop.xaml.cs"));
        var shell = File.ReadAllText(SourcePath(
            "src", "Jiaolong.ControlCenter", "Prototype", "PrototypeWindow.xaml"));
        Assert.AreEqual(2, xaml.Split("Stretch=\"Fill\"", StringSplitOptions.None).Length - 1);
        Assert.IsFalse(xaml.Contains("CanvasControl", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("Microsoft.Graphics.Canvas", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("DrawImage", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("GuideAsset", StringComparison.Ordinal));
        StringAssert.Contains(code, "backgroundStoryboard");
        StringAssert.Contains(code, "ImageOpened +=");
        StringAssert.Contains(code, "ImageFailed +=");
        Assert.IsFalse(code.Contains("foreach (var scene in ModeVisualCatalog.All)", StringComparison.Ordinal));
        StringAssert.Contains(code, "TrimPlateCache()");
        StringAssert.Contains(code, "ImageOpened -= OnPlateOpened");
        StringAssert.Contains(code, "if (!target.IsReady)");
        Assert.IsTrue(
            code.IndexOf("TargetPlate.Source = target.Image", StringComparison.Ordinal) <
            code.IndexOf("if (!target.IsReady)", StringComparison.Ordinal),
            "The transparent target image must enter the visual tree before WinUI can lazily decode it.");
        StringAssert.Contains(code, "plan.Version != latestVersion");
        StringAssert.Contains(code, "CurrentPlate.Opacity");
        StringAssert.Contains(code, "TargetPlate.Opacity");
        Assert.IsFalse(shell.Contains("SidebarEdgeBlend", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Win2d_scene_assets_resolve_ms_appx_uris_to_unpackaged_file_paths()
    {
        var baseDirectory = @"C:\Jiaolong";
        var resolved = ModeSceneAssetPath.Resolve(
            "ms-appx:///Assets/ModeScenes/office_orbital_focus/plate-16x10.png",
            baseDirectory);

        Assert.AreEqual(
            Path.Combine(baseDirectory, "Assets", "ModeScenes", "office_orbital_focus", "plate-16x10.png"),
            resolved);
        Assert.Throws<ArgumentException>(() => ModeSceneAssetPath.Resolve("https://example.test/plate.png", "C:\\Jiaolong"));
    }

    [TestMethod]
    public void Turbo_tiers_change_strength_without_selecting_another_plate()
    {
        var normal = TurboBackgroundIntensity.Resolve("Normal");
        var quiet = TurboBackgroundIntensity.Resolve("Quiet");
        var extreme = TurboBackgroundIntensity.Resolve("Extreme");

        Assert.IsTrue(quiet.DisplacementScale < normal.DisplacementScale);
        Assert.IsTrue(quiet.ShadeOpacity > normal.ShadeOpacity);
        Assert.IsTrue(extreme.DisplacementScale > normal.DisplacementScale);
        Assert.IsTrue(extreme.GlowOpacity > normal.GlowOpacity);
        Assert.Throws<ArgumentOutOfRangeException>(() => TurboBackgroundIntensity.Resolve("Dense"));

        var backdrop = File.ReadAllText(SourcePath(
            "src", "Jiaolong.ControlCenter", "Prototype", "Controls", "ModeAmbientBackdrop.xaml"));
        var backdropCode = File.ReadAllText(SourcePath(
            "src", "Jiaolong.ControlCenter", "Prototype", "Controls", "ModeAmbientBackdrop.xaml.cs"));
        var modeBarCode = File.ReadAllText(SourcePath(
            "src", "Jiaolong.ControlCenter", "Prototype", "Controls", "HomeModeBar.xaml.cs"));

        StringAssert.Contains(backdrop, "x:Name=\"TurboTierShade\"");
        StringAssert.Contains(backdrop, "x:Name=\"TurboTierGlow\"");
        Assert.IsFalse(backdropCode.Contains("IntensityScale", StringComparison.Ordinal));
        StringAssert.Contains(backdropCode, "var currentShade = TurboTierShade.Opacity");
        StringAssert.Contains(backdropCode, "From = from");
        StringAssert.Contains(modeBarCode, "TurboTierRequested");
    }

    [TestMethod]
    [DataRow(false, false, false, false)]
    [DataRow(true, false, false, true)]
    [DataRow(false, true, false, true)]
    [DataRow(false, false, true, true)]
    public void Ambient_activity_pauses_for_each_required_condition(
        bool deactivated,
        bool minimized,
        bool energySaver,
        bool expected)
    {
        Assert.AreEqual(expected, ModeAmbientActivityPolicy.ShouldPause(deactivated, minimized, energySaver));
        Assert.IsFalse(ModeAmbientActivityPolicy.DecorationsVisible(highContrast: true));
        Assert.IsTrue(ModeAmbientActivityPolicy.DecorationsVisible(highContrast: false));
    }

    [TestMethod]
    public void Toggle_templates_clip_the_indicator_and_restore_every_unchecked_property()
    {
        var document = XDocument.Load(SourcePath(
            "src", "Jiaolong.ControlCenter", "Prototype", "PrototypeThemeResources.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        foreach (var pair in new[]
                 {
                     (Root: "StrongCoolingRoot", Content: "StrongCoolingContent", Accent: "StrongCoolingAccent"),
                 })
        {
            var root = document.Descendants().Single(element => (string?)element.Attribute(x + "Name") == pair.Root);
            var accent = root.Descendants().Single(element => (string?)element.Attribute(x + "Name") == pair.Accent);
            Assert.AreEqual("1,0,1,1", (string?)accent.Attribute("Margin"));
            Assert.AreEqual("Grid", root.Parent?.Name.LocalName);
            Assert.IsTrue(root.Parent?.Elements(p + "VisualStateManager.VisualStateGroups").Any());

            var style = root.Ancestors(p + "Style").Single();
            var uncheckedState = style.Descendants(p + "VisualState")
                .Single(state => (string?)state.Attribute(x + "Name") == "Unchecked");
            var targets = uncheckedState.Descendants(p + "Setter")
                .Select(setter => (string?)setter.Attribute("Target"))
                .ToArray();
            CollectionAssert.IsSubsetOf(
                new[]
                {
                    $"{pair.Root}.Background",
                    $"{pair.Root}.BorderBrush",
                    $"{pair.Content}.Foreground",
                    $"{pair.Content}.Opacity",
                    $"{pair.Accent}.Visibility",
                },
                targets);
        }

        Assert.IsNotNull(document.Descendants().SingleOrDefault(element => (string?)element.Attribute(x + "Name") == "QuickTint"));
        Assert.IsNotNull(document.Descendants().SingleOrDefault(element => (string?)element.Attribute(x + "Name") == "QuickGlow"));
    }

    [TestMethod]
    public void Quick_toggle_state_is_the_single_explicit_local_boolean_source()
    {
        var state = new HomeQuickToggleState();

        Assert.IsFalse(state.IsEnabled(HomeQuickToggleState.Wifi));
        state.Set(HomeQuickToggleState.Wifi, true);
        Assert.IsTrue(state.IsEnabled(HomeQuickToggleState.Wifi));
        state.Set(HomeQuickToggleState.Wifi, false);
        Assert.IsFalse(state.IsEnabled(HomeQuickToggleState.Wifi));
        Assert.Throws<ArgumentOutOfRangeException>(() => state.Set("Unknown", true));
    }

    [TestMethod]
    public void Strong_cooling_and_home_toggles_expose_synchronized_two_layer_states()
    {
        var sidebar = XDocument.Load(SourcePath(
            "src", "Jiaolong.ControlCenter", "Prototype", "Controls", "HomeSidebar.xaml"));
        var sidebarCode = File.ReadAllText(SourcePath(
            "src", "Jiaolong.ControlCenter", "Prototype", "Controls", "HomeSidebar.xaml.cs"));
        var interactiveControls = File.ReadAllText(SourcePath(
            "src", "Jiaolong.ControlCenter", "Prototype", "Controls", "PrototypeInteractiveControls.cs"));
        var hero = File.ReadAllText(SourcePath(
            "src", "Jiaolong.ControlCenter", "Prototype", "Controls", "HomeHero.xaml"));
        var heroCode = File.ReadAllText(SourcePath(
            "src", "Jiaolong.ControlCenter", "Prototype", "Controls", "HomeHero.xaml.cs"));
        var theme = XDocument.Load(SourcePath(
            "src", "Jiaolong.ControlCenter", "Prototype", "PrototypeThemeResources.xaml"));
        XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XNamespace controls = "using:Jiaolong_ControlCenter.Prototype.Controls";

        var toggles = sidebar.Descendants(p + "ToggleButton").ToArray();
        Assert.AreEqual(0, toggles.Length);
        StringAssert.Contains(sidebar.ToString(), "x:Name=\"QuickSettingsGrid\"");
        StringAssert.Contains(sidebarCode, "PrototypeToggleButton");
        StringAssert.Contains(sidebarCode, "HomeQuickSettingSemantics.ToUiEnabled");
        StringAssert.Contains(sidebarCode, "toggle.IsEnabled = true;");
        Assert.IsNotNull(sidebar.Descendants(controls + "PrototypeButton").SingleOrDefault(button => (string?)button.Attribute("Tag") == "编辑快捷菜单"));
        Assert.IsFalse(sidebar.Descendants(p + "Button").Any(button => (string?)button.Attribute("Tag") == "添加快捷项"));
        Assert.IsFalse(sidebar.Descendants(p + "Button").Any(button => (string?)button.Attribute("Tag") == "快捷项排序"));
        Assert.IsFalse(sidebar.Descendants(p + "ToggleButton").Any(toggle => (string?)toggle.Attribute("Tag") == "Osd"));
        StringAssert.Contains(sidebarCode, "QuickMenuCatalog.CreateDefault");

        StringAssert.Contains(sidebarCode, "VisualStateManager.GoToState");
        StringAssert.Contains(interactiveControls, "InputSystemCursorShape.Hand");
        Assert.IsFalse(sidebar.ToString().Contains("Fn 锁", StringComparison.Ordinal));
        StringAssert.Contains(sidebarCode, "AutomationProperties.SetName");

        var strongStyle = theme.Descendants(p + "Style")
            .Single(style => (string?)style.Attribute(x + "Key") == "PrototypeStrongCoolingToggleStyle");
        var checkedState = strongStyle.Descendants(p + "VisualState")
            .Single(state => (string?)state.Attribute(x + "Name") == "Checked");
        var checkedSetters = checkedState.Descendants(p + "Setter")
            .ToDictionary(setter => (string)setter.Attribute("Target")!, setter => (string)setter.Attribute("Value")!);
        Assert.AreEqual("{StaticResource PrototypeControlAcrylicBrush}", checkedSetters["StrongCoolingRoot.Background"]);
        Assert.AreEqual("#42FFFFFF", checkedSetters["StrongCoolingRoot.BorderBrush"]);
        Assert.AreEqual("White", checkedSetters["StrongCoolingContent.Foreground"]);
        var strongStyleText = strongStyle.ToString();
        Assert.IsFalse(strongStyleText.Contains("StrongCoolingEnabledAcrylicBrush", StringComparison.Ordinal));
        Assert.IsFalse(strongStyleText.Contains("ModeAccentBrush", StringComparison.Ordinal));
        Assert.IsFalse(hero.Contains("StrongCoolingFrost.png", StringComparison.Ordinal));
        StringAssert.Contains(hero, "x:Name=\"StrongCoolingAmbientLayer\"");
        StringAssert.Contains(hero, "x:Name=\"StrongCoolingFreezeLayer\"");
        for (var frame = 1; frame <= 8; frame++)
            StringAssert.Contains(hero, $"x:Name=\"StrongCoolingFrame{frame:00}\"");
        Assert.IsFalse(hero.Contains("M158,148 L72,148", StringComparison.Ordinal));
        var frostAnimationCode = heroCode[heroCode.IndexOf("private void AnimateFrost", StringComparison.Ordinal)..];
        Assert.IsFalse(frostAnimationCode.Contains("TranslateX", StringComparison.Ordinal));
        Assert.IsFalse(frostAnimationCode.Contains("TranslateY", StringComparison.Ordinal));
        StringAssert.Contains(heroCode, "FrostLayers()");
        StringAssert.Contains(heroCode, "target.Stagger");
        StringAssert.Contains(hero, "LinearGradientBrush");
        StringAssert.Contains(hero, "x:Name=\"AutomaticModeButton\"");
        StringAssert.Contains(hero, "x:Name=\"AutoModeHaloLayer\"");
        Assert.IsFalse(hero.Contains("StrongCoolingAccentBrush", StringComparison.Ordinal));
        Assert.IsFalse(heroCode.Contains("StrongCoolingIcon.Fill = enabled", StringComparison.Ordinal));
        Assert.IsFalse(heroCode.Contains("VisualStateManager.GoToState(StrongCoolingButton", StringComparison.Ordinal));
        StringAssert.Contains(heroCode, "SetFrostVisible");
    }

    [TestMethod]
    public void Mode_icons_use_white_outlines_with_mode_specific_ambient_glow()
    {
        var bar = XDocument.Load(SourcePath(
            "src", "Jiaolong.ControlCenter", "Prototype", "Controls", "HomeModeBar.xaml"));
        var hero = XDocument.Load(SourcePath(
            "src", "Jiaolong.ControlCenter", "Prototype", "Controls", "HomeHero.xaml"));
        var sources = bar.Descendants().Attributes("Source").Select(value => value.Value).ToArray();

        CollectionAssert.IsSubsetOf(
            new[]
            {
                "ms-appx:///Assets/Icons/HomeModeOfficeOutline.svg",
                "ms-appx:///Assets/Icons/HomeModeGamingOutline.svg",
                "ms-appx:///Assets/Icons/HomeModeTurboOutline.svg",
                "ms-appx:///Assets/Icons/HomeModeCustomOutline.svg",
            },
            sources);
        var barText = bar.ToString();
        Assert.IsFalse(barText.Contains("RadialGradientBrush", StringComparison.Ordinal));
        var barCode = File.ReadAllText(SourcePath(
            "src", "Jiaolong.ControlCenter", "Prototype", "Controls", "HomeModeBar.xaml.cs"));
        foreach (var accent in new[]
                 {
                     "0x16, 0x77, 0xFF", "0xFF, 0x8A, 0x1F",
                     "0xFF, 0x31, 0x41", "0x9A, 0x5C, 0xFF",
                 })
            StringAssert.Contains(barCode, accent);
        StringAssert.Contains(barCode, "icon.GetAlphaMask()");
        StringAssert.Contains(barCode, "AnimateModeIcon(button, 1, 0.7, 120)");
        StringAssert.Contains(hero.ToString(), "NavAutomationFilled.svg");
    }

    [TestMethod]
    public void Unpackaged_window_polls_high_contrast_without_the_unsupported_event_subscription()
    {
        var source = File.ReadAllText(SourcePath(
            "src", "Jiaolong.ControlCenter", "Prototype", "PrototypeWindow.xaml.cs"));

        StringAssert.Contains(source, "accessibilitySettings.HighContrast");
        Assert.IsFalse(source.Contains("HighContrastChanged +=", StringComparison.Ordinal));
    }

    private static string SourcePath(params string[] segments)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root.Parent is not null && !File.Exists(Path.Combine(root.FullName, "Jiaolong.ControlCenter.slnx")))
            root = root.Parent;
        return Path.Combine(new[] { root.FullName }.Concat(segments).ToArray());
    }
}
