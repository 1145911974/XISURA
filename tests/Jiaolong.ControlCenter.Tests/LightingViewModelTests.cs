using Jiaolong.Contracts.Errors;
using Jiaolong.Contracts.Models;
using Jiaolong_ControlCenter.ViewModels;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class LightingViewModelTests
{
    [TestMethod]
    public void Lid_logo_exposes_only_on_off()
    {
        var properties = typeof(LightingDraft).GetProperties().Select(x => x.Name).ToArray();

        CollectionAssert.Contains(properties, nameof(LightingDraft.LidLogoEnabled));
        Assert.IsFalse(properties.Any(x => x.Contains("LidLogoBrightness") || x.Contains("LidLogoColor") || x.Contains("LidLogoZone")));
    }

    [TestMethod]
    public void Quick_grid_has_exact_order_and_source_labels()
    {
        var items = QuickSettingsFactory.CreateDefault();

        CollectionAssert.AreEqual(
            new[] { QuickSettingKind.Fn, QuickSettingKind.Touchpad, QuickSettingKind.NumLock, QuickSettingKind.CapsLock, QuickSettingKind.LidLogo, QuickSettingKind.Wifi },
            items.Select(x => x.Kind).ToArray());
        Assert.IsTrue(items.All(x => !string.IsNullOrWhiteSpace(x.Source.ToDisplayName())));
    }

    [TestMethod]
    public async Task Unsupported_effect_is_not_sent_even_if_forged_in_ui_state()
    {
        var client = new RecordingControlCenterClient();
        var result = await LightingFixture.Create(client, [LightingEffect.Fixed])
            .ApplyAsync(LightingFixture.DraftWith(LightingEffect.Rainbow), RiskAcknowledgement.Accepted, CancellationToken.None);

        Assert.AreEqual(ErrorCode.CapabilityUnavailable, result.Error?.Code);
        Assert.AreEqual(0, client.SentCommands.Count);
    }
}

internal static class LightingFixture
{
    public static LightingViewModel Create(RecordingControlCenterClient client, IEnumerable<LightingEffect> supportedEffects) =>
        new(client, supportedEffects);

    public static LightingDraft DraftWith(LightingEffect effect) =>
        new(true, KeyboardBrightness.Medium, new RgbColor(255, 255, 255), effect, false);
}
