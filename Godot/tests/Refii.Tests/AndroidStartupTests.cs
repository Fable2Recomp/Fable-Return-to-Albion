namespace Refii.Tests;

using GdUnit4;
using static GdUnit4.Assertions;
using Refii.AndroidRuntime;

[TestSuite]
public class AndroidStartupTests
{
    private const string Microsoft = "microsoft_logo.bik", Lionhead = "lionhead_logo.bik";

    /// <summary>Skip intro videos is off unless the player turns it on, and a phone's player sees it without Advanced.</summary>
    [TestCase]
    public void TestSkipIntroVideosIsAnOffByDefaultPhoneOption()
    {
        AssertThat(AndroidStartup.SkipsVideos(new System.Collections.Generic.Dictionary<string, string>())).IsFalse();
        AssertThat(AndroidStartup.SkipsVideos(new System.Collections.Generic.Dictionary<string, string> { [AndroidStartup.SkipVideosKey] = "true" })).IsTrue();
        Refii.Launcher.Core.GraphicsPresets.Offered = true;
        try
        {
            AssertThat(AndroidLauncherSettings.Shows(AndroidStartup.SkipVideosKey)).IsTrue();
            AssertThat(AndroidLauncherSettings.Advanced(AndroidStartup.SkipVideosKey)).IsEqual(false);
            Refii.Launcher.Core.GraphicsPresets.Offered = false;
            AssertThat(AndroidLauncherSettings.Shows(AndroidStartup.SkipVideosKey)).IsFalse();
        }
        finally { Refii.Launcher.Core.GraphicsPresets.Offered = Refii.Core.Engine_Platform.IsAndroid; }
    }

    [TestCase]
    public void TestAFirstStartLearnsTheGuestsOrder()
    {
        AssertThat(AndroidStartup.Merge(new string[0], new[] { Microsoft, Lionhead })).ContainsExactly(Microsoft, Lionhead);
    }

    [TestCase]
    public void TestAVideoPlayedAheadAndMissedStaysLearned()
    {
        // The guest's request for the first was retired before the player saw it.
        AssertThat(AndroidStartup.Merge(new[] { Microsoft, Lionhead }, new[] { Lionhead })).ContainsExactly(Microsoft, Lionhead);
        AssertThat(AndroidStartup.Merge(new[] { Microsoft, Lionhead }, new string[0])).ContainsExactly(Microsoft, Lionhead);
    }

    [TestCase]
    public void TestANewVideoTakesItsPlaceInTheGuestsOrder()
    {
        AssertThat(AndroidStartup.Merge(new[] { Lionhead }, new[] { Microsoft, Lionhead })).ContainsExactly(Microsoft, Lionhead);
        AssertThat(AndroidStartup.Merge(new[] { Microsoft, Lionhead }, new[] { "middlewarelogos.bik", Microsoft }))
            .ContainsExactly("middlewarelogos.bik", Microsoft, Lionhead);
    }
}
