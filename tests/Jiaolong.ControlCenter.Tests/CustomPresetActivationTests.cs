using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.Services;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class CustomPresetActivationTests
{
    [TestMethod]
    public async Task Activation_confirms_success_and_checks_originals_after_cancel_or_failed_recovery()
    {
        var cpu = new CpuTuningState(75, 55, 65, 4700, true, 8, Guid.NewGuid(), -20, "hardwareReadback");
        foreach (var outcome in new[] { "missing", "success", "rollback", "mismatch" })
        {
            var current = new HomeControlState(PerformanceMode.Turbo, false, []) { CpuTuning = cpu };
            var writes = new List<PerformanceMode>();
            var result = await CustomPresetActivation.RunAsync(
                () => current,
                mode =>
                {
                    writes.Add(mode);
                    current = current with { PerformanceMode = mode,
                        CpuTuning = outcome == "mismatch" && mode == PerformanceMode.Turbo
                            ? cpu with { SplWatts = 60 } : cpu };
                    return Task.FromResult(true);
                },
                _ => Task.FromResult(true),
                async prepare => outcome != "missing" && await prepare() && outcome == "success");
            Assert.AreEqual(outcome == "success", result.Applied, outcome);
            Assert.AreEqual(outcome != "mismatch", result.RecoveryVerified, outcome);
            CollectionAssert.AreEqual(outcome switch
            {
                "missing" => Array.Empty<PerformanceMode>(),
                "success" => new[] { PerformanceMode.Custom },
                _ => new[] { PerformanceMode.Custom, PerformanceMode.Turbo }
            }, writes.ToArray(), outcome);
        }

        var unrelatedChange = new HomeControlState(PerformanceMode.Turbo, false, []) { CpuTuning = cpu };
        bool recoveryWrite = false;
        var canceled = await CustomPresetActivation.RunAsync(
            () => unrelatedChange,
            _ => { recoveryWrite = true; return Task.FromResult(true); },
            _ => { recoveryWrite = true; return Task.FromResult(true); },
            _ =>
            {
                unrelatedChange = unrelatedChange with { CpuTuning = cpu with { SplWatts = 60 } };
                return Task.FromResult(false);
            });
        Assert.IsFalse(canceled.Applied);
        Assert.IsFalse(recoveryWrite, "Canceling before confirmation must not restore unrelated hardware changes.");
        foreach (var target in Enum.GetValues<PerformanceMode>())
        {
            var current = new HomeControlState(PerformanceMode.Turbo, false, []) { CpuTuning = cpu };
            var result = await CustomPresetActivation.RunAsync(() => current,
                mode => { current = current with { PerformanceMode = mode }; return Task.FromResult(true); },
                _ => Task.FromResult(true), prepare => prepare(), target);
            Assert.IsTrue(result.Applied, target.ToString());
            Assert.AreEqual(target, current.PerformanceMode);
        }
    }
}
