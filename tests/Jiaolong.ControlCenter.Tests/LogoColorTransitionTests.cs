using Jiaolong_ControlCenter.Branding;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class LogoColorTransitionTests
{
    [TestMethod]
    public void Blend_preserves_coverage_and_retargets_without_a_jump()
    {
        var blend = new LogoColorTransition();
        blend.Select(1, 0, .42);
        blend.Advance(.21);
        Assert.AreEqual(1d, blend.Weights.Sum(), 1e-10);
        Assert.IsTrue(blend.Weights[0] > 0 && blend.Weights[1] > 0);
        AssertSourceOverMatchesWeights(blend);
        var before = blend.Weights.ToArray();
        blend.Select(2, .25, .42, advance: false);
        CollectionAssert.AreEqual(before, blend.Weights);
        blend.Advance(.46);
        Assert.AreEqual(1d, blend.Weights.Sum(), 1e-10);
        AssertSourceOverMatchesWeights(blend);
        blend.Advance(.7);
        Assert.AreEqual(1d, blend.Weights[2]);
        Assert.IsFalse(blend.IsActive);
        AssertSourceOverMatchesWeights(blend);
    }

    [TestMethod]
    public void Duplicate_target_does_not_restart_and_zero_duration_snaps()
    {
        var blend = new LogoColorTransition();
        blend.Select(1, 0, .4);
        blend.Select(1, .2, .4);
        blend.Advance(.4);
        Assert.IsFalse(blend.IsActive);
        blend.Select(3, .4, 0);
        Assert.AreEqual(1d, blend.Weights[3]);
        Assert.AreEqual(1d, blend.Weights.Sum());
        AssertSourceOverMatchesWeights(blend);
    }

    private static void AssertSourceOverMatchesWeights(LogoColorTransition blend)
    {
        var opacities = new double[6];
        blend.WriteSourceOverOpacities(opacities);
        double coverage = 0;
        double[] effective = new double[6];
        for (int i = 0; i < effective.Length; i++)
        {
            for (int underneath = 0; underneath < i; underneath++)
                effective[underneath] *= 1 - opacities[i];
            effective[i] = opacities[i];
            coverage = opacities[i] + coverage * (1 - opacities[i]);
        }
        Assert.AreEqual(1d, coverage, 1e-10);
        for (int i = 0; i < effective.Length; i++)
            Assert.AreEqual(blend.Weights[i], effective[i], 1e-10);
    }
}
