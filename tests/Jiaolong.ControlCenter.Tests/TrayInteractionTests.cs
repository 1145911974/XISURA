using System;
using System.IO;
using System.Linq;
using Jiaolong_ControlCenter.Prototype;
using Jiaolong_ControlCenter.Services;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class TrayInteractionTests
{
    [TestMethod]
    public void Shell_selection_keeps_its_monitor_anchor_until_owner_dispatch()
    {
        foreach (var (x, y, notification) in new[] { (2500, 1400, 0x400), (-1800, -40, 0x202), (800, 900, 0x401) })
        {
            Action? queued = null;
            Windows.Graphics.PointInt32? received = null;
            var tray = new TrayIconService(_ => Assert.Fail("Selection must retain its anchor"), action => queued = action);
            var data = typeof(TrayIconService).GetField("data", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            data.SetValue(tray, Activator.CreateInstance(data.FieldType));
            tray.QuickConsoleRequested += point => received = point;
            var packedPoint = new nint(unchecked((int)((uint)(ushort)x | ((uint)(ushort)y << 16))));
            Assert.IsTrue(tray.HandleWindowMessage(0x8000, new nint(0x10000 | notification), packedPoint));
            Assert.IsNull(received);
            Assert.IsNotNull(queued);
            queued();
            Assert.AreEqual(x, received!.Value.X);
            Assert.AreEqual(y, received.Value.Y);
        }
    }

    [TestMethod]
    public void Acceptance_main_window_placement_cannot_override_tray_or_disable_dismissal()
    {
        var shell = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "PrototypeWindow.xaml.cs");
        var start = shell.IndexOf("private void ShowTrayQuickConsole(", StringComparison.Ordinal);
        var show = shell[start..shell.IndexOf("private TrayQuickConsoleWindow CreateTrayQuickConsole", start, StringComparison.Ordinal)];
        Assert.IsFalse(show.Contains("acceptanceSecondaryDisplay", StringComparison.Ordinal));
        Assert.IsFalse(show.Contains("IsModalActionPending = true", StringComparison.Ordinal));
        StringAssert.Contains(show, "ShowDocked(anchor)");
    }

    [TestMethod]
    public void Runtime_icons_use_six_approved_C_modes_and_keep_the_fixed_fallback()
    {
        foreach (var mode in Enum.GetValues<Jiaolong.Contracts.Models.ControlModeId>())
            Assert.AreEqual($"Assets\\Brand\\CModeIcons\\JiaolongC{mode}.ico", TrayIconAssetCatalog.For("explore", mode));
        Assert.AreEqual(TrayIconAssetCatalog.FixedBrandAsset, TrayIconAssetCatalog.For("explore", null));
        foreach (var mode in Enum.GetValues<Jiaolong.Contracts.Models.ControlModeId>())
            StringAssert.EndsWith(TrayIconAssetCatalog.ResolveAbsolute("root", "explore", mode), $"JiaolongC{mode}-runtime.ico");
        StringAssert.EndsWith(TrayIconAssetCatalog.ResolveAbsolute("root", "explore", null), "JiaolongWaveApp-runtime.ico");
        StringAssert.EndsWith(TrayIconAssetCatalog.ResolveAbsolute("root", "classic", Jiaolong.Contracts.Models.ControlModeId.Office), "JiaolongTrayIconOffice-tight.ico");
        Assert.AreEqual(TrayIconAssetCatalog.FixedBrandAsset, TrayIconAssetCatalog.For("explore", (Jiaolong.Contracts.Models.ControlModeId)999));
        Assert.AreEqual(TrayIconAssetCatalog.For(PrototypePerformanceMode.Office), TrayIconAssetCatalog.For("classic", Jiaolong.Contracts.Models.ControlModeId.Office));
    }

    [TestMethod]
    public void Quick_console_repeated_show_does_not_toggle_or_fade_the_surface()
    {
        var code = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "TrayQuickConsoleWindow.xaml.cs");
        StringAssert.Contains(code, "if (isVisible && !isClosing && !changingDisplay) return;");
        Assert.IsFalse(code.Contains("ConsoleSurface.Opacity = animate ? 0 : 1"));
        StringAssert.Contains(code, "CompositionTarget.Rendering");
        StringAssert.Contains(code, "AnimationsEnabled");
        StringAssert.Contains(code, "SetLayeredWindowAttributes");
        StringAssert.Contains(code, "HideAnimated()");
        StringAssert.Contains(code, "warmupFrames = 2");
        StringAssert.Contains(code, "isClosing ? 340 : 420");
    }

    [TestMethod]
    public void First_click_waits_and_flushes_as_quick_console()
    {
        var classifier = new TrayClickClassifier(TimeSpan.FromMilliseconds(250));

        Assert.AreEqual(TrayClickResult.None, classifier.RegisterClick(DateTimeOffset.UnixEpoch));
        Assert.AreEqual(TrayClickResult.None, classifier.Flush(DateTimeOffset.UnixEpoch.AddMilliseconds(100)));
        Assert.AreEqual(TrayClickResult.ShowQuickConsole, classifier.Flush(DateTimeOffset.UnixEpoch.AddMilliseconds(251)));
    }

    [TestMethod]
    public void Second_click_within_window_opens_main_and_clears_pending_click()
    {
        var classifier = new TrayClickClassifier(TimeSpan.FromMilliseconds(250));

        classifier.RegisterClick(DateTimeOffset.UnixEpoch);

        Assert.AreEqual(
            TrayClickResult.OpenMainWindow,
            classifier.RegisterClick(DateTimeOffset.UnixEpoch.AddMilliseconds(180)));
        Assert.AreEqual(TrayClickResult.None, classifier.Flush(DateTimeOffset.UnixEpoch.AddSeconds(1)));
    }

    [TestMethod]
    public void Click_after_window_starts_a_new_single_click_window()
    {
        var classifier = new TrayClickClassifier(TimeSpan.FromMilliseconds(250));

        classifier.RegisterClick(DateTimeOffset.UnixEpoch);

        Assert.AreEqual(TrayClickResult.ShowQuickConsole, classifier.Flush(DateTimeOffset.UnixEpoch.AddMilliseconds(251)));
        Assert.AreEqual(TrayClickResult.None, classifier.RegisterClick(DateTimeOffset.UnixEpoch.AddMilliseconds(300)));
        Assert.AreEqual(TrayClickResult.ShowQuickConsole, classifier.Flush(DateTimeOffset.UnixEpoch.AddMilliseconds(551)));
    }

    [TestMethod]
    public void Native_tray_service_owns_custom_icon_gesture_and_menu_contract()
    {
        var source = ReadSource("src", "Jiaolong.ControlCenter", "Services", "TrayIconService.cs");

        StringAssert.Contains(source, "ShowQuickConsole");
        StringAssert.Contains(source, "WmLButtonUp");
        StringAssert.Contains(source, "LoadImage");
        StringAssert.Contains(source, "ContextMenuRequested");
        Assert.IsFalse(source.Contains("TrackPopupMenu", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("显示主页", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Native_tray_service_handles_the_full_right_button_callback_sequence()
    {
        var source = ReadSource("src", "Jiaolong.ControlCenter", "Services", "TrayIconService.cs");

        StringAssert.Contains(source, "WmRButtonUp");
        StringAssert.Contains(source, "GetCursorPos");
        StringAssert.Contains(source, "contextMenuShowing");
    }

    [TestMethod]
    public void Native_tray_service_uses_a_dedicated_message_window_for_shell_callbacks()
    {
        var source = ReadSource("src", "Jiaolong.ControlCenter", "Services", "TrayIconService.cs");

        StringAssert.Contains(source, "callbackWindowHandle");
        StringAssert.Contains(source, "HwndMessage");
        StringAssert.Contains(source, "CreateWindowEx");
        StringAssert.Contains(source, "TrayWindowProcedure");
    }

    [TestMethod]
    public void Prototype_window_surfaces_tray_registration_failure_for_diagnostics()
    {
        var source = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "PrototypeWindow.xaml.cs");

        StringAssert.Contains(source, "var trayRegistered = tray.Show(");
        StringAssert.Contains(source, "托盘图标注册失败");
    }

    [TestMethod]
    public void Adaptive_dragon_icon_assets_are_present_for_runtime_and_packaging()
    {
        var root = FindRepositoryRoot();
        var iconPng = Path.Combine(root, "src", "Jiaolong.ControlCenter", "Assets", "Brand", "JiaolongAppIcon.png");
        var iconIco = Path.Combine(root, "src", "Jiaolong.ControlCenter", "Assets", "AppIcon.ico");
        var project = File.ReadAllText(Path.Combine(root, "src", "Jiaolong.ControlCenter", "Jiaolong.ControlCenter.csproj"));

        Assert.IsTrue(File.Exists(iconPng));
        Assert.IsTrue(File.Exists(iconIco));
        CollectionAssert.AreEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, File.ReadAllBytes(iconPng)[..4]);
        CollectionAssert.AreEqual(new byte[] { 0x00, 0x00, 0x01, 0x00 }, File.ReadAllBytes(iconIco)[..4]);
        var png = File.ReadAllBytes(iconPng);
        Assert.AreEqual(1088, ReadPngDimension(png, 16));
        Assert.AreEqual(1088, ReadPngDimension(png, 20));
        StringAssert.Contains(project, "Assets\\AppIcon.ico");
    }

    [TestMethod]
    public void Tight_icon_ico_files_contain_complete_image_payloads()
    {
        var root = FindRepositoryRoot();
        foreach (var relativePath in new[]
        {
            Path.Combine("src", "Jiaolong.ControlCenter", "Assets", "Brand", "JiaolongAppIcon-tight.ico"),
            Path.Combine("src", "Jiaolong.ControlCenter", "Assets", "Brand", "JiaolongTrayIconOffice-tight.ico"),
            Path.Combine("src", "Jiaolong.ControlCenter", "Assets", "Brand", "JiaolongTrayIconGaming-tight.ico"),
            Path.Combine("src", "Jiaolong.ControlCenter", "Assets", "Brand", "JiaolongTrayIconTurbo-tight.ico")
        })
        {
            var bytes = File.ReadAllBytes(Path.Combine(root, relativePath));
            Assert.IsTrue(bytes.Length >= 22, relativePath);
            Assert.AreEqual(0, BitConverter.ToUInt16(bytes, 0), relativePath);
            Assert.AreEqual(1, BitConverter.ToUInt16(bytes, 2), relativePath);
            var count = BitConverter.ToUInt16(bytes, 4);
            Assert.IsTrue(count >= 1, relativePath);
            for (var index = 0; index < count; index++)
            {
                var entry = 6 + index * 16;
                var payloadLength = BitConverter.ToUInt32(bytes, entry + 8);
                var payloadOffset = BitConverter.ToUInt32(bytes, entry + 12);
                Assert.IsTrue(payloadLength > 0, relativePath);
                Assert.IsTrue(payloadOffset >= 6 + count * 16, relativePath);
                Assert.IsTrue(payloadOffset + payloadLength <= bytes.Length, relativePath);
            }
        }
    }

    [TestMethod]
    public void Tray_icon_catalog_maps_the_three_confirmed_modes_to_colored_assets()
    {
        Assert.AreEqual("Assets\\Brand\\JiaolongTrayIconOffice-tight.ico", TrayIconAssetCatalog.For(PrototypePerformanceMode.Office));
        Assert.AreEqual("Assets\\Brand\\JiaolongTrayIconGaming-tight.ico", TrayIconAssetCatalog.For(PrototypePerformanceMode.Gaming));
        Assert.AreEqual("Assets\\Brand\\JiaolongTrayIconTurbo-tight.ico", TrayIconAssetCatalog.For(PrototypePerformanceMode.Turbo));
        Assert.AreEqual("Assets\\AppIcon.ico", TrayIconAssetCatalog.For(PrototypePerformanceMode.Custom));
    }

    [TestMethod]
    public void Tray_icon_assets_are_declared_as_runtime_content()
    {
        var project = ReadSource("src", "Jiaolong.ControlCenter", "Jiaolong.ControlCenter.csproj");

        StringAssert.Contains(project, "Assets\\Brand\\JiaolongTrayIconOffice.ico");
        StringAssert.Contains(project, "Assets\\Brand\\JiaolongTrayIconGaming.ico");
        StringAssert.Contains(project, "Assets\\Brand\\JiaolongTrayIconTurbo.ico");
    }

    [TestMethod]
    public void Native_tray_service_supports_replacing_the_icon_without_recreating_the_callback_window()
    {
        var source = ReadSource("src", "Jiaolong.ControlCenter", "Services", "TrayIconService.cs");

        StringAssert.Contains(source, "public bool UpdateIcon(string? iconPath)");
        StringAssert.Contains(source, "NimModify");
        StringAssert.Contains(source, "Shell_NotifyIcon(NimModify, data)");
    }

    private static int ReadPngDimension(byte[] png, int offset)
    {
        return (png[offset] << 24) | (png[offset + 1] << 16) | (png[offset + 2] << 8) | png[offset + 3];
    }

    [TestMethod]
    public void Quick_console_covers_modes_and_existing_shortcuts()
    {
        var xaml = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "TrayQuickConsoleWindow.xaml");
        var code = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "TrayQuickConsoleWindow.xaml.cs");
        var selection = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "TrayQuickConsoleWindow.Selection.cs");
        var shell = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "PrototypeWindow.xaml.cs");

        foreach (var mode in new[] { "Office", "Gaming", "Turbo", "Custom1", "Custom2", "Custom3" })
            StringAssert.Contains(selection, $"ControlModeId.{mode}");
        StringAssert.Contains(code, "ConsoleWidth = 360");
        StringAssert.Contains(code, "ConsoleHeight = 480");
        foreach (var name in new[] { "ModeChoicesGrid", "PresetChoicesPanel", "StrategyChoicesPanel", "SelectorLayer", "QuickMenuGrid" })
            StringAssert.Contains(xaml, name);
        foreach (var text in new[] { "快捷控制", "自适应调度", "打开主界面设置", "展开快捷风扇控制", "关闭显示器", "快捷控制台", "ThemeToggleButton" })
            StringAssert.Contains(xaml, text);
        Assert.IsFalse(xaml.Contains("ScrollViewer", StringComparison.Ordinal));
        Assert.IsFalse(xaml.Contains("CurrentPresetButton", StringComparison.Ordinal));
        Assert.IsFalse(xaml.Contains("QuickMenuEditButton", StringComparison.Ordinal));
        Assert.IsFalse(xaml.Contains("CpuBandClick", StringComparison.Ordinal));
        Assert.IsFalse(xaml.Contains("GpuBandClick", StringComparison.Ordinal));
        StringAssert.Contains(code, "QuickMenuCatalog.CreateDefault()");
        StringAssert.Contains(selection, "ThemeChanged?.Invoke(next)");
        StringAssert.Contains(selection, "TimeSpan.FromMilliseconds(320)");
        StringAssert.Contains(shell, "userPreferences.TrayTheme");
        StringAssert.Contains(shell, "userPreferences.ResolveTrayQuickMenuLayout()");
        var fan = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "TrayQuickConsoleWindow.Fan.cs");
        StringAssert.Contains(fan, "ConfigureFanActions");
        StringAssert.Contains(fan, "fixedFanRequested?.Invoke(target)");
        StringAssert.Contains(fan, "fanConnected && !fanBusy && fanStrongCooling");
        StringAssert.Contains(shell, "if (trayFanCommandPending)");
        Assert.IsFalse(fan.Contains("Navigate(", StringComparison.Ordinal));
        Assert.IsFalse(xaml.Contains("OnCloseClick", StringComparison.Ordinal));
        StringAssert.Contains(xaml, "OnOpenMainClick");
        StringAssert.Contains(xaml, "开启控制台");
        Assert.IsFalse(xaml.Contains("ActionStatusText", StringComparison.Ordinal));
        StringAssert.Contains(code, "Width = 86, Height = new UISettings().TextScaleFactor > 1 ? double.NaN : 56, MinHeight = 56");
        StringAssert.Contains(code, "QuickMenuLayout.FromPersisted");
        StringAssert.Contains(code, "Take(6)");
        var refresh = code[code.IndexOf("private void RefreshQuickButton", StringComparison.Ordinal)..code.IndexOf("public void SetSelectedMode", StringComparison.Ordinal)];
        Assert.IsFalse(refresh.Contains("button.Content =", StringComparison.Ordinal), "Polling must preserve the icon visual.");
        Assert.IsFalse(refresh.Contains("new Image", StringComparison.Ordinal));
        Assert.IsFalse(refresh.Contains("Shapes.Ellipse", StringComparison.Ordinal));
        StringAssert.Contains(code, "quickIconCache.TryGetValue");
        StringAssert.Contains(selection, "Width = 23, Height = 56");
        Assert.IsFalse(selection.Contains("Shapes.Ellipse", StringComparison.Ordinal));
        Assert.IsFalse(xaml.Contains("AdaptiveStatusText", StringComparison.Ordinal));
        Assert.IsFalse(xaml.Contains("HoverWash", StringComparison.Ordinal));
        StringAssert.Contains(selection, "CornerRadius = new CornerRadius(0, 8, 8, 0)");
        StringAssert.Contains(xaml, "Text=\"电源未读取\" FontSize=\"9\" HorizontalAlignment=\"Left\"");
        StringAssert.Contains(xaml, "AutomationProperties.Name=\"开启控制台\"");
        StringAssert.Contains(code, "添加快捷项");
        foreach (var text in new[] { "ShowDocked", "SetWindowOpacity", "CompositionTarget.Rendering", "AppWindow.Hide", "ApplyTelemetry", "ApplyState", "PopupXamlRoot", "IsModalActionPending", "SetModeBusy(bool busy)", "ShowStatus(string message)", "ReducedMotion", "ApplyAdaptiveState(bool enabled, bool available, bool pending = false)", "Microsoft.UI.Xaml.Automation", "if (isVisible && !isClosing && !changingDisplay)", "pendingQuickSettings" })
            StringAssert.Contains(code, text);
        foreach (var field in new[] { "CpuTemperatureText", "GpuPowerText", "CpuFanText", "MemoryText", "CapturedAtUtc" })
            StringAssert.Contains(code, field);
        StringAssert.Contains(code, "TimeSpan.FromSeconds(10)");
        StringAssert.Contains(code, "adaptiveRequested(requested)");
        Assert.IsFalse(code.Contains("customProfileRequested(profile)", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("SetSelectedMode(PrototypeModeCommandCoordinator.Map(hardwareMode))", StringComparison.Ordinal));
        foreach (var text in new[] { "PresetKey.Create(mode, slot)", "slot <= 3", "confirmedPreset == key", "rememberedPreset?.Invoke(mode)", "presetInfo?.Invoke(key).Available", "applyPresetRequested(key)", "strategyRequested?.Invoke(strategy)", "CloseSelector(", "selectorTrigger", "ModeAccentBrush" })
            StringAssert.Contains(selection, text);
        Assert.IsFalse(selection.Contains("adaptiveRequested(", StringComparison.Ordinal));
        StringAssert.Contains(xaml, "JiaolongAppIcon-tight.png");
        StringAssert.Contains(xaml, "WaveLogoControl");
        foreach (var command in new[] { "TrayCommand.ShowQuickConsole", "TrayCommand.Open", "TrayCommand.Exit" })
            StringAssert.Contains(shell, command);
    }

    [TestMethod]
    public void Tray_context_menu_uses_dark_acrylic_with_two_actions()
    {
        var xaml = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "TrayContextMenuWindow.xaml");
        var code = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "TrayContextMenuWindow.xaml.cs");
        var shell = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "PrototypeWindow.xaml.cs");

        StringAssert.Contains(xaml, "RequestedTheme=\"Dark\"");
        StringAssert.Contains(code, "DesktopAcrylicBackdrop");
        StringAssert.Contains(xaml, "打开客户端");
        StringAssert.Contains(xaml, "退出");
        StringAssert.Contains(code, "WindowActivationState.Deactivated");
        StringAssert.Contains(code, "VirtualKey.Escape");
        StringAssert.Contains(code, "~0x00C40000L");
        StringAssert.Contains(xaml, "Color=\"#1F1F1F\"");
        StringAssert.Contains(code, "ShowNearCursor(PointInt32 cursor)");
        StringAssert.Contains(code, "cursor.X +");
        StringAssert.Contains(code, "cursor.Y -");
        StringAssert.Contains(code, "SetForegroundWindow");
        StringAssert.Contains(shell, "ContextMenuRequested");
        StringAssert.Contains(shell, "ShowTrayContextMenu");
    }

    [TestMethod]
    public void Quick_console_receives_live_keyboard_indicator_state_and_refreshes_after_toggle()
    {
        var quickWindow = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "TrayQuickConsoleWindow.xaml.cs");
        var shell = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "PrototypeWindow.xaml.cs");

        StringAssert.Contains(quickWindow, "public void ApplyQuickSettingState");
        StringAssert.Contains(shell, "trayQuickConsole?.ApplyQuickSettingState(setting, enabled)");
        StringAssert.Contains(shell, "ApplySharedQuickSettingState(setting, status?.Enabled, available)");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Jiaolong.ControlCenter.slnx")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new DirectoryNotFoundException("无法定位蛟龙仓库根目录。");
    }

    private static string ReadSource(params string[] parts)
    {
        return File.ReadAllText(Path.Combine(new[] { FindRepositoryRoot() }.Concat(parts).ToArray()));
    }
}
