using System.Text.Json;
using Jiaolong.Contracts.Models;

namespace Jiaolong_ControlCenter.Services;

public sealed record CustomPresetActivationResult(bool Applied, bool RecoveryVerified, string Message);

public static class CustomPresetActivation
{
    public static async Task<CustomPresetActivationResult> RunAsync(
        Func<HomeControlState?> readState,
        Func<PerformanceMode, Task<bool>> setMode,
        Func<CpuTuningState, Task<bool>> restoreWindows,
        Func<Func<Task<bool>>, Task<bool>> applyPreset,
        PerformanceMode targetMode = PerformanceMode.Custom)
    {
        var original = readState();
        if (original?.PerformanceMode is not { } originalMode || original.CpuTuning is null)
            return new(false, true, "当前模式或 CPU 原值未读回，未应用自定义预设");

        bool modeAttempted = false;
        bool preparationEntered = false;
        try
        {
            bool applied = await applyPreset(async () =>
            {
                preparationEntered = true;
                if (readState()?.PerformanceMode == targetMode) return true;
                modeAttempted = true;
                return await setMode(targetMode) && readState()?.PerformanceMode == targetMode;
            });
            if (applied && readState()?.PerformanceMode == targetMode)
                return new(true, true, "性能预设已应用，硬件读回已确认");
        }
        catch (Exception error)
        {
            AppRuntimeLog.Write($"[{DateTimeOffset.Now:O}] Custom preset activation: {error}\n");
        }

        if (!preparationEntered)
            return new(false, true, "自定义预设未应用；没有提交命令");

        try
        {
            if (modeAttempted && !await setMode(originalMode))
                return new(false, false, "自定义预设未完成，原模式恢复未确认；请重新读取硬件");

            // Firmware mode restoration may reset OEM limits. Restore only known Windows settings;
            // out-of-range SMU originals require native rollback, never a guessed write.
            if (!SameCpu(original.CpuTuning, readState()?.CpuTuning) &&
                !await restoreWindows(original.CpuTuning))
                return new(false, false, "自定义预设未完成，Windows 设置恢复未确认；请重新读取硬件");

            bool restored = readState()?.PerformanceMode == originalMode &&
                SameCpu(original.CpuTuning, readState()?.CpuTuning);
            return new(false, restored, restored
                ? "自定义预设未应用；原控制值已核对"
                : "自定义预设未完成，原 CPU 控制值恢复未确认；请重新读取硬件");
        }
        catch (Exception error)
        {
            AppRuntimeLog.Write($"[{DateTimeOffset.Now:O}] Custom preset recovery: {error}\n");
            return new(false, false, "自定义预设未完成，恢复结果未知；请重新读取硬件");
        }
    }

    private static bool SameCpu(CpuTuningState first, CpuTuningState? second) => second is not null &&
        JsonSerializer.Serialize(first with { WindowsPowerSchemeName = null }) ==
        JsonSerializer.Serialize(second with { WindowsPowerSchemeName = null });
}
