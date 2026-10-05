using Jiaolong.Contracts.Models;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Jiaolong_ControlCenter.Prototype.QuickMenu;

public enum QuickMenuInteraction
{
    Toggle,
    Action
}

public sealed record QuickMenuItem(
    QuickSettingKind Kind,
    string Label,
    string IconSource,
    string CapabilityKey,
    QuickMenuInteraction Interaction = QuickMenuInteraction.Toggle)
{
    public bool IsAction => Interaction == QuickMenuInteraction.Action;
}

public sealed class QuickMenuOption(QuickMenuItem item) : INotifyPropertyChanged
{
    private bool isAvailable;
    private bool isEnabled;
    private bool isSelectable;

    public QuickMenuItem Item { get; } = item;
    public string Label => Item.Label;
    public bool IsAvailable
    {
        get => isAvailable;
        set => Set(ref isAvailable, value);
    }

    public bool IsEnabled
    {
        get => isEnabled;
        set => Set(ref isEnabled, value);
    }

    public bool IsSelectable
    {
        get => isSelectable;
        set => Set(ref isSelectable, value);
    }

    public string AvailabilityText => IsAvailable ? string.Empty : "当前设备不可用";
    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        if (propertyName == nameof(IsAvailable))
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AvailabilityText)));
    }
}

public sealed class QuickMenuPreviewSlot(QuickMenuOption? option)
{
    public QuickMenuOption? Option { get; } = option;
    public bool IsFilled => Option is not null;
    // Keep the binding source non-null; empty slots hide the image via opacity.
    public string IconSource => Option?.Item.IconSource ?? "ms-appx:///Assets/QuickMenu/QuickWifi.png";
    public double FilledOpacity => IsFilled ? 1 : 0;
}

public static class QuickMenuPreviewLayout
{
    public static IReadOnlyList<QuickMenuPreviewSlot> Create(
        QuickMenuLayout layout,
        IReadOnlyList<QuickMenuOption> options,
        int maxItems = QuickMenuLayout.MaxEnabledItems)
    {
        var byKind = options.ToDictionary(option => option.Item.Kind);
        var slots = layout.EnabledOrder
            .Where(byKind.ContainsKey)
            .Take(maxItems)
            .Select(kind => new QuickMenuPreviewSlot(byKind[kind]))
            .ToList();

        while (slots.Count < maxItems)
            slots.Add(new QuickMenuPreviewSlot(null));

        return slots;
    }

    public static QuickMenuLayout ToLayout(IEnumerable<QuickMenuPreviewSlot> slots, int maxItems = QuickMenuLayout.MaxEnabledItems) =>
        new(slots
            .Where(slot => slot.Option is not null)
            .Select(slot => slot.Option!.Item.Kind)
            .Distinct()
            .Take(maxItems)
            .ToArray());
}

public sealed record QuickMenuLayout(IReadOnlyList<QuickSettingKind> EnabledOrder)
{
    public const int MaxEnabledItems = 9;

    public static QuickMenuLayout Default { get; } = new(
        [
            QuickSettingKind.Wifi,
            QuickSettingKind.Bluetooth,
            QuickSettingKind.Touchpad,
            QuickSettingKind.FnLock,
            QuickSettingKind.LidLogo,
            QuickSettingKind.NumLock,
            QuickSettingKind.CapsLock,
            QuickSettingKind.WinKey,
            QuickSettingKind.DisplayOff
        ]);

    public QuickMenuLayout Toggle(QuickSettingKind kind, bool enabled, int maxItems = MaxEnabledItems)
    {
        var result = EnabledOrder.Where(candidate => candidate != kind).ToList();
        if (enabled && (EnabledOrder.Contains(kind) || result.Count < maxItems))
        {
            var index = Math.Min(EnabledOrder.Count, result.Count);
            result.Insert(index, kind);
        }
        return new QuickMenuLayout(result.Take(maxItems).ToArray());
    }

    public QuickMenuLayout Reorder(QuickSettingKind kind, int index)
    {
        var result = EnabledOrder.Where(candidate => candidate != kind).ToList();
        if (!EnabledOrder.Contains(kind)) return this;
        result.Insert(Math.Clamp(index, 0, result.Count), kind);
        return new QuickMenuLayout(result.Take(MaxEnabledItems).ToArray());
    }

    public static QuickMenuLayout FromPersisted(
        IReadOnlyList<string> enabledIds,
        IReadOnlyList<string> orderIds,
        int maxItems = MaxEnabledItems,
        bool useDefaultWhenEmpty = true)
    {
        if (enabledIds.Count == 0 && orderIds.Count == 0 && useDefaultWhenEmpty)
            return new QuickMenuLayout(Default.EnabledOrder.Take(maxItems).ToArray());
        var valid = QuickMenuCatalog.CreateDefault().Select(item => item.Kind).ToHashSet();
        var enabled = Parse(enabledIds).Where(valid.Contains).Distinct().ToList();
        var ordered = Parse(orderIds).Where(valid.Contains).Distinct().ToList();
        var result = ordered.Where(enabled.Contains).ToList();
        result.AddRange(enabled.Where(kind => !result.Contains(kind)));
        return new QuickMenuLayout(result.Take(maxItems).ToArray());
    }

    public string[] ToPersistedEnabled() => EnabledOrder.Select(kind => kind.ToString()).ToArray();

    public string[] ToPersistedOrder() => EnabledOrder.Select(kind => kind.ToString()).ToArray();

    private static IEnumerable<QuickSettingKind> Parse(IEnumerable<string> ids) =>
        ids.Select(id => Enum.TryParse<QuickSettingKind>(id, ignoreCase: true, out var kind) ? kind : (QuickSettingKind?)null)
           .Where(kind => kind.HasValue)
           .Select(kind => kind!.Value);
}
