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

    /// <summary>Off unless the configuration picks a bake; the earlier on/off switch's true and false keep their meaning.</summary>
    [TestCase]
    public void ChoiceIsOffByDefaultAndReadsTheOldSwitch()
    {
        static AndroidBakedShadows.Mode Read(string value) =>
            AndroidBakedShadows.Chosen(new Dictionary<string, string> { [AndroidBakedShadows.Key] = value });
        AssertThat(AndroidBakedShadows.Chosen(new Dictionary<string, string>())).IsEqual(AndroidBakedShadows.Mode.Off);
        AssertThat(Read("true")).IsEqual(AndroidBakedShadows.Mode.BeforePlaying);
        AssertThat(Read("\"true\"")).IsEqual(AndroidBakedShadows.Mode.BeforePlaying);
        AssertThat(Read("1")).IsEqual(AndroidBakedShadows.Mode.BeforePlaying);
        AssertThat(Read("false")).IsEqual(AndroidBakedShadows.Mode.Off);
        AssertThat(Read("\"while_playing\"")).IsEqual(AndroidBakedShadows.Mode.WhilePlaying);
        AssertThat(Read("something else")).IsEqual(AndroidBakedShadows.Mode.Off);
    }

    /// <summary>Only Before playing runs the sweep at Play; While playing and Off go straight to the game.</summary>
    [TestCase]
    public void OnlyBeforePlayingSweepsAtPlay()
    {
        static bool Sweeps(string value) =>
            AndroidBakedShadows.BakesBeforePlaying(new Dictionary<string, string> { [AndroidBakedShadows.Key] = value });
        AssertThat(Sweeps(AndroidBakedShadows.BeforePlayingValue)).IsTrue();
        AssertThat(Sweeps(AndroidBakedShadows.WhilePlayingValue)).IsFalse();
        AssertThat(Sweeps(AndroidBakedShadows.OffValue)).IsFalse();
    }

    /// <summary>The launcher offers the three choices, each stored value reading back as its own mode, off by default.</summary>
    [TestCase]
    public void LauncherOffersTheThreeChoices()
    {
        RuntimeCVarDefinition definition = RuntimeCVarsAndroid.BakedShadows[0];
        AssertThat(definition.DefaultValue).IsEqual(AndroidBakedShadows.OffValue);
        List<RuntimeCVarOption> options = RuntimeSettingsModel.ParseAllowedValues(definition.AllowedValues);
        AssertThat(options.Count).IsEqual(3);
        AssertThat(AndroidBakedShadows.Parse(options[0].Value)).IsEqual(AndroidBakedShadows.Mode.BeforePlaying);
        AssertThat(AndroidBakedShadows.Parse(options[1].Value)).IsEqual(AndroidBakedShadows.Mode.WhilePlaying);
        AssertThat(AndroidBakedShadows.Parse(options[2].Value)).IsEqual(AndroidBakedShadows.Mode.Off);
        AssertThat(options[0].Label).IsEqual("Before playing");
        AssertThat(options[1].Label).IsEqual("While playing");
        AssertThat(options[2].Label).IsEqual("Off");
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

    /// <summary>The phone's bake hooks leave the desktop's bake as it was: whole AO strips in one job, its own batch of
    /// lamps, and ground AO baked during play.</summary>
    [TestCase]
    public void DesktopBakeIsUnchanged()
    {
        if (Engine_Platform.IsAndroid) return;
        AssertThat(AndroidBakedShadows.InGameBakesAo).IsTrue();
        AssertThat(AndroidBakedShadows.AoTraceColumns(4096, 130)).IsEqual(4096);
        AssertThat(AndroidBakedShadows.AoStripRows(4096, 128)).IsEqual(128);
        AssertThat(AndroidBakedShadows.ShadowBatchLights(12)).IsEqual(12L);
    }

    /// <summary>A phone's AO pieces are whole 8-wide shader groups of about <see cref="AndroidBakedShadows.PhoneAoTraceTexels"/>
    /// texels, at least one group.</summary>
    [TestCase]
    public void PhonePiecesAreWholeGroups()
    {
        static int Columns(int width, int rows) => AndroidBakedShadows.PhoneAoTraceColumns(width, rows);
        AssertThat(Columns(4096, 6)).IsEqual(680);
        AssertThat(Columns(4096, 6) % 8).IsEqual(0);
        AssertThat(Columns(4096, 3) * 3).IsLessEqual(AndroidBakedShadows.PhoneAoTraceTexels);
        AssertThat(Columns(256, 3)).IsEqual(256);
        AssertThat(Columns(4096, 10000)).IsEqual(8);
    }

    /// <summary>On a phone the row says what the chosen bake costs, a different line for each choice; other rows and
    /// the desktop get none.</summary>
    [TestCase]
    public void PhoneRowExplainsTheChoice()
    {
        GraphicsPresets.Offered = true;
        string before = AndroidSettingNotes.For(AndroidBakedShadows.Key, AndroidBakedShadows.BeforePlayingValue)!;
        string during = AndroidSettingNotes.For(AndroidBakedShadows.Key, "\"while_playing\"")!;
        string off = AndroidSettingNotes.For(AndroidBakedShadows.Key, AndroidBakedShadows.OffValue)!;
        AssertThat(before).Contains("hours");
        AssertThat(during).Contains("stutter");
        AssertThat(off).Contains("no space");
        AssertThat(AndroidSettingNotes.For("msaa", "2")).IsNull();
        GraphicsPresets.Offered = false;
        AssertThat(AndroidSettingNotes.For(AndroidBakedShadows.Key, AndroidBakedShadows.WhilePlayingValue)).IsNull();
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
