using Jiaolong.Contracts.Models;

namespace Jiaolong_ControlCenter.Services;

public static class PresetNameCatalog
{
    private static readonly string[] Defaults = ["安静", "均衡", "性能"];

    public static string GetName(IReadOnlyDictionary<string, string> names, PresetKey key) =>
        names.TryGetValue(StorageKey(key), out var name) && !string.IsNullOrWhiteSpace(name)
            ? name
            : Defaults[key.Slot - 1];

    public static void SetName(IDictionary<string, string> names, PresetKey key, string value)
    {
        var name = value.Trim();
        if (name.Length > 16)
            throw new ArgumentOutOfRangeException(nameof(value), "预设名称最多 16 个字符。");

        if (name.Length == 0 || name == Defaults[key.Slot - 1])
            names.Remove(StorageKey(key));
        else
            names[StorageKey(key)] = name;
    }

    private static string StorageKey(PresetKey key)
    {
        PresetKey.Create(key.Mode, key.Slot);
        return $"{key.Mode}:{key.Slot}";
    }
}
