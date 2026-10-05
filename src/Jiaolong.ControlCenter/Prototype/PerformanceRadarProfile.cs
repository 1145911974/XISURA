namespace Jiaolong_ControlCenter.Prototype;

public sealed record PerformanceRadarProfile(
    int Cpu,
    int Gpu,
    int Cooling,
    int Response,
    int Quiet)
{
    public IReadOnlyList<int> Values => [Cpu, Gpu, Cooling, Response, Quiet];

    public PerformanceRadarProfile Normalized() => new(
        Math.Clamp(Cpu, 0, 100),
        Math.Clamp(Gpu, 0, 100),
        Math.Clamp(Cooling, 0, 100),
        Math.Clamp(Response, 0, 100),
        Math.Clamp(Quiet, 0, 100));

    public static PerformanceRadarProfile ForMode(
        PrototypePerformanceMode mode,
        string? turboTier,
        string? customProfile) => mode switch
        {
            PrototypePerformanceMode.Office => new(42, 38, 34, 48, 88),
            PrototypePerformanceMode.Gaming => new(76, 90, 78, 88, 38),
            PrototypePerformanceMode.Turbo => turboTier switch
            {
                "Quiet" => new(76, 82, 58, 80, 68),
                "Extreme" => new(100, 100, 100, 100, 12),
                _ => new(88, 92, 86, 94, 28)
            },
            PrototypePerformanceMode.Custom => customProfile switch
            {
                "Profile2" => new(78, 82, 76, 80, 42),
                "Profile3" => new(92, 88, 94, 90, 24),
                _ => new(62, 66, 58, 68, 62)
            },
            _ => new(42, 38, 34, 48, 88)
        };
}
