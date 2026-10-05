using Jiaolong.Contracts.Commands;
using Jiaolong_ControlCenter.ViewModels;
using System.IO;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class AdvancedCpuContractTests
{
    [TestMethod]
    public void Advanced_cpu_plan_exposes_the_complete_smu_and_pbo_field_set()
    {
        foreach (var name in new[]
        {
            "StapmWatts", "FastPptWatts", "SlowPptWatts", "PptWatts",
            "VrmCurrentMilliamps", "TdcCurrentMilliamps", "EdcCurrentMilliamps",
            "Mp1TemperatureC", "RsmuTemperatureC", "PboScalar", "OcClockMhz",
            "PerCoreOcClockMhz", "CurveOptimizerAll", "PerCoreCurveOptimizer"
        })
            Assert.IsNotNull(typeof(AdvancedCpuTuningDraft).GetProperty(name), name);
    }

    [TestMethod]
    public void Performance_command_factory_carries_advanced_cpu_values()
    {
        var advanced = new AdvancedCpuTuningDraft { StapmWatts = 45, PboScalar = 2, CurveOptimizerAll = -15 };
        var draft = new PerformanceDraft { AdvancedCpuTuning = advanced };

        var command = PerformanceCommandFactory.Create(draft, riskConfirmed: true);

        Assert.AreEqual(45, command.Plan.Advanced!.StapmWatts);
        Assert.AreEqual(2, command.Plan.Advanced.PboScalar);
        Assert.AreEqual(-15, command.Plan.Advanced.CurveOptimizerAll);
        var legacy = System.Text.Json.JsonSerializer.Deserialize<AdvancedCpuTuningDraft>("{\"OverclockEnabled\":true,\"OcClockMhz\":3800,\"OcVoltageMillivolts\":1000,\"PboScalar\":2}")!;
        Assert.IsNull(legacy.OverclockEnabled);
        Assert.IsNull(legacy.OcClockMhz);
        Assert.AreEqual(2, legacy.PboScalar);
        var state = new Jiaolong.Contracts.Models.CpuTuningState(null, null, null, 4700, true, null, null, null, "fixture");
        var split = new PerformanceDraft { AcMaxFrequencyMhz = 4700, DcMaxFrequencyMhz = 3500, NegativeCurveOptimizer = null };
        var splitPlan = PerformanceCommandFactory.CreatePresetCommands(split, state, true, true, true, true);
        var windows = splitPlan.Steps.Single(item => item.Label.StartsWith("Windows"));
        Assert.AreEqual(4700, windows.Command.Plan.AcMaxFrequencyMhz);
        Assert.AreEqual(3500, windows.Command.Plan.DcMaxFrequencyMhz);
        var unchanged = state with { AcMaxFrequencyMhz = 4700, DcMaxFrequencyMhz = 3500 };
        var fullPlan = PerformanceCommandFactory.CreatePresetCommands(split, unchanged, true, true, true, true, forceApply: true);
        var fullWindows = fullPlan.Steps.Single(item => item.Label.StartsWith("Windows")).Command.Plan;
        Assert.AreEqual(4700, fullWindows.AcMaxFrequencyMhz);
        Assert.AreEqual(3500, fullWindows.DcMaxFrequencyMhz);
        Assert.AreEqual(split.IsBoostEnabled, fullWindows.BoostEnabled);
        var unavailable = PerformanceCommandFactory.CreatePresetCommands(split, state with { MaxFrequencyMhz = null, AcMaxFrequencyMhz = 4700 }, true, true, true, true);
        Assert.IsTrue(unavailable.Skipped.Any(item => item.Contains("电池最高频率")));
        Assert.IsFalse(unavailable.Steps.Any(item => item.Command.Plan.AcMaxFrequencyMhz is not null || item.Command.Plan.DcMaxFrequencyMhz is not null));
        foreach (var curveDraft in new[] {
            new AdvancedCpuTuningDraft { CurveOptimizerMode = CpuCurveOptimizerMode.AllCore, CurveOptimizerAll = -15 },
            new AdvancedCpuTuningDraft { CurveOptimizerMode = CpuCurveOptimizerMode.PerCore,
                PerCoreCurveOptimizer = new Dictionary<int, int> { [0] = -15 } } })
        {
            var curvePlan = PerformanceCommandFactory.CreatePresetCommands(new PerformanceDraft {
                AdvancedCpuTuning = curveDraft }, state, true, true, true, true);
            var step = curvePlan.Steps.Single(item => item.Label.Contains("Curve Optimizer"));
            Assert.IsNotNull(step.ReadbackPlan);
            Assert.IsFalse(PerformanceCommandFactory.MatchesReadBack(state, step.ReadbackPlan));
            var actual = state with { PerCoreCurveOptimizer = Enumerable.Range(0, 8).ToDictionary(core => core, _ => -15) };
            Assert.IsTrue(PerformanceCommandFactory.MatchesReadBack(actual, step.ReadbackPlan));
            Assert.IsFalse(PerformanceCommandFactory.MatchesReadBack(actual with {
                PerCoreCurveOptimizer = Enumerable.Range(0, 8).ToDictionary(core => core, _ => -14) }, step.ReadbackPlan));
        }
    }

    [TestMethod]
    public void Performance_workspace_uses_the_approved_block_layout_and_separate_actions()
    {
        var xaml = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "PerformanceWorkspace.xaml");
        var code = ReadSource("src", "Jiaolong.ControlCenter", "Prototype", "Controls", "PerformanceWorkspace.xaml.cs");

        StringAssert.Contains(xaml, "<shared:ModePresetPicker");
        StringAssert.Contains(xaml, "Content=\"保存预设\"");
        StringAssert.Contains(xaml, "Content=\"使用预设\"");
        StringAssert.Contains(xaml, "CPU 运行边界");
        StringAssert.Contains(xaml, "x:Name=\"AdvancedCpuTuningExpander\"");
        StringAssert.Contains(xaml, "ExpandDirection=\"Down\"");
        StringAssert.Contains(xaml, "x:Name=\"AdvancedSmuPanel\"");
        StringAssert.Contains(xaml, "x:Name=\"AdvancedPboPanel\"");
        Assert.IsFalse(xaml.Contains("参数说明与安全边界", StringComparison.Ordinal));

        StringAssert.Contains(code, "SaveDraftAsync");
        StringAssert.Contains(code, "ApplyDraftToHardwareAsync");
        var saveStart = code.IndexOf("SaveDraftAsync", StringComparison.Ordinal);
        var applyStart = code.IndexOf("ApplyDraftToHardwareAsync", StringComparison.Ordinal);
        Assert.IsTrue(saveStart >= 0 && applyStart > saveStart);
        var saveBodyEnd = code.IndexOf("private async void OnRestorePresetClick", saveStart, StringComparison.Ordinal);
        Assert.IsTrue(saveBodyEnd > saveStart);
        Assert.IsFalse(code[saveStart..saveBodyEnd].Contains("ApplyDraftToHardwareAsync", StringComparison.Ordinal));
    }

    private static string ReadSource(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Jiaolong.ControlCenter.slnx")))
            directory = directory.Parent;

        var root = directory?.FullName ?? throw new DirectoryNotFoundException("无法定位蛟龙仓库根目录。");
        return File.ReadAllText(Path.Combine(new[] { root }.Concat(parts).ToArray()));
    }
}
