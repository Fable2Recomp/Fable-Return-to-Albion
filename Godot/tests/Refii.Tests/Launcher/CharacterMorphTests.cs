using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GdUnit4;
using Refii.Hero;
using Refii.Launcher.Core;
using Refii.Ui;
using static GdUnit4.Assertions;

namespace Refii.Tests.Launcher;

[TestSuite]
public class CharacterMorphTests
{
    [TestCase]
    public void DefaultsRetainAutomaticMorphsAndNeutralManualAppearance()
    {
        AssertThat(Engine_CharacterMorphs.Automatic(_ => "")).IsTrue();
        foreach (string off in new[] { "false", "FALSE", "0", "off", "\"false\"" })
            AssertThat(Engine_CharacterMorphs.Automatic(_ => off)).IsFalse();
        foreach (string on in new[] { "true", "1", "on", "invalid" })
            AssertThat(Engine_CharacterMorphs.Automatic(_ => on)).IsTrue();
        var stats = Engine_CharacterMorphs.Read(_ => "");
        for (uint type = 2; type <= 18; type++) AssertThat(Engine_HeroMorphState.Weight(stats, type)).IsEqual(0f);
        AssertThat(Engine_CharacterMorphs.Sliders.Select(s => s.Input).Distinct().Count()).IsEqual(9);
    }

    [TestCase]
    public void ManualRangesClampAndRejectInvalidNumbers()
    {
        foreach (string invalid in new[] { "NaN", "Infinity", "-Infinity", "bad", "1e99" })
        {
            var stats = Engine_CharacterMorphs.Read(_ => invalid);
            AssertThat(stats.Age).IsEqual(30f);
            AssertThat(stats.Morality).IsEqual(0.5f);
            AssertThat(stats.Strength).IsEqual(0f);
        }
        var high = Engine_CharacterMorphs.Read(_ => "999");
        var low = Engine_CharacterMorphs.Read(_ => "-999");
        foreach (var slider in Engine_CharacterMorphs.Sliders)
        {
            AssertThat(high[slider.Input]).IsEqual(slider.Maximum);
            AssertThat(low[slider.Input]).IsEqual(slider.Minimum);
            AssertThat(Engine_CharacterMorphs.Value(slider, -100)).IsEqual(slider.Minimum);
            AssertThat(Engine_CharacterMorphs.Value(slider, 999)).IsEqual(slider.Maximum);
        }
    }

