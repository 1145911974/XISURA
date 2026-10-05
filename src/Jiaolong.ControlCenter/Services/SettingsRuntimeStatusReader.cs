using System.Text.Json;
using Microsoft.Win32;

namespace Jiaolong_ControlCenter.Services;

internal sealed record SettingsRuntimeStatus(
    string RecoveryPlan,
    string RecoveryPlanDetail,
    string LastConfiguration,
    string LastConfigurationDetail,
    string PendingConfiguration,
    string PendingConfigurationDetail);

internal static class SettingsRuntimeStatusReader
{
    internal static SettingsRuntimeStatus Read()
    {
        var (plan, planDetail) = ReadServiceRecoveryPlan();
        var (last, lastDetail, pending, pendingDetail) = ReadSavedConfiguration();
        return new(plan, planDetail, last, lastDetail, pending, pendingDetail);
    }

    private static (string, string) ReadServiceRecoveryPlan()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\JiaolongControlService");
            if (key?.GetValue("FailureActions") is not byte[] data || data.Length < 20)
                return ("未配置", "Windows 服务恢复操作缺失");

            var count = BitConverter.ToInt32(data, 12);
            var offset = BitConverter.ToInt32(data, 16);
            if (count is < 0 or > 8 || offset < 20 || offset > data.Length - count * 8)
                return ("未能读取", "Windows 服务恢复配置无效");
            if (count == 0) return ("未配置", "Windows 服务无失败操作");

            var restartDelays = new List<int>();
            for (var index = 0; index < count; index++)
            {
                if (BitConverter.ToInt32(data, offset + index * 8) != 1)
                    return ($"已配置 {count} 级", "包含非重启的服务恢复操作");
                restartDelays.Add(BitConverter.ToInt32(data, offset + index * 8 + 4) / 1000);
            }

            return ($"自动重启 ×{count}", $"失败后 {string.Join(" / ", restartDelays)} 秒");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return ("未能读取", "Windows 服务恢复配置不可访问");
        }
    }

    private static (string, string, string, string) ReadSavedConfiguration()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Jiaolong Control Center", "last-applied-controls.json");
        if (!File.Exists(path))
            return ("无记录", "尚未保存硬件配置", "无待处理项", "无本地恢复锁");

        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length > 262_144) return ("未能读取", "配置记录过大", "未能确认", "恢复锁状态未知");
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            var outcome = root.TryGetProperty("lastOutcome", out var outcomeValue) ? outcomeValue.GetString() : null;
            var group = root.TryGetProperty("lastGroup", out var groupValue) ? groupValue.GetString() : null;
            var last = root.TryGetProperty("lastAttemptUtc", out var attemptValue) &&
                       attemptValue.ValueKind == JsonValueKind.String &&
                       attemptValue.TryGetDateTimeOffset(out var attemptedAt)
                ? attemptedAt.ToLocalTime().ToString("MM-dd HH:mm")
                : "无记录";
            var blocked = root.TryGetProperty("blockedGroups", out var blockedValue) &&
                          blockedValue.ValueKind == JsonValueKind.Array
                ? blockedValue.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString()).OfType<string>().ToArray()
                : [];
            var pending = root.TryGetProperty("pendingGroup", out var pendingValue) &&
                          pendingValue.ValueKind == JsonValueKind.String ? pendingValue.GetString() : null;
            var groups = blocked.Concat(pending is null ? [] : [pending]).Distinct().ToArray();
            var detail = outcome switch
            {
                "manualApplied" => "手动应用成功",
                "applied" => "自动恢复成功",
                "transportFailed" => "自动恢复传输失败",
                "deferredConflict" => "等待其他控制台释放",
                null => "尚无结果",
                _ => $"恢复结果：{outcome}"
            };
            if (group is not null) detail += $" · {GroupName(group)}";
            return (last, detail, groups.Length == 0 ? "无待处理项" : $"{groups.Length} 项需确认",
                groups.Length == 0 ? "已保存配置无恢复锁" : string.Join(" / ", groups.Select(GroupName)) + "恢复锁");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return ("未能读取", "本地配置记录不可用", "未能确认", "恢复锁状态未知");
        }
    }

    private static string GroupName(string group) => group switch
    {
        "mode" => "模式",
        "cpu" => "处理器",
        "gpu" => "显卡",
        "fan" => "风扇",
        "lighting" => "灯光",
        "logo" => "灯标",
        _ => "其他"
    };
}
