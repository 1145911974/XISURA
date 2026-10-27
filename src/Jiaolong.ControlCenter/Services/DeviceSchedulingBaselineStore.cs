using Jiaolong.Contracts.Models;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Jiaolong_ControlCenter.Services;

public sealed record DeviceSchedulingBaseline(int SchemaVersion, HardwareIdentity Identity,
    DateTimeOffset CapturedAtUtc, PerformanceMode NativeMode, CpuTuningState Cpu)
{
    public DeviceGpuBaseline? Gpu { get; init; }
    public FanControlPlan? Fan { get; init; }
    public bool? StrongCooling { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string[]? CapturedCpuFields { get; init; }
}

public sealed record DeviceGpuBaseline(int? CoreOffsetKhz, int? MemoryOffsetKhz, int[]? VfOffsetsKhz);

public sealed class DeviceSchedulingBaselineStore(IUserPreferencesPathProvider? paths = null)
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly IUserPreferencesPathProvider paths = paths ?? new DefaultUserPreferencesPathProvider();
    private DeviceSchedulingBaseline? cached;

    public async Task<DeviceSchedulingBaseline?> LoadOrCaptureAsync(HomeStateSnapshot? snapshot, CancellationToken token,
        bool allowPartial = false)
    {
        token.ThrowIfCancellationRequested();
        if (snapshot?.Capabilities.Identity is not { } identity ||
            string.IsNullOrWhiteSpace(identity.BoardProduct) || string.IsNullOrWhiteSpace(identity.CpuModel)) return null;
        if (cached?.Identity == identity)
        {
            if (cached.SchemaVersion == 2 && !allowPartial)
                throw new InvalidDataException("部分本机均衡基准尚未接通，原文件已保留。");
            return cached;
        }
        string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(identity))));
        string path = Path.Combine(paths.LocalAppDataRoot, "Jiaolong Control Center", "device-baselines", fingerprint + ".json");
        var gate = Gates.GetOrAdd(path, _ => new(1, 1));
        await gate.WaitAsync(token);
        try
        {
            if (File.Exists(path))
            {
                try
                {
                    await using var stream = File.OpenRead(path);
                    if (stream.Length > 131072) throw new InvalidDataException("本机均衡基准文件过大，已停止读取。");
                    var saved = await JsonSerializer.DeserializeAsync<DeviceSchedulingBaseline>(stream, Json, token);
                    if (saved is null || saved.Identity != identity || !IsValidSavedBaseline(saved, allowPartial))
                        throw new InvalidDataException("本机均衡基准无法校验，原文件已保留。");
                    return cached = saved;
                }
                catch (JsonException error) { throw new InvalidDataException("本机均衡基准损坏，原文件已保留。", error); }
            }
            if (snapshot.Capabilities.SupportState != DeviceSupportState.Ready ||
                snapshot.Controls.PerformanceMode is not { } mode || !Enum.IsDefined(mode) ||
                snapshot.Controls.CpuTuning is not { } cpu) return null;
            bool complete = HasCompleteCpuReadback(cpu);
            if (allowPartial && (!IsNativeMode(mode) || !HasPartialCpuReadback(cpu)) || !allowPartial && !complete)
                return null;
            // Store requested configuration only: instantaneous GPU clocks and fan RPM are not policies.
            var gpu = snapshot.Controls.GpuVf is { Error: null } vf ? new DeviceGpuBaseline(
                vf.CoreOffsetKhz, vf.MemoryOffsetKhz,
                vf.Nodes is { Length: 127 } ? vf.Nodes.Select(node => node.OffsetKhz).ToArray() : null) : null;
            var captured = new DeviceSchedulingBaseline(complete ? 1 : 2, identity, DateTimeOffset.UtcNow, mode, cpu)
            {
                Gpu = gpu, Fan = snapshot.Controls.ActiveFanControlPlan, StrongCooling = snapshot.Controls.StrongCooling,
                CapturedCpuFields = complete ? null : GetCapturedCpuFields(cpu)
            };
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    await JsonSerializer.SerializeAsync(stream, captured, Json, token);
                    await stream.FlushAsync(token);
                    stream.Flush(flushToDisk: true);
                }
                token.ThrowIfCancellationRequested();
                File.Move(temporary, path);
                paths.RecordWrite(path);
                return cached = captured;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { gate.Release(); }
    }

    internal static bool HasCompleteCpuReadback(CpuTuningState? cpu) => cpu is
        { TemperatureLimitC: >= 45 and <= 100,
          AcMaxFrequencyMhz: not null, DcMaxFrequencyMhz: not null, BoostEnabled: not null, WindowsPowerSchemeId: not null } &&
        (cpu.SplWatts ?? cpu.ObservedSplWatts) is >= 20 and <= 105 &&
        (cpu.SpptWatts ?? cpu.AdvancedLimits?.FastPptWatts) is >= 20 and <= 120 &&
        (cpu.SplWatts ?? cpu.ObservedSplWatts) <= (cpu.SpptWatts ?? cpu.AdvancedLimits?.FastPptWatts) &&
        (cpu.SplWatts is null || cpu.ObservedSplWatts is null || cpu.SplWatts == cpu.ObservedSplWatts) &&
        cpu.WindowsPowerSchemeId != Guid.Empty &&
        ValidFrequency(cpu.AcMaxFrequencyMhz.Value) && ValidFrequency(cpu.DcMaxFrequencyMhz.Value);

    private static bool IsValidSavedBaseline(DeviceSchedulingBaseline saved, bool allowPartial) => saved.SchemaVersion switch
    {
        1 => HasCompleteCpuReadback(saved.Cpu) && Enum.IsDefined(saved.NativeMode),
        2 => allowPartial && IsNativeMode(saved.NativeMode) && HasPartialCpuReadback(saved.Cpu) &&
            saved.CapturedCpuFields is { } fields && fields.All(field => !string.IsNullOrWhiteSpace(field)) &&
            fields.Order(StringComparer.OrdinalIgnoreCase)
                .SequenceEqual(GetCapturedCpuFields(saved.Cpu), StringComparer.OrdinalIgnoreCase),
        _ => false
    };

    private static bool IsNativeMode(PerformanceMode mode) =>
        mode is PerformanceMode.Quiet or PerformanceMode.Balanced or PerformanceMode.Turbo;

    private static bool HasPartialCpuReadback(CpuTuningState? cpu) => cpu is
        { AcMaxFrequencyMhz: not null, DcMaxFrequencyMhz: not null, BoostEnabled: not null,
          WindowsPowerSchemeId: not null } && cpu.WindowsPowerSchemeId != Guid.Empty &&
        ValidFrequency(cpu.AcMaxFrequencyMhz.Value) && ValidFrequency(cpu.DcMaxFrequencyMhz.Value) &&
        (cpu.TemperatureLimitC is null or >= 45 and <= 100) &&
        (cpu.SplWatts is null or >= 20 and <= 105) && (cpu.SpptWatts is null or >= 20 and <= 120) &&
        (cpu.SplWatts is null || cpu.SpptWatts is null || cpu.SplWatts <= cpu.SpptWatts) &&
        (cpu.ObservedSplWatts is null || cpu.SpptWatts is null || cpu.ObservedSplWatts <= cpu.SpptWatts) &&
        (cpu.ObservedSplWatts is null || double.IsFinite(cpu.ObservedSplWatts.Value) && cpu.ObservedSplWatts is >= 20 and <= 105) &&
        (cpu.SplWatts is null || cpu.ObservedSplWatts is null || cpu.SplWatts == cpu.ObservedSplWatts);

    internal static string[] GetCapturedCpuFields(CpuTuningState cpu)
    {
        var fields = new List<string>
        { "acMaxFrequencyMhz", "dcMaxFrequencyMhz", "isBoostEnabled", "windowsPowerSchemeId" };
        if (cpu.TemperatureLimitC is >= 45 and <= 100) fields.Add("temperatureLimitC");
        if (cpu.SplWatts is >= 20 and <= 105) fields.Add("splWatts");
        if (cpu.SpptWatts is >= 20 and <= 120) fields.Add("spptWatts");
        if (cpu.AcMinActiveCoresPercent is >= 0 and <= 100 && cpu.DcMinActiveCoresPercent is >= 0 and <= 100)
            fields.AddRange(["acMinActiveCoresPercent", "dcMinActiveCoresPercent"]);
        if (cpu.CurveOptimizerVerification == "hardwareReadback" && cpu.NegativeCurveOptimizer is >= -50 and <= 0)
            fields.Add("negativeCurveOptimizer");
        if (cpu.PboScalar is >= 1 and <= 10 || cpu.CurveOptimizerVerification == "hardwareReadback" &&
            cpu.NegativeCurveOptimizer is null && cpu.HasCompleteCurveValues() &&
            cpu.PerCoreCurveOptimizer!.Values.All(value => value is >= -50 and <= 0))
            fields.Add("advancedCpuTuning");
        return fields.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool ValidFrequency(int value) => value == 0 || value is >= 1500 and <= 5400;
}
