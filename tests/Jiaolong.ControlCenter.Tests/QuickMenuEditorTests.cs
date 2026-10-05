using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.Prototype.QuickMenu;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class QuickMenuEditorTests
{
    [TestMethod]
    public void Catalog_contains_the_eight_selected_shortcuts_and_two_editable_actions()
    {
        var items = QuickMenuCatalog.CreateDefault();

        Assert.AreEqual(10, items.Count);
        Assert.IsTrue(items.Any(item => item.Kind == QuickSettingKind.LidLogo));
        CollectionAssert.IsSubsetOf(
            new[] { "背板灯", "NumLock", "CapsLock", "Win键", "显示器息屏" },
            items.Select(item => item.Label).ToArray());
        Assert.IsFalse(items.Any(item => item.Kind == QuickSettingKind.Osd));
        Assert.IsTrue(items.Any(item => item.Kind == QuickSettingKind.WinKey));
        Assert.IsTrue(items.Any(item => item.Kind == QuickSettingKind.DisplayOff && item.Interaction == QuickMenuInteraction.Action));
        Assert.IsTrue(items.All(item => item.IconSource.Contains("Assets/", StringComparison.Ordinal)));
        StringAssert.Contains(items.Single(item => item.Kind == QuickSettingKind.WinKey).IconSource, "QuickWinKey.png");
        StringAssert.Contains(items.Single(item => item.Kind == QuickSettingKind.DisplayOff).IconSource, "QuickDisplayOff.png");
    }

    [TestMethod]
    public void Layout_toggle_and_reorder_keep_unique_enabled_items()
    {
        var layout = QuickMenuLayout.Default;

        Assert.AreEqual(9, layout.EnabledOrder.Count);

        var changed = layout.Toggle(QuickSettingKind.LidLogo, true)
            .Toggle(QuickSettingKind.Wifi, false)
            .Reorder(QuickSettingKind.LidLogo, 0);

        CollectionAssert.AreEqual(
            new[]
            {
                QuickSettingKind.LidLogo,
                QuickSettingKind.Bluetooth,
                QuickSettingKind.Touchpad,
                QuickSettingKind.FnLock,
                QuickSettingKind.NumLock,
                QuickSettingKind.CapsLock,
                QuickSettingKind.WinKey,
                QuickSettingKind.DisplayOff
            },
            changed.EnabledOrder.ToArray());
        Assert.AreEqual(changed.EnabledOrder.Count, changed.EnabledOrder.Distinct().Count());
    }

    [TestMethod]
    public void Layout_adds_a_new_shortcut_when_fewer_than_nine_are_selected()
    {
        var layout = new QuickMenuLayout(QuickMenuLayout.Default.EnabledOrder.Take(6).ToArray());

        var changed = layout.Toggle(QuickSettingKind.DisplayOff, true);

        Assert.AreEqual(7, changed.EnabledOrder.Count);
        Assert.IsTrue(changed.EnabledOrder.Contains(QuickSettingKind.DisplayOff));
    }

    [TestMethod]
    public void Preview_layout_always_returns_nine_fixed_slots()
    {
        var options = QuickMenuCatalog.CreateDefault()
            .Take(6)
            .Select(item => new QuickMenuOption(item))
            .ToArray();
        var layout = new QuickMenuLayout(options.Select(option => option.Item.Kind).ToArray());

        var slots = QuickMenuPreviewLayout.Create(layout, options);

        Assert.AreEqual(QuickMenuLayout.MaxEnabledItems, slots.Count);
        Assert.AreEqual(6, slots.Count(slot => slot.IsFilled));
        Assert.AreEqual(3, slots.Count(slot => !slot.IsFilled));
        var traySlots = QuickMenuPreviewLayout.Create(layout, options, maxItems: 6);
        Assert.AreEqual(6, traySlots.Count);
        Assert.IsTrue(traySlots.All(slot => slot.IsFilled));
        var emptyTray = QuickMenuPreviewLayout.Create(new QuickMenuLayout([]), options, maxItems: 6);
        Assert.AreEqual(6, emptyTray.Count);
        Assert.IsTrue(emptyTray.All(slot => !slot.IsFilled));
        var reorderedTray = QuickMenuPreviewLayout.ToLayout(traySlots.Reverse(), maxItems: 6);
        CollectionAssert.AreEqual(options.Select(option => option.Item.Kind).Reverse().ToArray(), reorderedTray.EnabledOrder.ToArray());
    }

    [TestMethod]
    public void Layout_round_trip_ignores_unknown_or_duplicate_ids()
    {
        var layout = QuickMenuLayout.FromPersisted(
            ["wifi", "lidLogo", "lidLogo", "unknown"],
            ["lidLogo", "wifi", "unknown"]);

        CollectionAssert.AreEqual(
            new[] { QuickSettingKind.LidLogo, QuickSettingKind.Wifi },
            layout.EnabledOrder.ToArray());
    }

    [TestMethod]
    public void Layout_caps_enabled_items_at_the_three_by_three_grid_limit()
    {
        var layout = QuickMenuLayout.Default
            .Toggle(QuickSettingKind.StrongCooling, true);

        Assert.AreEqual(QuickMenuLayout.MaxEnabledItems, layout.EnabledOrder.Count);
        Assert.IsFalse(layout.EnabledOrder.Contains(QuickSettingKind.StrongCooling));
        var tray = QuickMenuLayout.FromPersisted(QuickMenuLayout.Default.ToPersistedEnabled(),
            QuickMenuLayout.Default.ToPersistedOrder(), maxItems: 6);
        Assert.AreEqual(6, tray.EnabledOrder.Count);
        Assert.AreEqual(6, tray.Toggle(QuickSettingKind.StrongCooling, true, maxItems: 6).EnabledOrder.Count);
        Assert.IsFalse(tray.Toggle(QuickSettingKind.StrongCooling, true, maxItems: 6).EnabledOrder.Contains(QuickSettingKind.StrongCooling));
        var replaced = tray.Toggle(QuickSettingKind.Wifi, false, maxItems: 6)
            .Toggle(QuickSettingKind.StrongCooling, true, maxItems: 6);
        Assert.AreEqual(6, replaced.EnabledOrder.Count);
        Assert.IsTrue(replaced.EnabledOrder.Contains(QuickSettingKind.StrongCooling));
        Assert.AreEqual(0, QuickMenuLayout.FromPersisted([], [], maxItems: 6, useDefaultWhenEmpty: false).EnabledOrder.Count);
        Assert.AreEqual(9, QuickMenuLayout.Default.EnabledOrder.Count);
    }

    [TestMethod]
    public void Layout_can_remove_every_item_without_creating_a_placeholder()
    {
        var layout = QuickMenuLayout.Default;

        foreach (var kind in layout.EnabledOrder.ToArray())
            layout = layout.Toggle(kind, false);

        Assert.AreEqual(0, layout.EnabledOrder.Count);
        CollectionAssert.AreEqual(Array.Empty<QuickSettingKind>(), layout.EnabledOrder.ToArray());
    }

    [TestMethod]
    public void Persisted_win_key_layout_keeps_the_new_shortcut()
    {
        var layout = QuickMenuLayout.FromPersisted(
            ["wifi", "bluetooth", "touchpad", "winKey", "lidLogo", "fnLock"],
            ["wifi", "bluetooth", "touchpad", "winKey", "lidLogo", "fnLock"]);

        CollectionAssert.AreEqual(
            new[]
            {
                QuickSettingKind.Wifi,
                QuickSettingKind.Bluetooth,
                QuickSettingKind.Touchpad,
                QuickSettingKind.WinKey,
                QuickSettingKind.LidLogo,
                QuickSettingKind.FnLock
            },
            layout.EnabledOrder.ToArray());
    }
}
