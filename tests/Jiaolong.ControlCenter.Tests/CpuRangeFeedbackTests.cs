using Jiaolong_ControlCenter.ViewModels;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class CpuRangeFeedbackTests
{
    [TestMethod]
    public void Invalid_current_and_temperature_report_the_actual_manual_input_bounds()
    {
        var draft = new PerformanceDraft { AdvancedCpuTuning = new AdvancedCpuTuningDraft {
            VrmCurrentMilliamps = 0, Mp1TemperatureC = 101 } };
        var invalid = PerformanceDraftValidator.Validate(draft);
        Assert.IsFalse(invalid.IsValid);
        Assert.IsTrue(invalid.Errors.Contains("SMU 电流限制必须在 1000–200000mA 之间"));
        Assert.IsTrue(invalid.Errors.Contains("SMU 温度限制必须在 40–100°C 之间"));
        foreach (var limits in new[] { (1000, 40), (200_000, 100) })
        {
            var valid = PerformanceDraftValidator.Validate(draft with { AdvancedCpuTuning = new AdvancedCpuTuningDraft {
                VrmCurrentMilliamps = limits.Item1, Mp1TemperatureC = limits.Item2 } });
            Assert.IsTrue(valid.IsValid, string.Join("; ", valid.Errors));
        }
    }
}
