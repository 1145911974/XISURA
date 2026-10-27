using ControlCenter = Jiaolong_ControlCenter.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Jiaolong.Service.Tests;

[TestClass]
public sealed class AdaptiveForegroundExitGraceTests
{
    [TestMethod]
    public void Foreground_absence_waits_for_grace_then_returns_to_load_target_once()
    {
        var start = DateTimeOffset.UnixEpoch;
        var policy = Policy(exitSeconds: 1);
        var session = new ControlCenter.AdaptiveFilteredSession();
        var input = Sample(ControlCenter.AdaptiveStage.Game, foreground: true);
        ControlCenter.AdaptiveTrialResult Tick(int second) =>
            session.Evaluate(policy, input, start.AddSeconds(second), second);

        for (int second = 0; second <= 3; second++)
        {
            var result = Tick(second);
            if (second < 3) Assert.IsNull(result.Target);
            else Assert.AreEqual(ControlCenter.AdaptiveStage.Turbo, result.Target);
        }

        input = input with { Current = ControlCenter.AdaptiveStage.Turbo, Foreground = false };
        for (int second = 4; second <= 6; second++)
            Assert.IsNull(Tick(second).Target, "The foreground rule remains active during its three-second absence grace.");

        Assert.AreEqual(ControlCenter.AdaptiveStage.Game, Tick(7).Target);
        input = input with { Current = ControlCenter.AdaptiveStage.Game };
        Assert.IsNull(Tick(8).Target, "A completed fallback must not trigger a second restore.");
    }

    [TestMethod]
    public void Foreground_reappearance_cancels_old_grace_and_starts_a_fresh_one()
    {
        var start = DateTimeOffset.UnixEpoch;
        var policy = Policy(exitSeconds: 7);
        var session = new ControlCenter.AdaptiveFilteredSession();
        var input = Sample(ControlCenter.AdaptiveStage.Game, foreground: true);
        ControlCenter.AdaptiveTrialResult Tick(int second) =>
            session.Evaluate(policy, input, start.AddSeconds(second), second);

        for (int second = 0; second <= 3; second++) Tick(second);
        input = input with { Current = ControlCenter.AdaptiveStage.Turbo, Foreground = false };
        Assert.IsNull(Tick(4).Target);
        Assert.IsNull(Tick(5).Target);

        input = input with { Foreground = true };
        Assert.IsNull(Tick(6).Target);
        Assert.IsNull(Tick(7).Target, "Reappearance before the deadline cancels the pending exit.");

        input = input with { Foreground = false };
        for (int second = 8; second <= 10; second++)
            Assert.IsNull(Tick(second).Target, "A new absence must receive its full grace period.");
        Assert.AreEqual(ControlCenter.AdaptiveStage.Game, Tick(11).Target);
        input = input with { Current = ControlCenter.AdaptiveStage.Game };
        Assert.IsNull(Tick(12).Target);
    }

    private static ControlCenter.AdaptiveTriggerPolicy Policy(int exitSeconds) =>
        new(50, 50, 1, true, 85, 85, 1, 5, 5, 1)
        {
            TurboExitCpuPercent = 70,
            TurboExitGpuPercent = 75,
            TurboExitSeconds = exitSeconds,
            TurboMinimumDwellSeconds = 1,
            Advanced = new()
            {
                CooldownSeconds = 1,
                MinimumDwellSeconds = 1,
                CpuTemperatureCeiling = 90,
                GpuTemperatureCeiling = 80,
                ApplicationSeconds = 1,
                ApplicationRules =
                [
                    new("game.exe", true, ControlCenter.AdaptiveStage.Turbo) { RequireActivity = false }
                ]
            }
        };

    private static ControlCenter.AdaptiveTrialInput Sample(ControlCenter.AdaptiveStage current, bool foreground) => new()
    {
        Current = current,
        AcConnected = true,
        BatteryPercent = 95,
        BatterySaver = false,
        CpuPercent = 10,
        GpuPercent = 10,
        CpuTemperature = 60,
        GpuTemperature = 50,
        Executable = "game.exe",
        Foreground = foreground,
        ApplicationContextAvailable = true
    };
}
