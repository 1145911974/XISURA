using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.Services;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class GpuModeFollowObserverTests
{
    private static long evidenceSequence;

    [TestMethod]
    public void New_modes_require_two_distinct_fresh_observations_and_emit_once()
    {
        var observer = new PageModeFollowObserver();
        Assert.IsNull(observer.Observe(State(PerformanceMode.Balanced), 1, true));
        var first = State(PerformanceMode.Quiet);
        Assert.IsNull(observer.Observe(first, 1, true));
        Assert.IsNull(observer.Observe(first, 1, true), "The same evidence cannot count twice.");
        Assert.AreEqual(PerformanceMode.Quiet, observer.Observe(State(PerformanceMode.Quiet), 1, true));
        Assert.IsNull(observer.Observe(State(PerformanceMode.Quiet), 1, true));
        Assert.IsNull(observer.Observe(State(PerformanceMode.Turbo), 1, true));
        Assert.IsNull(observer.Observe(State(PerformanceMode.Balanced), 1, true));
        Assert.IsNull(observer.Observe(State(PerformanceMode.Turbo), 1, true));
        Assert.AreEqual(PerformanceMode.Turbo, observer.Observe(State(PerformanceMode.Turbo), 1, true));
    }

    [TestMethod]
    public void Invalid_evidence_resets_the_baseline_without_replaying_the_recovered_mode()
    {
        Func<HomeStateSnapshot, HomeStateSnapshot>[] invalid =
        [
            state => state with { Controls = state.Controls with { PerformanceMode = null } },
            state => state with { Controls = state.Controls with { ModeEvidence = null } },
            state => Evidence(state, state.Controls.ModeEvidence! with { RawValue = null }),
            state => Evidence(state, state.Controls.ModeEvidence! with { ParsedMode = PerformanceMode.Turbo }),
            state => Evidence(state, state.Controls.ModeEvidence! with { CapturedAtUtc = DateTimeOffset.UtcNow.AddSeconds(-2) }),
            state => Evidence(state, state.Controls.ModeEvidence! with { CapturedAtUtc = DateTimeOffset.UtcNow.AddSeconds(2) }),
            state => state with { Controls = state.Controls with { PerformanceMode = PerformanceMode.Custom } }
        ];
        foreach (var invalidate in invalid)
        {
            var observer = new PageModeFollowObserver();
            Assert.IsNull(observer.Observe(State(PerformanceMode.Balanced), 1, true));
            Assert.IsNull(observer.Observe(State(PerformanceMode.Quiet), 1, true));
            Assert.IsNull(observer.Observe(invalidate(State(PerformanceMode.Quiet)), 1, true));
            Assert.IsNull(observer.Observe(State(PerformanceMode.Quiet), 1, true));
            Assert.IsNull(observer.Observe(State(PerformanceMode.Quiet), 1, true));
            Assert.IsNull(observer.Observe(State(PerformanceMode.Balanced), 1, true));
            Assert.AreEqual(PerformanceMode.Balanced, observer.Observe(State(PerformanceMode.Balanced), 1, true));
        }
    }

    [TestMethod]
    public void Disabled_automation_and_readonly_observations_do_not_replay_on_reenable()
    {
        (bool CanApply, Func<HomeStateSnapshot, HomeStateSnapshot> Change)[] gates =
        [
            (false, state => state),
            (true, state => state with { Controls = state.Controls with
                { AdaptiveAutomation = new(true, false, null, null, null, null, null, null) } }),
            (true, state => state with { Capabilities = state.Capabilities with { SupportState = DeviceSupportState.ReadOnly } }),
            (true, state => state with { Capabilities = state.Capabilities with
                { Items = [new("gpuFrequencyLimit", CapabilityState.ReadOnly, null)] } }),
            (true, state => state with { Capabilities = state.Capabilities with
                { Items = [new("performanceMode", CapabilityState.Available, null)] } })
        ];
        foreach (var (canApply, change) in gates)
        {
            var observer = new PageModeFollowObserver();
            Assert.IsNull(observer.Observe(State(PerformanceMode.Balanced), 1, true));
            Assert.IsNull(observer.Observe(change(State(PerformanceMode.Quiet)), 1, canApply));
            Assert.IsNull(observer.Observe(State(PerformanceMode.Quiet), 1, true));
            Assert.IsNull(observer.Observe(State(PerformanceMode.Quiet), 1, true));
            Assert.IsNull(observer.Observe(State(PerformanceMode.Turbo), 1, true));
            var memoryOnly = State(PerformanceMode.Turbo);
            memoryOnly = memoryOnly with { Capabilities = memoryOnly.Capabilities with
                { Items = [new("gpuMemoryOffset", CapabilityState.Available, null)] } };
            Assert.AreEqual(PerformanceMode.Turbo, observer.Observe(memoryOnly, 1, true));
        }
    }

    [TestMethod]
    public void Reconnect_reset_and_manual_targets_consume_their_baseline_without_duplicate_follow()
    {
        var observer = new PageModeFollowObserver();
        Assert.IsNull(observer.Observe(State(PerformanceMode.Balanced), 1, true));
        Assert.IsNull(observer.Observe(State(PerformanceMode.Quiet), 1, true));
        Assert.IsNull(observer.Observe(State(PerformanceMode.Quiet), 2, true));
        Assert.IsNull(observer.Observe(State(PerformanceMode.Quiet), 2, true));
        observer.Reset();
        Assert.IsNull(observer.Observe(State(PerformanceMode.Turbo), 2, true));
        Assert.IsNull(observer.Observe(State(PerformanceMode.Balanced), 2, true,
            manualTarget: PerformanceMode.Balanced, manualInFlight: true));
        Assert.IsNull(observer.Observe(State(PerformanceMode.Balanced), 2, true));
        Assert.IsNull(observer.Observe(State(PerformanceMode.Quiet), 2, true, manualTarget: PerformanceMode.Quiet));
        Assert.IsNull(observer.Observe(State(PerformanceMode.Quiet), 2, true));
        Assert.IsNull(observer.Observe(State(PerformanceMode.Turbo), 2, true));
        Assert.AreEqual(PerformanceMode.Turbo, observer.Observe(State(PerformanceMode.Turbo), 2, true));
    }

    private static HomeStateSnapshot Evidence(HomeStateSnapshot state, PerformanceModeEvidence evidence) =>
        state with { Controls = state.Controls with { ModeEvidence = evidence } };

    private static HomeStateSnapshot State(PerformanceMode mode) => new(
        new CapabilitySnapshot([new("gpuFrequencyLimit", CapabilityState.Available, null)])
            { SupportState = DeviceSupportState.Ready }, null, new HomeControlState(mode, false, [])
        {
            ModeEvidence = new(DateTimeOffset.UtcNow.AddMilliseconds(-100).AddTicks(Interlocked.Increment(ref evidenceSequence)), "test", 0, "00",
                mode switch { PerformanceMode.Balanced => 0, PerformanceMode.Turbo => 1, _ => 2 },
                mode, null, "InterfaceAcknowledged")
        });
}
