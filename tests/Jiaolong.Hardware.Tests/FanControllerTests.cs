using Jiaolong.Contracts.Models;
using Jiaolong.Hardware.Mechrevo.Controls;

namespace Jiaolong.Hardware.Tests;

[TestClass]
public sealed class FanControllerTests
{
    [TestMethod]
    public async Task StaleOrEmergencyTelemetryReleasesToEcAutomatic()
    {
        var transport = new RecordingFanTransport();
        var now = DateTimeOffset.UtcNow;
        var controller = new FanController(transport);

        var stale = await controller.EvaluateAsync(
            new FanTelemetry(now.AddSeconds(-4), 50, 50),
            new FanControlPlan([new FanPoint(60, 50)]),
            CancellationToken.None);
        var thermal = await controller.EvaluateAsync(
            new FanTelemetry(now, 95, 50),
            new FanControlPlan([new FanPoint(60, 50)]),
            CancellationToken.None);

        Assert.AreEqual(ReleaseReason.SensorStale, stale.ReleaseReason);
        Assert.AreEqual(ReleaseReason.ThermalEmergency, thermal.ReleaseReason);
        Assert.AreEqual(FanOwner.EcAutomatic, transport.Owner);
        Assert.AreEqual(2, transport.ReleaseReasons.Count);
    }

    [TestMethod]
    public async Task HealthyTelemetryAppliesCurveAndLifecycleReleaseIsSafe()
    {
        var transport = new RecordingFanTransport();
        var controller = new FanController(transport);
        var plan = new FanControlPlan([new FanPoint(60, 50)]);

        var applied = await controller.EvaluateAsync(
            new FanTelemetry(DateTimeOffset.UtcNow, 50, 50), plan, CancellationToken.None);
        var released = await controller.ReleaseAsync(ReleaseReason.ServiceStopping, CancellationToken.None);

        Assert.IsTrue(applied.Applied);
        Assert.AreEqual(FanOwner.ApplicationCurve, applied.FinalOwner);
        Assert.AreEqual(FanOwner.EcAutomatic, released.FinalOwner);
        Assert.AreEqual(ReleaseReason.ServiceStopping, released.ReleaseReason);
    }

    [TestMethod]
    public async Task Invalid_or_future_temperature_samples_release_fan_control()
    {
        var transport = new RecordingFanTransport();
        var controller = new FanController(transport);
        var plan = new FanControlPlan([new FanPoint(60, 50)]);
        var now = DateTimeOffset.UtcNow;
        foreach (var telemetry in new[]
        {
            new FanTelemetry(now, double.NaN, 55),
            new FanTelemetry(now, 55, double.PositiveInfinity),
            new FanTelemetry(now, -1, 55),
            new FanTelemetry(now.AddSeconds(5), 55, 55)
        })
        {
            var result = await controller.EvaluateAsync(telemetry, plan, CancellationToken.None);
            Assert.AreEqual(ReleaseReason.SensorStale, result.ReleaseReason);
            Assert.AreEqual(FanOwner.EcAutomatic, result.FinalOwner);
        }
        Assert.AreEqual(4, transport.ReleaseReasons.Count);
    }

    private sealed class RecordingFanTransport : IFanTransport
    {
        public FanOwner Owner { get; private set; } = FanOwner.EcAutomatic;
        public List<ReleaseReason> ReleaseReasons { get; } = [];

        public Task ApplyCurveAsync(FanControlPlan plan, CancellationToken cancellationToken)
        {
            Owner = FanOwner.ApplicationCurve;
            return Task.CompletedTask;
        }

        public Task<FanOwner> ReadOwnerAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Owner);

        public Task ReleaseAsync(ReleaseReason reason, CancellationToken cancellationToken)
        {
            ReleaseReasons.Add(reason);
            Owner = FanOwner.EcAutomatic;
            return Task.CompletedTask;
        }
    }
}
