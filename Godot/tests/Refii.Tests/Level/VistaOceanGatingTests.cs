namespace Refii.Tests.Level;

using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Level;

[TestSuite]
public class VistaOceanGatingTests
{
    [TestCase("albion/bwsmarket", "bwsmarket", false)]
    [TestCase("albion/bwsslums", "bwsslums", false)]
    [TestCase("albion/bwscemetary", "bwscemetary", false)]
    [TestCase("albion/fairfaxcastlegardens", "fairfaxcastlegardens", false)]
    [TestCase("albion/brightwood", "brightwood", false)]
    [TestCase("albion/bowerlake", "bowerlake", false)]
    [TestCase("albion/crucible", "crucible", false)]
    [TestCase("albion/dreamworld", "dreamworld", false)]
    [TestCase("albion/chamberofseasons", "chamberofseasons", false)]
    [TestCase("albion/wintershrine", "wintershrine", false)]
    [TestCase("albion/dlc2/dlc2_colosseum", "dlc2_colosseum", false)]
    [TestCase("albion/caves/bowerlake/thagscave", "thagscave", false)]
    [TestCase("albion/tombs/wraithmarsh/autumnshrine", "autumnshrine", false)]
    [TestCase("albion/demondoors/brightwooddd", "brightwooddd", false)]
    [TestCase("albion/caves/dunecrest/waterfallcave", "waterfallcave", false)]
    public void TestInlandAndUndergroundLevelsDoNotAllowVistaOcean(string levelPath, string name, bool expected)
    {
        var entry = new LevelEntry
        {
            World = "albion",
            LevelPath = levelPath,
            Name = name,
            Group = name,
        };

        bool allowed = Engine_LevelScene.AllowsVistaOcean(entry);
        AssertThat(allowed).IsEqual(expected);
    }

    [TestCase("albion/bloodstone", "bloodstone", true)]
    [TestCase("albion/reaver beach (bloodtsone)", "reaver beach", true)]
    [TestCase("albion/ravenscar", "ravenscar", true)]
    [TestCase("albion/tatteredspire", "tatteredspire", true)]
    [TestCase("albion/mysteryisland", "mysteryisland", true)]
    [TestCase("albion/dunecrestnew", "dunecrestnew", true)]
    [TestCase("albion/westcliff", "westcliff", true)]
    [TestCase("albion/caves/westcliff/westcliffexterior", "westcliffexterior", true)]
    [TestCase("albion/caves/bloodstone/bloodstone_assault", "bloodstone_assault", true)]
    [TestCase("albion/wraithmarsh", "wraithmarsh", true)]
    public void TestCoastalMaritimeLevelsAllowVistaOcean(string levelPath, string name, bool expected)
    {
        var entry = new LevelEntry
        {
            World = "albion",
            LevelPath = levelPath,
            Name = name,
            Group = name,
        };

        bool allowed = Engine_LevelScene.AllowsVistaOcean(entry);
        AssertThat(allowed).IsEqual(expected);
    }
}
