using LibreHardwareMonitor.Hardware;

namespace Jiaolong_ControlCenter.Services;

public sealed class LibreHardwareTelemetryReader : IHomeTelemetryReader
{
    private const double BytesPerGb = 1024d * 1024 * 1024;
    private readonly Computer computer = new()
    {
        IsCpuEnabled = true,
        IsGpuEnabled = true,
        IsMemoryEnabled = true,
        IsMotherboardEnabled = true,
        IsStorageEnabled = true
    };

    public LibreHardwareTelemetryReader() => computer.Open();

    public HomeTelemetrySnapshot Read()
    {
        var sensors = new List<(IHardware Hardware, ISensor Sensor)>();
        foreach (var hardware in computer.Hardware) Collect(hardware, sensors);

        var cpu = sensors.Where(x => x.Hardware.HardwareType == HardwareType.Cpu).Select(x => x.Sensor).ToArray();
        var gpuHardware = sensors.Select(x => x.Hardware).Distinct()
            .Where(x => IsGpu(x.HardwareType))
            .OrderBy(x => x.HardwareType == HardwareType.GpuNvidia ? 0 : x.HardwareType == HardwareType.GpuAmd ? 1 : 2)
            .FirstOrDefault();
        var gpu = sensors.Where(x => ReferenceEquals(x.Hardware, gpuHardware)).Select(x => x.Sensor).ToArray();
        var memory = sensors.Where(x => x.Hardware.HardwareType == HardwareType.Memory).Select(x => x.Sensor).ToArray();
        var fans = sensors.Where(x => x.Sensor.SensorType == SensorType.Fan).Select(x => x.Sensor).ToArray();
        var (systemUsed, systemTotal, allUsed, allTotal) = ReadDrives();

        var memoryUsed = Named(memory, SensorType.Data, "Memory Used");
        var memoryAvailable = Named(memory, SensorType.Data, "Memory Available");
        var memoryTotal = Both(memoryUsed, memoryAvailable);
        var gpuUsed = Named(gpu, SensorType.SmallData, "GPU Memory Used") ?? Named(gpu, SensorType.Data, "GPU Memory Used");
        var gpuTotal = Named(gpu, SensorType.SmallData, "GPU Memory Total") ?? Named(gpu, SensorType.Data, "GPU Memory Total");

        return new HomeTelemetrySnapshot(
            Preferred(cpu, SensorType.Temperature, "Package", "Tctl", "Core"),
            Preferred(cpu, SensorType.Load, "CPU Total", "Total"),
            Average(cpu, SensorType.Clock, "Core"),
            Preferred(cpu, SensorType.Power, "Package", "CPU"),
            Preferred(gpu, SensorType.Temperature, "GPU Core", "Core"),
            Preferred(gpu, SensorType.Load, "GPU Core", "Core", "D3D 3D"),
            ToGb(gpuUsed), ToGb(gpuTotal),
            Preferred(gpu, SensorType.Power, "GPU Package", "Total", "GPU"),
            memoryUsed, memoryTotal,
            systemUsed, systemTotal, allUsed, allTotal,
            Preferred(fans, SensorType.Fan, "CPU"),
            Preferred(gpu, SensorType.Fan, "GPU") ?? Preferred(fans, SensorType.Fan, "GPU"));
    }

    public void Dispose() => computer.Close();

    private static void Collect(IHardware hardware, ICollection<(IHardware, ISensor)> sensors)
    {
        hardware.Update();
        foreach (var sensor in hardware.Sensors) sensors.Add((hardware, sensor));
        foreach (var child in hardware.SubHardware) Collect(child, sensors);
    }

    private static bool IsGpu(HardwareType type) => type is HardwareType.GpuAmd or HardwareType.GpuIntel or HardwareType.GpuNvidia;

    private static double? Preferred(IEnumerable<ISensor> sensors, SensorType type, params string[] names)
    {
        var values = sensors.Where(x => x.SensorType == type && x.Value.HasValue && IsPlausible(type, x.Value.Value)).ToArray();
        foreach (var name in names)
        {
            var match = values.FirstOrDefault(x => x.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
            if (match?.Value is float value) return value;
        }
        return values.FirstOrDefault()?.Value;
    }

    private static double? Named(IEnumerable<ISensor> sensors, SensorType type, string name) =>
        sensors.FirstOrDefault(x => x.SensorType == type && x.Name.Contains(name, StringComparison.OrdinalIgnoreCase))?.Value;

    private static double? Average(IEnumerable<ISensor> sensors, SensorType type, string name)
    {
        var values = sensors.Where(x => x.SensorType == type && x.Name.Contains(name, StringComparison.OrdinalIgnoreCase) && x.Value.HasValue && IsPlausible(type, x.Value.Value))
            .Select(x => (double)x.Value!.Value).ToArray();
        return values.Length == 0 ? null : values.Average();
    }

    private static double? Both(double? first, double? second) => first.HasValue && second.HasValue ? first + second : null;
    private static double? ToGb(double? megabytes) => megabytes / 1024d;
    private static bool IsPlausible(SensorType type, float value) => type switch
    {
        SensorType.Temperature => value > 0 && value <= 150,
        SensorType.Clock => value > 0,
        SensorType.Power => value > 0,
        _ => float.IsFinite(value)
    };

    private static (double? SystemUsed, double? SystemTotal, double? AllUsed, double? AllTotal) ReadDrives()
    {
        try
        {
            var drives = DriveInfo.GetDrives().Where(x => x.IsReady && x.DriveType == DriveType.Fixed).ToArray();
            var systemRoot = Path.GetPathRoot(Environment.SystemDirectory);
            var system = drives.FirstOrDefault(x => string.Equals(x.RootDirectory.FullName, systemRoot, StringComparison.OrdinalIgnoreCase));
            var allTotal = drives.Sum(x => (double)x.TotalSize) / BytesPerGb;
            var allFree = drives.Sum(x => (double)x.AvailableFreeSpace) / BytesPerGb;
            return (
                system is null ? null : (system.TotalSize - system.AvailableFreeSpace) / BytesPerGb,
                system?.TotalSize / BytesPerGb,
                drives.Length == 0 ? null : allTotal - allFree,
                drives.Length == 0 ? null : allTotal);
        }
        catch { return (null, null, null, null); }
    }
}
