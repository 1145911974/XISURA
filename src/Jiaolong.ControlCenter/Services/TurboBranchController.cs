using System.Text.Json;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;

namespace Jiaolong_ControlCenter.Services;

public sealed class TurboBranchController(
    Func<HomeStateSnapshot?> state,
    Func<HardwareCommand, CancellationToken, Task<CommandResult>> execute)
{
    private HomeControlState? original;
    private HomeControlState? owned;
    private CpuTuningPlan? activeCpu;
    private bool ownsFan;
    private bool ownsGpu;
    public string? ActiveTier { get; private set; }
    public bool HasOwnership => ownsFan || ownsGpu;

    public static CpuTuningPlan CreateCpuPlan(string tier, CpuTuningState current) => tier switch
    {
        "Quiet" => Plan(current, 80, 45, 45, 4500, 3300, true),
        "Extreme" => Plan(current, 95, 75, 75, 5100, 3300, true),
        _ => throw new ArgumentOutOfRangeException(nameof(tier))
    };

    private static CpuTuningPlan Plan(CpuTuningState current, int temperature, int spl, int sppt, int ac, int dc, bool boost) =>
        new(temperature, spl, sppt, null, boost, null, current.WindowsPowerSchemeId, null)
        { AcMaxFrequencyMhz = ac, DcMaxFrequencyMhz = dc,
          AcMinActiveCoresPercent = current.AcMinActiveCoresPercent, DcMinActiveCoresPercent = current.DcMinActiveCoresPercent };

    public async Task ApplyAsync(string tier, CancellationToken token)
    {
        await ReleaseAsync(token);
        var before = state() ?? throw new InvalidOperationException("硬件状态不可用");
        var cpu = before.Controls.CpuTuning;
        if (cpu?.TemperatureLimitC is not int temperature || cpu.SplWatts is not int spl || cpu.SpptWatts is not int sppt ||
            cpu.AcFrequency() is not int ac || cpu.DcFrequency() is not int dc || cpu.BoostEnabled is not bool boost ||
            before.Controls.PerformanceMode is not { } mode || before.Controls.StrongCooling is null)
            throw new InvalidOperationException("分支原始设置无法完整回读");
        bool Available(string key) => before.Capabilities.Items.Any(item => item.Key == key && item.State == CapabilityState.Available);
        if (tier == "Quiet" && (!Available("fanControl") || !before.Controls.FanAutomaticCeilingAvailable) ||
            tier == "Extreme" && !Available("strongCooling"))
            throw new InvalidOperationException("此分支需要的散热控制不可用");
        var plan = CreateCpuPlan(tier, cpu);
        original = before.Controls;
        bool cpuApplied = false;
        try
        {
            if (tier == "Extreme") await ApplyFanAsync(null, true, token);
            if (Available("gpuFrequencyLimit") && before.Controls.GpuClockLimit is { } clock)
            {
                int? target = tier == "Quiet" ? Math.Clamp(2100, clock.MinimumMhz, clock.MaximumMhz) : null;
                await SendAsync(new SetGpuFrequencyLimitCommand(Guid.NewGuid(), target, true), token);
                if (state()?.Controls.GpuClockLimit is not { Error: null } actual || actual.SubmittedMhz != target)
                    throw new InvalidOperationException("分支显卡频率限制未确认");
                ownsGpu = true;
                owned = owned is null ? state()!.Controls : owned with { GpuClockLimit = state()!.Controls.GpuClockLimit };
            }
            if (tier == "Quiet")
                await ApplyFanAsync(new FanControlPlan([new(40, 60), new(95, 100)]) { Strategy = "Auto", MaximumRpm = 3500 }, false, token);
            // CPU is last: its service transaction can restore original limits above the user write range.
            await SendAsync(new SetCpuTuningBatchCommand(Guid.NewGuid(), [plan], true) { NativeMode = PerformanceMode.Turbo }, token);
            cpuApplied = true;
            activeCpu = plan;
            ActiveTier = tier;
            if (!Matches(state()!.Controls)) throw new InvalidOperationException("分支最终回读不一致");
        }
        catch (Exception failure)
        {
            ActiveTier = null;
            var errors = new List<Exception> { failure };
            try { await ReleaseAsync(CancellationToken.None); } catch (Exception error) { errors.Add(error); }
            if (cpuApplied)
                errors.Add(new InvalidOperationException("CPU 参数已提交，但分支整体回读未确认；已释放本分支附属控制，请按实际回读重新选择模式"));
            throw new AggregateException("狂飙分支未完整应用", errors);
        }
    }

    private async Task ApplyFanAsync(FanControlPlan? plan, bool strong, CancellationToken token)
    {
        HardwareCommand command = strong
            ? new SetStrongCoolingCommand(Guid.NewGuid(), true)
            : new SetFanControlCommand(Guid.NewGuid(), plan!, true);
        await SendAsync(command, token);
        var actual = state()!.Controls;
        if (actual.StrongCooling != strong || !strong && !SameFan(actual.ActiveFanControlPlan, plan))
            throw new InvalidOperationException("分支散热策略未确认");
        ownsFan = true;
        owned = owned is null ? actual : owned with { StrongCooling = actual.StrongCooling, ActiveFanControlPlan = actual.ActiveFanControlPlan };
    }

    public bool InvalidateIfChanged(HomeStateSnapshot snapshot)
    {
        if (ActiveTier is null || Matches(snapshot.Controls)) return false;
        ActiveTier = null;
        return true;
    }

    private bool Matches(HomeControlState controls) => activeCpu is not null && MatchesCpu(controls, activeCpu) &&
        (!ownsFan || controls.StrongCooling == owned?.StrongCooling && SameFan(controls.ActiveFanControlPlan, owned?.ActiveFanControlPlan)) &&
        (!ownsGpu || controls.GpuClockLimit is { Error: null } clock && clock.SubmittedMhz == owned?.GpuClockLimit?.SubmittedMhz);

    private static bool MatchesCpu(HomeControlState? controls, CpuTuningPlan plan) => controls?.PerformanceMode == PerformanceMode.Turbo &&
        controls.CpuTuning is { } cpu && cpu.TemperatureLimitC == plan.TemperatureLimitC && cpu.SplWatts == plan.SplWatts &&
        cpu.SpptWatts == plan.SpptWatts && cpu.AcFrequency() == plan.AcMaxFrequencyMhz && cpu.DcFrequency() == plan.DcMaxFrequencyMhz && cpu.BoostEnabled == plan.BoostEnabled;

    public async Task ReleaseAsync(CancellationToken token)
    {
        ActiveTier = null;
        if (original is null || owned is null) return;
        var current = state()?.Controls ?? throw new InvalidOperationException("无法确认分支退出状态");
        if (ownsGpu)
        {
            if (current.GpuClockLimit is not { Error: null }) throw new InvalidOperationException("显卡限制无法回读，未覆盖当前设置");
            if (current.GpuClockLimit is { Error: null } gpu && gpu.SubmittedMhz == owned.GpuClockLimit?.SubmittedMhz)
            {
                await SendAsync(new SetGpuFrequencyLimitCommand(Guid.NewGuid(), original.GpuClockLimit?.SubmittedMhz, true), token);
                if (state()?.Controls.GpuClockLimit is not { Error: null } restored || restored.SubmittedMhz != original.GpuClockLimit?.SubmittedMhz)
                    throw new InvalidOperationException("显卡限制恢复未确认");
            }
            ownsGpu = false;
        }
        if (ownsFan)
        {
            current = state()!.Controls;
            if (current.StrongCooling is null) throw new InvalidOperationException("散热状态无法回读，未覆盖当前设置");
            if (current.StrongCooling == owned.StrongCooling && SameFan(current.ActiveFanControlPlan, owned.ActiveFanControlPlan))
            {
                HardwareCommand restore = original.StrongCooling == true
                    ? new SetStrongCoolingCommand(Guid.NewGuid(), true)
                    : original.ActiveFanControlPlan is { } fan
                        ? new SetFanControlCommand(Guid.NewGuid(), fan, true)
                        : new ReleaseFanControlCommand(Guid.NewGuid(), ReleaseReason.UserRequested);
                await SendAsync(restore, token);
                var restored = state()!.Controls;
                if (restored.StrongCooling != original.StrongCooling || !SameFan(restored.ActiveFanControlPlan, original.ActiveFanControlPlan))
                    throw new InvalidOperationException("散热策略恢复未确认");
            }
            ownsFan = false;
        }
        original = owned = null;
        activeCpu = null;
    }

    private async Task SendAsync(HardwareCommand command, CancellationToken token)
    {
        var result = await execute(command, token);
        if (result.State != CommandState.Applied || result.Error is not null)
            throw new InvalidOperationException($"{command.GetType().Name}: {result.Error?.Code}; {JsonSerializer.Serialize(result.Error?.Details)}");
    }

    private static bool SameFan(FanControlPlan? left, FanControlPlan? right) => JsonSerializer.Serialize(left) == JsonSerializer.Serialize(right);
}
