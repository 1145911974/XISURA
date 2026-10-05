using Jiaolong_ControlCenter.Styles;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class DesignTokenTests
{
    [TestMethod]
    public void Text_and_state_token_pairs_meet_required_contrast()
    {
        Assert.IsTrue(DesignTokenFixture.TextPairs.All(x => x.ContrastRatio >= 4.5));
        Assert.IsTrue(DesignTokenFixture.NonTextPairs.All(x => x.ContrastRatio >= 3.0));
    }
}
