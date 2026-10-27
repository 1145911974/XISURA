using System.Reflection;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong.Hardware.Abstractions.Compatibility;
using Jiaolong.Hardware.Mechrevo.Controls;
using Jiaolong.Service.Home;

namespace Jiaolong.Service.Tests;

[TestClass]
public sealed class ProviderReadLifecycleTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [TestMethod]
    public void Paused_control_evidence_discovery_and_cached_reads_reject_before_device_access()
    {
        foreach (string operation in new[] { "controls", "baseline", "advancedBaseline", "evidence", "discovery" })
        {
            var radios = new FakeRadios();
            using var provider = new WindowsHomeHardwareProvider(radios);
            var computerField = typeof(WindowsHomeHardwareProvider).GetField("computer", Private)!;
            var originalComputer = computerField.GetValue(provider);
            // A missed discovery guard must fail without opening a real sensor device.
            computerField.SetValue(provider, null);
            provider.SetBackgroundWorkPaused(true);
            try
            {
                Exception? failure = Capture(() =>
                {
                    switch (operation)
                    {
                        case "controls": provider.ReadControlsAsync(CancellationToken.None).GetAwaiter().GetResult(); break;
                        case "baseline": provider.ReadControlBaselineAsync(CancellationToken.None).GetAwaiter().GetResult(); break;
                        case "advancedBaseline": provider.ReadControlBaselineAsync(CancellationToken.None, true).GetAwaiter().GetResult(); break;
                        case "evidence": provider.ReadModeEvidenceAsync(CancellationToken.None).GetAwaiter().GetResult(); break;
                        case "discovery": provider.ReinitializeAsync(CancellationToken.None).GetAwaiter().GetResult(); break;
                    }
                });
                Assert.AreEqual("hardwareAccessPaused", failure?.Message, operation);
                Assert.AreEqual(0, radios.Reads, operation);
            }
            finally { computerField.SetValue(provider, originalComputer); }
        }
    }

    [TestMethod]
    public void Radio_read_pause_or_failure_stops_control_sequence_without_clearing_mode_evidence()
    {
        foreach (bool fail in new[] { false, true })
        {
            WindowsHomeHardwareProvider? provider = null;
            var radios = new FakeRadios(() => provider!.SetBackgroundWorkPaused(true), fail);
            using var current = provider = new WindowsHomeHardwareProvider(radios);
            var evidence = new PerformanceModeEvidence(DateTimeOffset.UtcNow, "fake", 0, "", null, null, null, "Unknown");
            var evidenceField = typeof(WindowsHomeHardwareProvider).GetField("lastModeEvidence", Private)!;
            evidenceField.SetValue(current, evidence);
            var error = Capture(() => current.ReadControlsAsync(CancellationToken.None).GetAwaiter().GetResult());
            Assert.AreEqual("hardwareAccessPaused", error?.Message);
            Assert.AreEqual(1, radios.Reads);
            Assert.AreSame(evidence, evidenceField.GetValue(current), "No next control read after radio interruption.");
        }
    }

    [TestMethod]
    public void Gpu_probe_recovery_read_is_scoped_and_interruption_never_publishes_a_failed_capability_cache()
    {
        foreach (string stage in new[] { "entry", "during", "duringFailure", "recovery" })
        {
            int probes = 0;
            WindowsHomeHardwareProvider? provider = null;
            using var current = provider = new WindowsHomeHardwareProvider(TimeProvider.System, _ =>
            {
                probes++;
                if (stage.StartsWith("during")) provider!.SetBackgroundWorkPaused(true);
                if (stage == "duringFailure") throw new IOException("simulatedProbeFailure");
                return Task.FromResult(new NvidiaClockDevice("GPU-fake", 300, 2400));
            });
            var lifecycle = new PowerLifecycleState(current);
            if (stage == "entry") current.SetBackgroundWorkPaused(true);
            if (stage == "recovery")
            {
                long version = lifecycle.Pause();
                using (lifecycle.AllowRecoveryReads(version))
                {
                    Assert.IsNotNull(Invoke(current, "ReadGpuClockLimitLocked", CancellationToken.None));
                    Assert.AreEqual(1, probes);
                    lifecycle.Pause();
                    Assert.AreEqual("hardwareAccessPaused", Capture(() => Invoke(current, "ReadGpuClockLimitLocked", CancellationToken.None))?.Message);
                    Assert.AreEqual(1, probes, "A revoked read scope cannot return a cached capability.");
                }
            }
            else
            {
                var error = Capture(() => Invoke(current, "ReadGpuClockLimitLocked", CancellationToken.None));
                Assert.AreEqual("hardwareAccessPaused", error?.Message, stage);
                Assert.AreEqual(stage == "entry" ? 0 : 1, probes);
                Assert.IsNull(typeof(WindowsHomeHardwareProvider).GetField("gpuClockState", Private)!.GetValue(current));
                Assert.IsNull(typeof(WindowsHomeHardwareProvider).GetField("gpuClockProbeFailedAt", Private)!.GetValue(current));
            }
        }
    }

    [TestMethod]
    public void Gpu_clock_capability_without_a_probed_driver_envelope_rejects_direct_write()
    {
        using var provider = new WindowsHomeHardwareProvider(TimeProvider.System,
            _ => Task.FromResult(new NvidiaClockDevice("GPU-fake", 300, 2400)));
        var decision = new CompatibilityDecision(CompatibilityMode.Writable,
            new CapabilitySnapshot([new CapabilityDescriptor("gpuFrequencyLimit", CapabilityState.Available, null)]), null, []);
        typeof(WindowsHomeHardwareProvider).GetField("compatibilityDecision", Private)!.SetValue(provider, decision);
        var command = new SetGpuFrequencyLimitCommand(Guid.NewGuid(), 1000, true);
        var result = (CommandResult)Invoke(provider, "ExecuteGpuClockLocked", command, CancellationToken.None)!;
        Assert.AreEqual(CommandState.Rejected, result.State);
        Assert.AreEqual(HardwareMutation.NoMutation, result.Mutation);
        Assert.AreEqual("gpuClockSubmissionUnavailable", result.Error?.Details["reason"]);
        Assert.IsNull(typeof(WindowsHomeHardwareProvider).GetField("gpuClockState", Private)!.GetValue(provider),
            "A capability flag alone must not authorize driver submission without a probed envelope.");
    }

    private static object? Invoke(WindowsHomeHardwareProvider provider, string method, params object?[] args) =>
        typeof(WindowsHomeHardwareProvider).GetMethod(method, Private)!.Invoke(provider, args);

    private static Exception? Capture(Action action)
    {
        try { action(); return null; }
        catch (TargetInvocationException error) { return error.InnerException; }
        catch (Exception error) { return error; }
    }

    private sealed class FakeRadios(Action? afterRead = null, bool fail = false) : IHomeRadioStateReader
    {
        public int Reads { get; private set; }
        public Task<HomeRadioSnapshot> ReadAsync(CancellationToken token)
        {
            Reads++;
            afterRead?.Invoke();
            if (fail) throw new IOException("simulatedRadioFailure");
            return Task.FromResult(HomeRadioSnapshot.Unknown);
        }
    }
}
