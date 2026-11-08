using GdUnit4;
using Refii.AndroidRuntime;
using Refii.Core;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>A phone's quit rows say they close the game; the desktop's keep their words.</summary>
[TestSuite]
public sealed class AndroidMenuWordsTests
{
    [TestCase]
    public void TestAPhoneQuitsTheGameNotToADesktop()
    {
        Engine_Title f2 = Engine_Title.Fable2;
        AssertThat(AndroidMenuWords.ForPhone("Quit to Desktop", true, f2)).IsEqual("Quit Game");
        AssertThat(AndroidMenuWords.ForPhone("Save and Quit to Desktop", true, f2)).IsEqual("Save and Quit Game");
        AssertThat(AndroidMenuWords.ForPhone("Quit to Desktop and Don't Save", true, f2)).IsEqual("Quit Game and Don't Save");
        AssertThat(AndroidMenuWords.ForPhone("Quit to Desktop", false, f2)).IsEqual("Quit to Desktop");
        AssertThat(AndroidMenuWords.ForPhone("Quit to Title", true, f2)).IsEqual("Quit to Title");
    }

    [TestCase]
    public void TestFable3KeepsItsOwnLabelsOnAPhone()
    {
        AssertThat(AndroidMenuWords.ForPhone("Quit to Desktop", true, Engine_Title.Fable3)).IsEqual("Quit to Desktop");
        AssertThat(Engine_Title.Fable3.PhoneHostLabels.Count).IsEqual(0);
    }
}
