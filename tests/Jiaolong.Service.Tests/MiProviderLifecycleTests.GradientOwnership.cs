using System.Diagnostics;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong.Hardware.Abstractions.Compatibility;
using Jiaolong.Hardware.Mechrevo.Controls;
using Jiaolong.Hardware.Mechrevo.Wmi;
using Jiaolong.Service.Home;

namespace Jiaolong.Service.Tests;

public sealed partial class MiProviderLifecycleTests
{
    [TestMethod]
    public void Gradient_snapshot_preserves_software_effect_only_while_physical_frame_matches()
    {
        using var fixture = new GradientFixture();
        Assert.AreEqual(GradientFixture.Plan with { LogoEnabled = true }, fixture.Read());
        fixture.Bus.Values[15] = [0];
        Assert.AreEqual(false, fixture.Read()!.LogoEnabled);
        fixture.Bus.Values[18] = [2];
        var actual = fixture.Read();
        Assert.AreEqual("Fixed", actual!.Effect);
        Assert.AreEqual(2, actual.BrightnessLevel);
        Assert.IsNull(fixture.Cache);
        Assert.AreEqual(0, fixture.Bus.Writes);
    }

    [TestMethod]
    public void Gradient_external_frame_mode_level_or_unreadable_state_stops_before_next_write()
    {
        foreach (string change in new[] { "color", "mode", "level", "unreadable" })
        {
            using var fixture = new GradientFixture();
            fixture.Change(change);
            fixture.Tick();
            Assert.AreEqual(0, fixture.Bus.Writes, change);
            Assert.IsNull(fixture.Cache, change);
            var actual = fixture.Read();
            if (change == "unreadable") Assert.IsNull(actual);
            else Assert.AreEqual("Fixed", actual!.Effect);
        }
        using var paused = new GradientFixture();
        paused.Provider.SetBackgroundWorkPaused(true);
        paused.Tick();
        Assert.AreEqual(0, paused.Bus.Reads);
        Assert.AreEqual(0, paused.Bus.Writes);
        Assert.IsNotNull(paused.Cache);
    }

    [TestMethod]
    public void Gradient_preview_expiry_and_dispose_preserve_unconfirmed_original_without_overwriting_external_state()
    {
        foreach (string change in new[] { "color", "unreadable" })
        {
            using var fixture = new GradientFixture();
            fixture.Lease.Begin(new("Fixed", 67) { BrightnessLevel = 2, Red = 1, Green = 2, Blue = 3 }, DateTimeOffset.UtcNow.AddMinutes(-1));
            fixture.Change(change);
            fixture.Tick();
            Assert.AreEqual(0, fixture.Bus.Writes, change);
            Assert.IsNotNull(fixture.Lease.Original);
            Assert.AreEqual(CommandState.Rejected, fixture.ApplyPreview().State,
                "A later preview cannot reuse an original snapshot whose ownership was lost.");
            fixture.Provider.Dispose();
            Assert.AreEqual(0, fixture.Bus.Writes, "Dispose cannot bypass lost preview ownership.");
            Assert.IsNotNull(fixture.Lease.Original);
        }
        foreach (string failure in new[] { "competing", "frameFailure", "rollbackFailed" })
        {
            using var fixture = new GradientFixture();
            fixture.Lease.Begin(new("Fixed", 67) { BrightnessLevel = 2, Red = 1, Green = 2, Blue = 3 }, DateTimeOffset.UtcNow);
            if (failure == "rollbackFailed")
            {
                fixture.Bus.FailWrites = true;
                Assert.AreEqual(CommandState.RecoveryRequired, fixture.ApplyPreview().State);
            }
            else
            {
                fixture.Competing = failure == "competing";
                fixture.Bus.FailWrites = failure == "frameFailure";
                typeof(WindowsHomeHardwareProvider).GetField("lightingFailures", Private)!.SetValue(fixture.Provider, 2);
                fixture.Tick();
            }
            int writes = fixture.Bus.Writes;
            fixture.Competing = fixture.Bus.FailWrites = false;
            Assert.AreEqual(CommandState.RecoveryRequired, fixture.RestorePreview().State, failure);
            fixture.Provider.Dispose();
            Assert.AreEqual(writes, fixture.Bus.Writes, "Terminal failure cannot authorize a stale preview restore: " + failure);
            Assert.IsNotNull(fixture.Lease.Original);
        }
        using var ordinary = new GradientFixture();
        ordinary.Lease.Begin(new("Fixed", 67) { BrightnessLevel = 2, Red = 1, Green = 2, Blue = 3 }, DateTimeOffset.UtcNow.AddMinutes(-1));
        ordinary.Tick();
        Assert.IsTrue(ordinary.Bus.Writes > 0);
        Assert.IsNull(ordinary.Lease.Original);
        Assert.AreEqual(2, ordinary.Read()!.BrightnessLevel);
    }

