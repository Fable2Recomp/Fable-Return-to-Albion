using GdUnit4;
using Refii.AndroidRuntime;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>A phone's quit rows say they close the game; the desktop's keep their words.</summary>
[TestSuite]
public sealed class AndroidMenuWordsTests
{
    [TestCase]
    public void TestAPhoneQuitsTheGameNotToADesktop()
    {
        AssertThat(AndroidMenuWords.ForPhone("Quit to Desktop", true)).IsEqual("Quit Game");
        AssertThat(AndroidMenuWords.ForPhone("Save and Quit to Desktop", true)).IsEqual("Save and Quit Game");
        AssertThat(AndroidMenuWords.ForPhone("Quit to Desktop and Don't Save", true)).IsEqual("Quit Game and Don't Save");
        AssertThat(AndroidMenuWords.ForPhone("Quit to Desktop", false)).IsEqual("Quit to Desktop");
        AssertThat(AndroidMenuWords.ForPhone("Quit to Title", true)).IsEqual("Quit to Title");
    }
}
