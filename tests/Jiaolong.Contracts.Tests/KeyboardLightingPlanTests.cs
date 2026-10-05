using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;

namespace Jiaolong.Contracts.Tests;

[TestClass]
public sealed class KeyboardLightingPlanTests
{
    [TestMethod]
    public void Lighting_plan_rejects_partial_colors_and_invalid_speed_or_level()
    {
        var plan = new KeyboardLightingPlan("Gradient", 67) { Red = 0, Green = 215, Blue = 232, BrightnessLevel = 2, Speed = 1 };
        ServiceValidation(plan, true);
        ServiceValidation(plan with { Blue = null }, false);
        ServiceValidation(plan with { Speed = double.NaN }, false);
        ServiceValidation(plan with { BrightnessLevel = 4 }, false);
        ServiceValidation(plan with { Effect = "unsupported" }, false);
        ServiceValidation(new("Fixed", 50), true);
    }

    [TestMethod]
    public void Effect_starts_at_selected_color_and_cycles_at_selected_speed()
    {
        var plan = new KeyboardLightingPlan("Gradient", 100) { Red = 255, Green = 0, Blue = 0 };
        Assert.AreEqual(new KeyboardRgb(255, 0, 0), plan.ColorAt(0));
        Assert.AreEqual(plan.ColorAt(6), (plan with { Speed = 2 }).ColorAt(3));
        Assert.AreEqual(plan.ColorAt(0), plan.ColorAt(6));
        var cycle = plan with { Effect = "Cycle" };
        Assert.AreEqual(cycle.ColorAt(0), cycle.ColorAt(2));
        Assert.AreEqual(cycle.ColorAt(0), cycle.ColorAt(3)); // Native cycle timing is not fabricated in the client.
        Assert.AreEqual(new KeyboardRgb(255, 255, 0), plan.ColorAt(1));
        Assert.AreEqual(new KeyboardRgb(0, 255, 0), plan.ColorAt(2));
        Assert.AreEqual(new KeyboardRgb(0, 255, 255), plan.ColorAt(3));
        Assert.AreEqual(new KeyboardRgb(0, 0, 255), plan.ColorAt(4));
        Assert.AreEqual(new KeyboardRgb(255, 0, 255), plan.ColorAt(5));
        Assert.AreEqual(new KeyboardRgb(89, 0, 0), (plan with { Red = 0 }).ColorAt(0));
        Assert.AreEqual(new KeyboardRgb(128, 0, 0), (plan with { Red = 128, Green = 128, Blue = 128 }).ColorAt(0));
        Assert.AreEqual(plan.ColorAt(0), plan.ColorAt(double.NaN));
    }

    private static void ServiceValidation(KeyboardLightingPlan plan, bool valid) =>
        Assert.AreEqual(valid, CommandValidation.Validate(new SetKeyboardLightingCommand(Guid.NewGuid(), plan)) is null);
}