    [TestCase]
    public void ManualAppearanceRoundTripsThroughConfigWithoutChangingOtherSettings()
    {
        string file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid() + ".toml");
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var settings = Engine_CharacterMorphs.Values(row => row == 0 ? 0 : row + 2);
            var values = new SortedDictionary<string, string>(settings) { ["master_volume"] = "0.7" };
            AssertThat(RuntimeConfig.WriteValues(file, values, out _)).IsTrue();
            var restored = RuntimeConfig.ReadValues(file);
            string Read(string key) => restored.GetValueOrDefault(key, "");
            AssertThat(Engine_CharacterMorphs.Automatic(Read)).IsFalse();
            var stats = Engine_CharacterMorphs.Read(Read);
            for (int i = 0; i < Engine_CharacterMorphs.Sliders.Length; i++)
            {
                var slider = Engine_CharacterMorphs.Sliders[i];
                AssertThat(Math.Abs(stats[slider.Input] - Engine_CharacterMorphs.Value(slider, i + 3)) < 0.0001f).IsTrue();
            }
            AssertThat(restored["master_volume"]).IsEqual("0.7");
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
            System.IO.File.Delete(file);
        }
    }

    [TestCase]
    public void RuntimePublishesVisualWeightsThenCommitsTheGeneration()
    {
        var values = new Dictionary<string, string>
        {
            [Engine_CharacterMorphs.AutomaticKey] = "false",
            [Engine_CharacterMorphs.Key(HeroMorphInput.Strength)] = "0.8",
            [Engine_CharacterMorphs.Key(HeroMorphInput.Skill)] = "0.9",
            [Engine_CharacterMorphs.Key(HeroMorphInput.Will)] = "0.75",
            [Engine_CharacterMorphs.Key(HeroMorphInput.Morality)] = "0",
            [Engine_CharacterMorphs.Key(HeroMorphInput.Purity)] = "0",
        };
        var sent = new Dictionary<string, float>();
        string last = "";
        Engine_CharacterMorphs.Publish(key => values.GetValueOrDefault(key, ""),
            (key, value) => { sent[key] = value; last = key; }, 7);
        string prefix = Engine_CharacterMorphs.BridgePrefix;
        AssertThat(last).IsEqual(prefix + "generation");
        AssertThat(sent[last]).IsEqual(7f);
        AssertThat(sent[prefix + "automatic"]).IsEqual(0f);
        AssertThat(sent[prefix + "5"]).IsEqual(0.6f);
        AssertThat(sent[prefix + "7"]).IsEqual(0.75f);
        AssertThat(sent[prefix + "14"]).IsEqual(1f);
        values[Engine_CharacterMorphs.AutomaticKey] = "true";
        Engine_CharacterMorphs.Publish(key => values.GetValueOrDefault(key, ""), (key, value) => sent[key] = value, 8);
        AssertThat(sent[prefix + "automatic"]).IsEqual(1f);
        AssertThat(sent[prefix + "7"]).IsEqual(0.75f);
    }

    [TestCase]
    public void CharacterIsAMainSettingsEntryAndMorphsOpensTheHostPage()
    {
        var game = Engine_UiTemplates.ComposeGame();
        var options = game.Menus["OptionsMenu"];
        var character = options.Entries.Single(e => e.Name == "CharacterSettings");
        AssertThat(character.Tag).IsEqual("Character");
        AssertThat(character.HostOnly).IsTrue();
        var morphs = character.Children.Single();
        AssertThat(morphs.Tag).IsEqual("Morphs");
        AssertThat(morphs.Command).IsEqual(Engine_UiCharacterOptions.OpenCommand);
        AssertThat(Engine_UiHostOptionsPage.For(morphs.Command)).IsSame(Engine_UiCharacterOptions.Page);
    }

    [TestCase]
    public void FailedSaveKeepsManualEditsOpenAndCancelRestoresTheStoredMode()
    {
        var page = new Engine_UiCharacterOptions(_ => "", _ => false);
        Engine_UiOptionsPending.Leave();
        try
        {
            Engine_UiOptionsPending.EnterHost(page);
            Engine_UiOptionsPending.Track(Engine_UiScreenInput.Left);
            Engine_UiOptionsPending.Track(Engine_UiScreenInput.Down);
            Engine_UiOptionsPending.Track(Engine_UiScreenInput.Right);
            Engine_UiOptionsPending.Track(Engine_UiScreenInput.Select);
            AssertThat(Engine_UiHostOptionsPage.Current).IsSame(page);
            AssertThat(page.Count).IsEqual(10);
            AssertThat(Engine_UiOptionsPending.IndexOf(1)).IsEqual(1);
            Engine_UiOptionsPending.Track(Engine_UiScreenInput.Back);
            Engine_UiOptionsPending.EnterHost(page);
            AssertThat(page.Count).IsEqual(1);
            AssertThat(Engine_UiOptionsPending.IndexOf(1)).IsEqual(0);
        }
        finally { Engine_UiOptionsPending.Leave(); }
    }

    [TestCase]
    public void OptionsLifecycleHidesManualRowsUntilOffAndPreservesThemAcrossToggles()
    {
        var stored = new Dictionary<string, string>();
        int writes = 0;
        var page = new Engine_UiCharacterOptions(key => stored.GetValueOrDefault(key, ""), values =>
        {
            foreach (var pair in values) stored[pair.Key] = pair.Value;
            writes++;
            return true;
        });
        Engine_UiOptionsPending.Leave();
        try
        {
            Engine_UiOptionsPending.EnterHost(page);
            AssertThat(page.Count).IsEqual(1);
            Engine_UiOptionsPending.Track(Engine_UiScreenInput.Down);
            AssertThat(Engine_UiOptionsPending.Row).IsEqual(0);
            Engine_UiOptionsPending.Track(Engine_UiScreenInput.Left);
            AssertThat(page.Count).IsEqual(10);
            Engine_UiOptionsPending.Track(Engine_UiScreenInput.Down);
            Engine_UiOptionsPending.Track(Engine_UiScreenInput.Right);
            AssertThat(Engine_UiOptionsPending.IndexOf(1)).IsEqual(1);
            AssertThat(writes).IsEqual(0);
            var paint = page.Paint(Engine_UiOptionsPending.IndexOf, true, 9);
            AssertThat(paint.Values[Engine_UiHostOptionsPage.TitleField]).IsEqual("Character - Morphs (6-10/10)");
            Engine_UiOptionsPending.Track(Engine_UiScreenInput.Select);
            AssertThat(writes).IsEqual(1);
            AssertThat(stored[Engine_CharacterMorphs.AutomaticKey]).IsEqual("false");
            AssertThat(stored[Engine_CharacterMorphs.Key(HeroMorphInput.Strength)]).IsEqual("0.05");

            Engine_UiOptionsPending.EnterHost(page);
            Engine_UiOptionsPending.Track(Engine_UiScreenInput.Right);
            AssertThat(page.Count).IsEqual(1);
            Engine_UiOptionsPending.Track(Engine_UiScreenInput.Select);
            AssertThat(stored[Engine_CharacterMorphs.AutomaticKey]).IsEqual("true");
            Engine_UiOptionsPending.EnterHost(page);
            Engine_UiOptionsPending.Track(Engine_UiScreenInput.Left);
            AssertThat(Engine_UiOptionsPending.IndexOf(1)).IsEqual(1);
            Engine_UiOptionsPending.Track(Engine_UiScreenInput.Back);
            AssertThat(writes).IsEqual(2);
            Engine_UiOptionsPending.EnterHost(page);
            AssertThat(page.Count).IsEqual(1);
        }
        finally { Engine_UiOptionsPending.Leave(); }
    }
}
