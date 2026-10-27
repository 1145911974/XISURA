using Jiaolong_ControlCenter.Services;

namespace Jiaolong.Automation.Tests;

[TestClass]
public sealed class AdaptiveFilteredSessionThermalTests
{
    [TestMethod]
    public void Missing_or_expired_temperature_blocks_turbo_upshift()
    {
        var start = DateTimeOffset.UnixEpoch;
        foreach (string sensor in new[] { "cpu", "gpu" })
        foreach (string state in new[] { "healthy", "missing", "expired" })
        {
            var session = new AdaptiveFilteredSession();
            AdaptiveTrialResult result = new(null, "");
            for (int second = 0; second <= 12; second++)
            {
                var at = start.AddSeconds(second);
                var input = Sample(AdaptiveStage.Game,
                    sensor == "cpu" && state == "missing" ? null : 70,
                    sensor == "gpu" && state == "missing" ? null : 60) with
                {
                    CpuObservedAtUtc = at,
                    GpuObservedAtUtc = at,
                    CpuTemperatureObservedAtUtc = sensor == "cpu" && state == "expired" ? at.AddSeconds(-4) : at,
                    GpuTemperatureObservedAtUtc = sensor == "gpu" && state == "expired" ? at.AddSeconds(-4) : at
                };
                result = session.Evaluate(Policy(), input, at, second);
                if (state != "healthy")
                    Assert.AreNotEqual(AdaptiveStage.Turbo, result.Target, $"{sensor} {state}");
            }
            if (state == "healthy") Assert.AreEqual(AdaptiveStage.Turbo, result.Target,
                "Identical fresh load with healthy temperatures must actually reach the upshift threshold.");
            else Assert.AreEqual(AdaptiveStage.Turbo, session.CandidateStage,
                "The temperature gate must block an otherwise eligible candidate.");
        }
    }

    [TestMethod]
    public void Sustained_soft_heat_caps_turbo_at_one_lower_stage()
    {
        var session = new AdaptiveFilteredSession();
        var start = DateTimeOffset.UnixEpoch;
        AdaptiveTrialResult result = new(null, "");
        for (int second = 0; second <= 15; second++)
        {
            var at = start.AddSeconds(second);
            result = session.Evaluate(Policy(), Sample(AdaptiveStage.Turbo, 91, 60) with
            {
                CpuObservedAtUtc = at,
                GpuObservedAtUtc = at,
                CpuTemperatureObservedAtUtc = at,
                GpuTemperatureObservedAtUtc = at
            }, at, second);
            if (second < 15)
            {
                Assert.IsNull(result.Target, "Soft heat must persist for fifteen seconds.");
                Assert.IsFalse(session.RequiresEcRelease);
            }
        }

        Assert.AreEqual(AdaptiveStage.Game, result.Target);
        Assert.IsTrue(session.RequiresEcRelease);
    }

    [TestMethod]
    public void Emergency_releases_to_office_and_recovery_does_not_immediately_upshift()
    {
        var session = new AdaptiveFilteredSession();
        var start = DateTimeOffset.UnixEpoch;
        AdaptiveTrialResult result = new(null, "");
        AdaptiveStage current = AdaptiveStage.Turbo;
        for (int second = 0; second <= 3; second++)
        {
            var at = start.AddSeconds(second);
            result = session.Evaluate(Policy(), Sample(current, 95, 60) with
            {
                CpuObservedAtUtc = at,
                GpuObservedAtUtc = at,
                CpuTemperatureObservedAtUtc = at,
                GpuTemperatureObservedAtUtc = at
            }, at, second);
            if (second < 3)
            {
                Assert.IsNull(result.Target, "Emergency heat must persist for three seconds.");
                Assert.IsFalse(session.RequiresEcRelease);
            }
            if (result.Target is { } target) current = target;
        }

        Assert.AreEqual(AdaptiveStage.Office, result.Target);
        Assert.IsTrue(session.RequiresEcRelease);

        for (int second = 4; second <= 33; second++)
        {
            var at = start.AddSeconds(second);
            result = session.Evaluate(Policy(), Sample(current, 60, 60) with
            {
                CpuObservedAtUtc = at,
                GpuObservedAtUtc = at,
                CpuTemperatureObservedAtUtc = at,
                GpuTemperatureObservedAtUtc = at
            }, at, second);
            Assert.IsFalse(result.Target is AdaptiveStage.Game or AdaptiveStage.Turbo,
                "Cooling after emergency must honor the thermal hold before any upshift.");
            Assert.IsTrue(session.RequiresEcRelease);
        }
        var recoveredAt = start.AddSeconds(34);
        result = session.Evaluate(Policy(), Sample(current, 60, 60), recoveredAt, 34);
        Assert.IsFalse(session.RequiresEcRelease, "Thirty seconds of fresh cooling releases the cap.");
        Assert.IsNull(result.Target, "Load candidates must start again after thermal recovery.");
        result = session.Evaluate(Policy(), Sample(current, 60, 60), recoveredAt.AddSeconds(1), 35);
        Assert.AreEqual(AdaptiveStage.Game, result.Target);
    }

    [TestMethod]
    public void Cached_hot_temperature_does_not_accumulate_emergency_duration()
    {
        var session = new AdaptiveFilteredSession();
        var start = DateTimeOffset.UnixEpoch;
        for (int second = 0; second <= 5; second++)
        {
            var result = session.Evaluate(Policy(), Sample(AdaptiveStage.Turbo, 95, 60) with
            {
                CpuTemperatureObservedAtUtc = start,
                GpuTemperatureObservedAtUtc = start
            }, start.AddSeconds(second), second);
            Assert.IsNull(result.Target, "A cached hot sample cannot prove sustained emergency heat.");
            Assert.IsFalse(session.RequiresEcRelease);
        }
    }

    private static AdaptiveTriggerPolicy Policy() => new(40, 30, 1, true, 70, 70, 10, 20, 15, 1)
    {
        Advanced = new()
        {
            CpuTemperatureCeiling = 90,
            GpuTemperatureCeiling = 80,
            CooldownSeconds = 1,
            MinimumDwellSeconds = 1
        },
        TurboExitSeconds = 1,
        TurboMinimumDwellSeconds = 1
    };

    private static AdaptiveTrialInput Sample(AdaptiveStage current, double? cpuTemp, double? gpuTemp) => new()
    {
        Current = current,
        AcConnected = true,
        BatterySaver = false,
        CpuPercent = 100,
        GpuPercent = 100,
        CpuTemperature = cpuTemp,
        GpuTemperature = gpuTemp,
        CpuEmergencyTemperatureC = 95,
        GpuEmergencyTemperatureC = 87
    };
}
