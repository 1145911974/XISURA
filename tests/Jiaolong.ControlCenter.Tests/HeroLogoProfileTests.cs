using Jiaolong_ControlCenter.Branding;

namespace Jiaolong.ControlCenter.Tests;

[TestClass]
public sealed class HeroLogoProfileTests
{
    [TestMethod]
    public void Default_profile_matches_the_approved_home_slot()
    {
        Assert.AreEqual(new HeroLogoProfile(124.5, 70, 360, 1, 1, 420), HeroLogoProfile.Default);
        Assert.AreEqual(0, HeroLogoProfile.Default.Validate().Count);
    }

    [TestMethod]
    public void Invalid_profile_falls_back_without_hiding_the_field_error()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, "{\"left\":114,\"top\":70,\"size\":0,\"crystalOpacity\":1,\"reflectionOpacity\":1,\"transitionMilliseconds\":420}");
        var result = HeroLogoProfileStore.LoadOrDefault(path);
        Assert.AreEqual(HeroLogoProfile.Default, result.Profile);
        StringAssert.Contains(result.Error, "size");
    }

    [TestMethod]
    public void Atomic_save_round_trips_a_valid_profile()
    {
        var directory = Directory.CreateTempSubdirectory();
        var path = Path.Combine(directory.FullName, "HeroLogoProfile.json");
        var expected = HeroLogoProfile.Default with { Left = 115, ReflectionOpacity = 0.8 };
        HeroLogoProfileStore.SaveAtomic(path, expected);
        Assert.AreEqual(expected, HeroLogoProfileStore.LoadOrDefault(path).Profile);
        Assert.AreEqual(0, directory.GetFiles("*.tmp").Length);
    }

    [TestMethod]
    public void Missing_json_field_falls_back_with_a_field_diagnostic()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, "{\"left\":114,\"top\":70,\"size\":360,\"reflectionOpacity\":1,\"transitionMilliseconds\":420}");
        var result = HeroLogoProfileStore.LoadOrDefault(path);
        Assert.AreEqual(HeroLogoProfile.Default, result.Profile);
        StringAssert.Contains(result.Error, "crystalOpacity");
    }

    [TestMethod]
    public void Non_finite_values_are_rejected()
    {
        var profile = HeroLogoProfile.Default with { Left = double.NaN };
        StringAssert.Contains(string.Join("; ", profile.Validate()), "left");
        Assert.Throws<ArgumentException>(() => HeroLogoProfileStore.SaveAtomic(Path.GetTempFileName(), profile));
    }
}
