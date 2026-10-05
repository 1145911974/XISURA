using System.Text.Json;
using System.Text.Json.Serialization;
using Jiaolong.Contracts.Commands;

namespace Jiaolong.Contracts.Models;

[JsonConverter(typeof(LowerCamelEnumConverter<ControlModeId>))]
public enum ControlModeId
{
    Office,
    Gaming,
    Turbo,
    Custom1,
    Custom2,
    Custom3
}

[JsonConverter(typeof(LowerCamelEnumConverter<ControlPageId>))]
public enum ControlPageId
{
    Performance,
    Gpu,
    Fan,
    Lighting
}

public readonly record struct PresetKey(ControlModeId Mode, int Slot)
{
    public static IReadOnlyList<PresetKey> All { get; } = CreateAll();

    public static PresetKey Create(ControlModeId mode, int slot)
    {
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "未知的控制模式。");
        if (slot is < 1 or > 3)
            throw new ArgumentOutOfRangeException(nameof(slot), slot, "预设槽位必须为 1、2 或 3。");

        return new PresetKey(mode, slot);
    }

    private static IReadOnlyList<PresetKey> CreateAll() =>
        Enum.GetValues<ControlModeId>()
            .SelectMany(mode => Enumerable.Range(1, 3).Select(slot => new PresetKey(mode, slot)))
            .ToArray();
}

public sealed record PagePresetEnvelope(
    int SchemaVersion,
    ControlPageId Page,
    PresetKey Key,
    string DisplayName,
    JsonElement Payload,
    DateTimeOffset SavedAtUtc);
