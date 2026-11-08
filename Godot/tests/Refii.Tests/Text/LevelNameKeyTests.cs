namespace Refii.Tests.Text;

using GdUnit4;
using static GdUnit4.Assertions;

[TestSuite]
public class LevelNameKeyTests
{
    [TestCase]
    public void SeparatorsAndBracketsBecomeUnderscores()
    {
        AssertThat(Refii.Text.Engine_LevelNames.Normalise(@"Caves\GargoylesCave")).IsEqual("caves_gargoylescave");
        AssertThat(Refii.Text.Engine_LevelNames.Normalise(@"Tombs\BWSMarket\Nightmare Hollow")).IsEqual("tombs_bwsmarket_nightmare_hollow");
        AssertThat(Refii.Text.Engine_LevelNames.Normalise("A/B (C)")).IsEqual("a_b__c_");
    }

    /// <summary>The guest replaces singles before doubles (TU1 0x8231D000 before 0x8231D044), so a doubled separator is two underscores.</summary>
    [TestCase]
    public void ADoubledSeparatorIsTwoUnderscores()
    {
        AssertThat(Refii.Text.Engine_LevelNames.Normalise("a//b")).IsEqual("a__b");
        AssertThat(Refii.Text.Engine_LevelNames.Normalise(@"a\\b")).IsEqual("a__b");
    }
}