    private sealed class GradientFixture : IDisposable
    {
        public static readonly KeyboardLightingPlan Plan = new("Gradient", 100)
        { BrightnessLevel = 3, Red = 255, Green = 165, Blue = 0, Speed = 1.5 };
        public readonly WindowsHomeHardwareProvider Provider;
        public readonly GradientBus Bus = new();
        public bool Competing;
        private static readonly Type Type = typeof(WindowsHomeHardwareProvider);
        public KeyboardLightingPlan? Cache => (KeyboardLightingPlan?)Type.GetField("appliedLighting", Private)!.GetValue(Provider);
        public LightingPreviewLease Lease => (LightingPreviewLease)Type.GetField("lightingPreview", Private)!.GetValue(Provider)!;
        public GradientFixture()
        {
            Provider = new(() => Competing);
            var client = new MiCommonInterfaceClient(Bus, Bus);
            var decision = new CompatibilityDecision(CompatibilityMode.Writable,
                new CapabilitySnapshot([new("keyboardLighting", CapabilityState.Available, null),
                    new("quickSetting:lidLogo", CapabilityState.Available, null)]),
                new CompatibilityManifest { ManifestId = "fake", WmiProvider = new()
                { Namespace = "root\\WMI", Class = "MICommonInterface", InstanceName = "fake", ReadType = 250, WriteType = 251 } }, []);
            Set("miInterface", client); Set("compatibilityDecision", decision);
            Set("lightingController", new MiKeyboardLightingController(client, decision));
            Set("appliedLighting", Plan); Set("lastLightingColor", Plan.ColorAt(0));
            Set("observedMiProvider", decision.Manifest!.WmiProvider);
            var clock = (Stopwatch)Type.GetField("lightingClock", Private)!.GetValue(Provider)!;
            clock.Start();
            Assert.IsTrue(SpinWait.SpinUntil(() => clock.ElapsedMilliseconds >= 25, 1000));
        }
        private void Set(string field, object? value) => Type.GetField(field, Private)!.SetValue(Provider, value);
        public KeyboardLightingPlan? Read() => (KeyboardLightingPlan?)Type.GetMethod("ReadLightingLocked", Private)!.Invoke(Provider, [CancellationToken.None]);
        public void Tick() => Type.GetMethod("OnLightingFrame", Private)!.Invoke(Provider, [null]);
        public CommandResult ApplyPreview() => (CommandResult)Type.GetMethod("ExecuteLightingLocked", Private)!
            .Invoke(Provider, [new SetKeyboardLightingCommand(Guid.NewGuid(), Plan) { Preview = true }, CancellationToken.None])!;
        public CommandResult RestorePreview() => (CommandResult)Type.GetMethod("RestoreLightingPreviewLocked", Private)!
            .Invoke(Provider, [Guid.NewGuid(), CancellationToken.None])!;
        public void Change(string change)
        {
            if (change == "color") Bus.Values[17] = [0, 0, 255];
            if (change == "mode") Bus.Values[16] = [0];
            if (change == "level") Bus.Values[18] = [2];
            if (change == "unreadable") Bus.Unavailable = true;
        }
        public void Dispose() { Provider.Dispose(); Lease.Complete(); }
    }

    private sealed class GradientBus : IMiReadTransport, IMiWriteTransport
    {
        public readonly Dictionary<byte, byte[]> Values = new() { [15] = [1], [16] = [2], [17] = [255, 165, 0], [18] = [3] };
        public int Reads, Writes;
        public bool Unavailable, FailWrites;
        public Task<byte[]?> ReadAsync(MiReadBinding binding, CancellationToken token)
        {
            Reads++;
            byte[] result = new byte[32]; Values[binding.MethodName].CopyTo(result, 4);
            return Task.FromResult<byte[]?>(Unavailable ? null : result);
        }
        public Task<byte[]?> WriteAsync(VerifiedWmiBinding binding, ReadOnlyMemory<byte> request, CancellationToken token)
        {
            Writes++; Values[binding.MethodName] = request.Slice(4, binding.MethodName == 17 ? 3 : 1).ToArray();
            return Task.FromResult<byte[]?>(FailWrites ? null : []);
        }
    }
}
