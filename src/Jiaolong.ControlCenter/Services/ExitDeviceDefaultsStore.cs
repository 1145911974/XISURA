using System.Collections.Concurrent;
using System.Text.Json;
using Jiaolong.Contracts.Models;

namespace Jiaolong_ControlCenter.Services;

public sealed class ExitDeviceDefaultsStore(IUserPreferencesPathProvider? paths = null)
{
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string folder = Path.Combine((paths ?? new DefaultUserPreferencesPathProvider()).LocalAppDataRoot,
        "Jiaolong Control Center", "exit-defaults");
    private readonly IUserPreferencesPathProvider writeObserver = paths ?? new DefaultUserPreferencesPathProvider();

    public ExitDeviceDefaults? Load(string? deviceKey)
    {
        if (deviceKey is null) return null;
        string path = GetPath(deviceKey);
        lock (Gates.GetOrAdd(path, _ => new())) return LoadCore(path, deviceKey);
    }

    public void Save(ExitDeviceDefaults value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!Valid(value, value.DeviceKey)) throw new ArgumentException("退出恢复配置的设备键或数值无效。", nameof(value));
        string path = GetPath(value.DeviceKey);
        lock (Gates.GetOrAdd(path, _ => new()))
        {
            _ = LoadCore(path, value.DeviceKey);
            Directory.CreateDirectory(folder);
            string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, value, Json);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporary, path, overwrite: true);
                writeObserver.RecordWrite(path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    private string GetPath(string deviceKey)
    {
        if (!DeviceRecoveryKey.IsValid(deviceKey)) throw new ArgumentException("退出恢复配置缺少有效设备键。", nameof(deviceKey));
        return Path.Combine(folder, deviceKey.ToUpperInvariant() + ".json");
    }

    private static ExitDeviceDefaults? LoadCore(string path, string deviceKey)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length > 8192) throw new InvalidDataException("退出恢复配置过大，原文件已保留。");
            var saved = JsonSerializer.Deserialize<ExitDeviceDefaults>(stream, Json);
            if (saved is null || !Valid(saved, deviceKey)) throw new InvalidDataException("退出恢复配置无法校验，原文件已保留。");
            return saved;
        }
        catch (JsonException error) { throw new InvalidDataException("退出恢复配置损坏，原文件已保留。", error); }
    }

    private static bool Valid(ExitDeviceDefaults value, string deviceKey) => value.SchemaVersion == 1 &&
        DeviceRecoveryKey.IsValid(value.DeviceKey) && string.Equals(value.DeviceKey, deviceKey, StringComparison.OrdinalIgnoreCase) &&
        value.CurveOptimizerAll is null or >= -50 and <= 0;
}
