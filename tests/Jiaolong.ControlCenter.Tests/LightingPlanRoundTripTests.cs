using System.Text.Json;
using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.Services;
using Jiaolong.Contracts.Protocol;
using Jiaolong_ControlCenter.Prototype.Controls;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class LightingPlanRoundTripTests
{
    [TestMethod]
    public void Preview_and_service_share_color_rules_and_protocol_preserves_user_settings()
    {
        foreach (var effect in new[] { "Static", "Gradient", "Cycle" })
        {
            var draft = new LightingDraft(effect, 2, "#AD3F8C", false) { Speed = 1.5 };
            HardwareCommand command = new SetKeyboardLightingCommand(Guid.NewGuid(), draft.ToPlan()) { Preview = true };
            string json = JsonSerializer.Serialize(command, ProtocolJsonContext.Default.HardwareCommand);
            var restored = (SetKeyboardLightingCommand)JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.HardwareCommand)!;
            Assert.AreEqual(draft.ToPlan(), restored.Plan);
            Assert.IsTrue(restored.Preview);
            foreach (double time in new[] { 0d, 2.7, 8.9, 18 })
            {
                var preview = draft.PreviewColor(time);
                var actual = restored.Plan.ColorAt(time);
                Assert.AreEqual((preview.R, preview.G, preview.B), (actual.Red, actual.Green, actual.Blue));
            }
        }
        var slots = new Dictionary<string, int> { ["Gaming"] = 3, ["Office"] = 9, ["Custom2"] = 2, ["Turbo"] = 3 };
        var controls = new HomeControlState(PerformanceMode.Balanced, false, []);
        Assert.AreEqual(PresetKey.Create(ControlModeId.Gaming, 3), LightingPresetPolicy.ResolveTarget(controls, slots, null));
        Assert.AreEqual(PresetKey.Create(ControlModeId.Custom3, 2), LightingPresetPolicy.ResolveTarget(
            controls with { PerformanceMode = PerformanceMode.Turbo }, slots, null, PresetKey.Create(ControlModeId.Custom3, 1)));
        Assert.AreEqual(PresetKey.Create(ControlModeId.Office, 2), LightingPresetPolicy.ResolveTarget(controls with { PerformanceMode = PerformanceMode.Quiet }, slots, null));
        Assert.IsNull(LightingPresetPolicy.ResolveTarget(controls with { PerformanceMode = null }, slots, null));
        Assert.IsNull(LightingPresetPolicy.ResolveTarget(controls with { PerformanceMode = PerformanceMode.Custom }, slots, null));
        Assert.AreEqual(PresetKey.Create(ControlModeId.Custom2, 2), LightingPresetPolicy.ResolveTarget(controls with { PerformanceMode = PerformanceMode.Custom }, slots, PresetKey.Create(ControlModeId.Custom2, 2)));
        var automatic = new AdaptiveAutomationStatus(true, true, PresetKey.Create(ControlModeId.Turbo, 1), null, null, null, null, null);
        Assert.AreEqual(PresetKey.Create(ControlModeId.Turbo, 3), LightingPresetPolicy.ResolveTarget(controls with { AdaptiveAutomation = automatic }, slots, null));
        Assert.AreEqual(PresetKey.Create(ControlModeId.Gaming, 3), LightingPresetPolicy.ResolveTarget(controls, slots, null, PresetKey.Create(ControlModeId.Gaming, 2)));
        Assert.AreEqual(PresetKey.Create(ControlModeId.Turbo, 3), LightingPresetPolicy.ResolveTarget(controls with { AdaptiveAutomation = automatic }, slots, null, PresetKey.Create(ControlModeId.Gaming, 1)));
        Assert.AreEqual(PresetKey.Create(ControlModeId.Custom2, 3), LightingPresetPolicy.ResolveTarget(controls with { PerformanceMode = PerformanceMode.Custom }, new Dictionary<string, int> { ["Custom2"] = 3 }, PresetKey.Create(ControlModeId.Custom2, 1)));
        Assert.AreEqual(PresetKey.Create(ControlModeId.Gaming, 2), LightingPresetPolicy.ResolveTarget(controls, new Dictionary<string, int> { ["Gaming"] = 9 }, null));
        Assert.AreEqual(PresetKey.Create(ControlModeId.Gaming, 3), LightingPresetPolicy.ResolveTarget(controls with { AdaptiveAutomation = automatic with { Running = false } }, slots, null));
        var expected = new LightingDraft("Static", 2, "#AD3F8C", true).ToPlan();
        Assert.IsTrue(LightingPresetPolicy.SameEffect(expected, expected with { Effect = "Fixed", LogoEnabled = false, Speed = 2 }));
        Assert.IsFalse(LightingPresetPolicy.SameEffect(expected, expected with { Red = 0 }));
        Assert.IsFalse(LightingPresetPolicy.SameEffect(expected, null));
        Assert.IsTrue(LightingPresetPolicy.SameEffect(expected with { Effect = "Cycle" }, expected with { Effect = "Cycle", Red = 0, Speed = 2 }));
        Assert.IsFalse(LightingPresetPolicy.SameEffect(expected with { Effect = "Gradient" }, expected with { Effect = "Gradient", Speed = 2 }));
        Assert.IsTrue(LightingPresetPolicy.SameEffect(expected with { Effect = "Cycle", BrightnessLevel = 0 }, expected with { Effect = "Fixed", BrightnessLevel = 0 }));
        HardwareCommand restore = new RestoreKeyboardLightingPreviewCommand(Guid.NewGuid());
        string restoreJson = JsonSerializer.Serialize(restore, ProtocolJsonContext.Default.HardwareCommand);
        Assert.IsInstanceOfType<RestoreKeyboardLightingPreviewCommand>(JsonSerializer.Deserialize(restoreJson, ProtocolJsonContext.Default.HardwareCommand));
    }
}
