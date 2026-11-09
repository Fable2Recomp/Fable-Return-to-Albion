using System.Collections.Generic;
using GdUnit4;
using Refii.AndroidRuntime;
using Refii.Core;
using Refii.Launcher.Core;
using Refii.Lighting;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>The launcher's Baked lamp shadows option: when Play bakes, and who is offered it.</summary>
[TestSuite]
public sealed class AndroidBakedShadowsTests
{
    [AfterTest]
    public void BackToThisPlatform() => GraphicsPresets.Offered = Engine_Platform.IsAndroid;

    /// <summary>Off unless the configuration turns it on.</summary>
    [TestCase]
    public void OptionIsOffByDefault()
    {
        AssertThat(AndroidBakedShadows.Wanted(new Dictionary<string, string>())).IsFalse();
        AssertThat(AndroidBakedShadows.Wanted(new Dictionary<string, string> { [AndroidBakedShadows.Key] = "true" })).IsTrue();
        AssertThat(AndroidBakedShadows.Wanted(new Dictionary<string, string> { [AndroidBakedShadows.Key] = "false" })).IsFalse();
    }

    /// <summary>Play bakes Fable 2 with the option on, once per launcher session.</summary>
    [TestCase]
    public void PlayBakesOnlyWhenItHasTo()
    {
        AssertThat(AndroidBakedShadows.ShouldBake(true, Engine_Title.Fable2, false)).IsTrue();
        AssertThat(AndroidBakedShadows.ShouldBake(false, Engine_Title.Fable2, false)).IsFalse();
        AssertThat(AndroidBakedShadows.ShouldBake(true, Engine_Title.Fable2, true)).IsFalse();
        AssertThat(AndroidBakedShadows.ShouldBake(true, Engine_Title.Fable3, false)).IsFalse();
    }

    /// <summary>Areas already baked read as a check; only real baking reports a count.</summary>
    [TestCase]
    public void ProgressLineSaysWhatIsHappening()
    {
        AssertThat(AndroidBakedShadows.Line(new Engine_LevelShadowPrebuild.Progress(0, 0, 0, 0, 0, "")))
            .IsEqual("Checking baked lamp shadows...");
        AssertThat(AndroidBakedShadows.Line(new Engine_LevelShadowPrebuild.Progress(40, 143, 0, 40, 0, "")))
            .IsEqual("Checking baked lamp shadows...");
        AssertThat(AndroidBakedShadows.Line(new Engine_LevelShadowPrebuild.Progress(41, 143, 1, 40, 0, "")))
            .StartsWith("Baking lamp shadows: 41 of 143 areas.");
    }

    /// <summary>The launcher's list leaves the Lighting category to the desktop's dialog, so a row there never appears on a phone.</summary>
    [TestCase]
    public void NotFiledWhereThePhoneCannotSeeIt() =>
        AssertThat(RuntimeCVarsAndroid.BakedShadows[0].Category).IsNotEqual(RuntimeCVarsGodot.LightingCategory);

    /// <summary>A phone's player sees it without Advanced; the desktop, which always bakes, is not offered it.</summary>
    [TestCase]
    public void OfferedOnlyOnAPhone()
    {
        GraphicsPresets.Offered = true;
        AssertThat(AndroidLauncherSettings.Shows(AndroidBakedShadows.Key)).IsTrue();
        AssertThat(AndroidLauncherSettings.Advanced(AndroidBakedShadows.Key)).IsEqual(false);
        GraphicsPresets.Offered = false;
        AssertThat(AndroidLauncherSettings.Shows(AndroidBakedShadows.Key)).IsFalse();
        AssertThat(AndroidLauncherSettings.Shows("msaa")).IsTrue();
    }
}
