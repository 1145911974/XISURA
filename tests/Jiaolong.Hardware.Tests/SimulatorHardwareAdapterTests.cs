using Jiaolong.Hardware.Abstractions.Models;
using Jiaolong.Simulator;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Jiaolong.Hardware.Tests;

[TestClass]
public sealed class SimulatorHardwareAdapterTests
{
    [TestMethod]
    public async Task Readback_mismatch_scenario_returns_different_verified_value()
    {
        var fixture = new SimulatorFixture();
        var result = await fixture.ExecuteScenarioAsync("readback-mismatch.json");

        Assert.AreNotEqual(result.RequestedValue, result.ReadBackValue);
    }

    [TestMethod]
    public async Task Unknown_bios_scenario_exposes_monitoring_but_no_hardware_write_capabilities()
    {
        var fixture = new SimulatorFixture();
        var adapter = await fixture.LoadScenarioAsync("unknown-bios.json");

        Assert.IsTrue(adapter.Capabilities.CanMonitor);
        Assert.IsFalse(adapter.Capabilities.AnyHardwareWriteEnabled);
    }

    [TestMethod]
    public async Task Write_failure_scenario_seeds_sixty_seconds_and_records_failure_without_sleep()
    {
        var fixture = new SimulatorFixture();
        var adapter = await fixture.LoadScenarioAsync("write-failure.json");
        var result = await adapter.ExecuteAsync(fixture.CreateWrite(), CancellationToken.None);

        Assert.AreEqual(60, adapter.History.Count);
        Assert.AreEqual("writeFailure", result.Outcome);
        Assert.AreEqual(1, adapter.WriteRecords.Count);
        Assert.AreEqual(25, result.DelayMs);
    }

    [TestMethod]
    public async Task Rollback_failure_scenario_reports_uncertain_state()
    {
        var fixture = new SimulatorFixture();
        var adapter = await fixture.LoadScenarioAsync("write-failure.json");
        var result = await adapter.ExecuteAsync(fixture.CreateWrite(ControlKeys.GpuFrequencyLimit), CancellationToken.None);

        Assert.AreEqual("rollbackFailure", result.Outcome);
        Assert.IsFalse(result.RollbackSucceeded);
    }

    [TestMethod]
    public async Task Thermal_and_conflict_scenarios_keep_monitoring_but_disable_writes()
    {
        var fixture = new SimulatorFixture();
        var thermal = await fixture.LoadScenarioAsync("thermal-emergency.json");
        var conflict = await fixture.LoadScenarioAsync("conflict.json");

        Assert.IsTrue(thermal.Capabilities.CanMonitor);
        Assert.IsFalse(thermal.Capabilities.AnyHardwareWriteEnabled);
        Assert.IsTrue(conflict.Capabilities.ConflictDetected);
        Assert.IsFalse(conflict.Capabilities.AnyHardwareWriteEnabled);
        Assert.AreEqual("thermalEmergency", (await thermal.ReadSnapshotAsync(CancellationToken.None)).HardwareState);
        Assert.AreEqual("conflict", (await conflict.ReadSnapshotAsync(CancellationToken.None)).HardwareState);
    }

    [TestMethod]
    public async Task Baseline_exposes_all_typed_control_domains()
    {
        var fixture = new SimulatorFixture();
        var adapter = await fixture.LoadScenarioAsync("baseline.json");

        foreach (var key in ControlKeys.All.Select(value => new ControlKey(value)))
        {
            Assert.IsTrue((await adapter.ReadControlAsync(key, CancellationToken.None)).IsAvailable, key.Value);
        }
    }
}

internal sealed class SimulatorFixture
{
    private static string ScenarioPath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Scenarios", name);

    public async Task<SimulatorWriteResult> ExecuteScenarioAsync(string name)
    {
        var adapter = await LoadScenarioAsync(name);
        return await adapter.ExecuteAsync(
            new ValidatedHardwareWrite(
                Guid.NewGuid(),
                new ControlKey(ControlKeys.CpuTuning),
                NumericValue: 100,
                BooleanValue: null,
                TextValue: null),
            CancellationToken.None);
    }

    public Task<SimulatorHardwareAdapter> LoadScenarioAsync(string name) =>
        SimulatorHardwareAdapter.LoadAsync(ScenarioPath(name), CancellationToken.None);

    public ValidatedHardwareWrite CreateWrite(string key = ControlKeys.CpuTuning) =>
        new(Guid.NewGuid(), new ControlKey(key), 100, null, null);
}
