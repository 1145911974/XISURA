using Jiaolong_ControlCenter.Prototype;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class PerformanceRadarProfileTests
{
    [TestMethod]
    public void Turbo_tiers_and_custom_profiles_resolve_distinct_five_axis_values()
    {
        var normal = PerformanceRadarProfile.ForMode(PrototypePerformanceMode.Turbo, "Normal", null);
        var quiet = PerformanceRadarProfile.ForMode(PrototypePerformanceMode.Turbo, "Quiet", null);
        var extreme = PerformanceRadarProfile.ForMode(PrototypePerformanceMode.Turbo, "Extreme", null);

        CollectionAssert.AreEqual(new[] { 88, 92, 86, 94, 28 }, normal.Values.ToArray());
        CollectionAssert.AreEqual(new[] { 76, 82, 58, 80, 68 }, quiet.Values.ToArray());
        CollectionAssert.AreEqual(new[] { 100, 100, 100, 100, 12 }, extreme.Values.ToArray());

        var customProfiles = new[] { "Profile1", "Profile2", "Profile3" }
            .Select(profile => PerformanceRadarProfile.ForMode(PrototypePerformanceMode.Custom, null, profile))
            .Distinct()
            .Count();
        Assert.AreEqual(3, customProfiles);
    }

    [TestMethod]
    public void Injected_values_are_normalized_to_the_zero_to_one_hundred_contract()
    {
        var profile = new PerformanceRadarProfile(-1, 101, 50, 200, 0).Normalized();

        CollectionAssert.AreEqual(new[] { 0, 100, 50, 100, 0 }, profile.Values.ToArray());
    }
}
