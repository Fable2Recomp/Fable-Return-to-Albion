namespace Refii.Tests.Launcher;

using System.Collections.Generic;
using System.Linq;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.AndroidRuntime;
using Refii.Launcher.Core;

/// <summary>The presets as a phone offers them; <see cref="TestADesktopNeitherShowsNorWritesThePreset"/> is the desktop.</summary>
[TestSuite]
public class GraphicsPresetTests
{
    [BeforeTest]
    public void StandInForAPhone() => GraphicsPresets.Offered = true;

    [AfterTest]
    public void BackToThisPlatform() => GraphicsPresets.Offered = Refii.Core.Engine_Platform.IsAndroid;

    [TestCase]
    public void TestQualityPresetWritesItsValuesAndReadsBack()
    {
        var model = new RuntimeSettingsModel();
        model.ApplyGraphicsPreset("android");

        AssertThat(model.CurrentGraphicsPreset()).IsEqual("android");
        AssertThat(model.Values[GraphicsPresets.Key]).IsEqual("android");
        foreach ((string key, string value) in GraphicsPresets.QualityValues)
        {
            AssertThat(model.Values[key]).IsEqual(value);
        }
    }

    /// <summary>Both presets thin the distant trees: the launcher writes the setting's own default (off) otherwise.</summary>
    [TestCase]
    public void TestBothPresetsThinTheDistantTrees()
    {
        AssertThat(GraphicsPresets.QualityValues["tree_card_lod"]).IsEqual("true");
        AssertThat(GraphicsPresets.PerformanceValues["tree_card_lod"]).IsEqual("true");
    }

    /// <summary>The lighting dialog saves preset settings outside the panel's rows; a change there reads as custom too.</summary>
    [TestCase]
    public void TestAChangeOutsideThePanelRowsReadsAsCustom()
    {
        var model = new RuntimeSettingsModel();
        model.ApplyGraphicsPreset("android");
        model.Values["placed_light_limit"] = model.Values["placed_light_limit"] == "7" ? "8" : "7";
        model.RefreshGraphicsPreset();
        AssertThat(model.Values[GraphicsPresets.Key]).IsEqual("custom");

        model.ApplyGraphicsPreset("android");
        model.RefreshGraphicsPreset();
        AssertThat(model.Values[GraphicsPresets.Key]).IsEqual("android");
    }

    [TestCase]
    public void TestEverySettingAtItsDefaultIsCustom()
    {
        // The settings' own defaults are the desktop picture, which no phone preset is.
        var values = RuntimeCVarSet.All.ToDictionary(d => d.Name, d => d.DefaultValue);
        AssertThat(GraphicsPresets.Current(values)).IsEqual("custom");
        AssertThat(GraphicsPresets.Current(new Dictionary<string, string>())).IsEqual("custom");
    }

    [TestCase]
    public void TestChangingAPresetSettingReadsAsCustom()
    {
        var model = new RuntimeSettingsModel();
        model.ApplyGraphicsPreset("android");
        string key = GraphicsPresets.Members.First();
        string original = model.Values[key];

        model.Values[key] = original == "1234" ? "4321" : "1234";
        AssertThat(model.RefreshGraphicsPreset(key)).IsTrue();
        AssertThat(model.Values[GraphicsPresets.Key]).IsEqual("custom");

        model.Values[key] = original;
        model.RefreshGraphicsPreset(key);
        AssertThat(model.Values[GraphicsPresets.Key]).IsEqual("android");

        // A setting outside the preset leaves it alone.
        AssertThat(model.RefreshGraphicsPreset("camera_invert_y")).IsFalse();
    }

    [TestCase]
    public void TestNumbersMatchByValue()
    {
        var values = new Dictionary<string, string>(GraphicsPresets.QualityValues);
        string numeric = values.First(pair => double.TryParse(pair.Value,
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _)).Key;
        values[numeric] = double.Parse(values[numeric], System.Globalization.CultureInfo.InvariantCulture)
            .ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
        AssertThat(GraphicsPresets.Current(values)).IsEqual("android");
    }

    [TestCase]
    public void TestCustomAndUnknownNamesChangeNothing()
    {
        var values = new Dictionary<string, string>();
        AssertThat(GraphicsPresets.Apply("custom", values)).IsFalse();
        AssertThat(GraphicsPresets.Apply("default", values)).IsFalse();
        AssertThat(GraphicsPresets.Apply("ultra", values)).IsFalse();
        AssertThat(values.Count).IsEqual(0);
    }

    [TestCase]
    public void TestAPhoneGetsQualityOnceAndKeepsThePlayersChoice()
    {
        var fresh = new Dictionary<string, string> { ["render_scale_percent"] = "59" };
        AssertThat(AndroidLauncherSettings.ApplyPresetOnce(fresh)).IsTrue();
        AssertThat(fresh[GraphicsPresets.Key]).IsEqual("android");
        AssertThat(GraphicsPresets.Current(fresh)).IsEqual("android");

        // Chosen once already, whatever was chosen: nothing is rewritten.
        var chosen = new Dictionary<string, string> { [GraphicsPresets.Key] = "custom", ["render_scale_percent"] = "80" };
        AssertThat(AndroidLauncherSettings.ApplyPresetOnce(chosen)).IsFalse();
        AssertThat(chosen["render_scale_percent"]).IsEqual("80");
        AssertThat(chosen.Count).IsEqual(2);
    }

    [TestCase]
    public void TestAPhoneStillOnAPresetTakesItsNewerValuesOnce()
    {
        // Written by the 2026-10-25 preview: Quality's values then, and no revision recorded.
        var quality = new Dictionary<string, string>(GraphicsPresets.QualityValues)
        {
            ["shader_variant"] = "full", ["hdr_2d"] = "true", [GraphicsPresets.Key] = "android",
        };
        AssertThat(GraphicsPresets.Upgrade(quality)).IsTrue();
        AssertThat(quality["shader_variant"]).IsEqual("lite");
        AssertThat(quality["hdr_2d"]).IsEqual("false");
        AssertThat(GraphicsPresets.Current(quality)).IsEqual("android");
        AssertThat(quality[GraphicsPresets.RevisionKey]).IsEqual(GraphicsPresets.Revision.ToString());

        // Once per revision: a hand edit made afterwards stays as written.
        quality["shader_variant"] = "full";
        AssertThat(GraphicsPresets.Upgrade(quality)).IsFalse();
        AssertThat(quality["shader_variant"]).IsEqual("full");

        // A custom phone keeps every value; only the revision is recorded.
        var custom = new Dictionary<string, string> { [GraphicsPresets.Key] = "custom", ["shader_variant"] = "full" };
        AssertThat(GraphicsPresets.Upgrade(custom)).IsTrue();
        AssertThat(custom["shader_variant"]).IsEqual("full");
        AssertThat(custom.Count).IsEqual(3);

        // A configuration that never chose a preset is ApplyPresetOnce's, not this.
        AssertThat(GraphicsPresets.Upgrade(new Dictionary<string, string>())).IsFalse();

        // Choosing a preset records the revision it was written from.
        var chosen = new Dictionary<string, string>();
        GraphicsPresets.Apply(GraphicsPresets.Performance, chosen);
        AssertThat(GraphicsPresets.Upgrade(chosen)).IsFalse();
    }

    [TestCase]
    public void TestAQualityPhoneTakesTheMemoryAndCoreSettingsAtRevision8()
    {
        // Written by revision 7: Quality's values then, the five settings Performance-only.
        var quality = new Dictionary<string, string>(GraphicsPresets.QualityValues)
        {
            ["android_guest_prime_core"] = "false", ["android_dotnet_gc"] = "", ["android_loh_compaction"] = "false",
            ["anim_collapse_constant_tracks"] = "false", ["fx_prefetch_prepare"] = "false",
            [GraphicsPresets.Key] = GraphicsPresets.Quality, [GraphicsPresets.RevisionKey] = "7",
        };
        AssertThat(GraphicsPresets.Revision).IsGreaterEqual(8);
        AssertThat(GraphicsPresets.Upgrade(quality)).IsTrue();
        AssertThat(quality["android_guest_prime_core"]).IsEqual("true");
        AssertThat(quality["android_dotnet_gc"]).IsEqual("GCgen0size=2000000 GCHighMemPercent=61");
        AssertThat(quality["android_loh_compaction"]).IsEqual("true");
        AssertThat(quality["anim_collapse_constant_tracks"]).IsEqual("true");
        AssertThat(quality["fx_prefetch_prepare"]).IsEqual("true");
        AssertThat(GraphicsPresets.Current(quality)).IsEqual(GraphicsPresets.Quality);

        // The arrival fit stays Performance's alone; the five are the same in both presets.
        AssertThat(quality["motion_arrival_fit"]).IsEqual("false");
        AssertThat(GraphicsPresets.PerformanceValues["motion_arrival_fit"]).IsEqual("true");
        foreach (string key in new[] { "android_guest_prime_core", "android_dotnet_gc", "android_loh_compaction",
                                       "anim_collapse_constant_tracks", "fx_prefetch_prepare" })
        {
            AssertThat(GraphicsPresets.PerformanceValues[key]).IsEqual(GraphicsPresets.QualityValues[key]);
        }
    }

    [TestCase]
    public void TestThePresetsAreQualityAndPerformance()
    {
        RuntimeCVarDefinition definition = RuntimeCVarSet.All.First(d => d.Name == GraphicsPresets.Key);
        AssertThat(definition.DefaultValue).IsEqual("android");
        RuntimeCVarOption[] options = RuntimeSettingsModel.OptionsFor(definition, new Dictionary<string, string>()).ToArray();
        AssertThat(options.Select(option => option.Value).ToArray()).IsEqual(new[] { "android", "android_low", "custom" });
        AssertThat(options.Select(option => option.Label).ToArray()).IsEqual(new[] { "Quality", "Performance", "Custom" });
        AssertThat(GraphicsPresets.Label("android")).IsEqual("Quality");
        AssertThat(GraphicsPresets.Label("android_low")).IsEqual("Performance");
        AssertThat(RuntimeConfig.ShouldShowCVar(definition)).IsTrue();

        // The revision is written on a phone but never offered as a choice.
        RuntimeCVarDefinition revision = RuntimeCVarSet.All.First(d => d.Name == GraphicsPresets.RevisionKey);
        AssertThat(RuntimeConfig.ShouldWriteCVar(revision)).IsTrue();
        AssertThat(RuntimeConfig.ShouldShowCVar(revision)).IsFalse();
    }

    [TestCase]
    public void TestADesktopNeitherShowsNorWritesThePreset()
    {
        GraphicsPresets.Offered = false;
        RuntimeCVarDefinition definition = RuntimeCVarSet.All.First(d => d.Name == GraphicsPresets.Key);
        AssertThat(RuntimeConfig.ShouldShowCVar(definition)).IsFalse();
        AssertThat(RuntimeConfig.ShouldWriteCVar(definition)).IsFalse();
        AssertThat(RuntimeConfig.ShouldWriteCVar(RuntimeCVarSet.All.First(d => d.Name == GraphicsPresets.RevisionKey))).IsFalse();

        // Changing a setting the preset decides records nothing.
        var model = new RuntimeSettingsModel();
        model.Values.Remove(GraphicsPresets.Key);
        AssertThat(model.RefreshGraphicsPreset(GraphicsPresets.Members.First())).IsFalse();
        AssertThat(model.Values.ContainsKey(GraphicsPresets.Key)).IsFalse();

        // A file a phone build wrote loses the key on the next desktop save.
        string config = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "refii-preset-" + System.Guid.NewGuid().ToString("N") + ".toml");
        try
        {
            AssertThat(RuntimeConfig.WriteValues(config, new Dictionary<string, string> { [GraphicsPresets.Key] = "android" }, out _)).IsTrue();
            AssertThat(RuntimeConfig.ReadValues(config).ContainsKey(GraphicsPresets.Key)).IsFalse();
        }
        finally { System.IO.File.Delete(config); }
    }

    [TestCase]
    public void TestPerformanceIsQualityWithItsCompromises()
    {
        var model = new RuntimeSettingsModel();
        model.ApplyGraphicsPreset("android_low");
        AssertThat(model.CurrentGraphicsPreset()).IsEqual("android_low");
        AssertThat(model.Values["render_scale_percent"]).IsEqual("30");
        AssertThat(model.Values["sun_shadow_distance"]).IsEqual("0");
        // The settings it does not change are Quality's.
        AssertThat(model.Values["entity_draw_distance_scale"]).IsEqual(GraphicsPresets.QualityValues["entity_draw_distance_scale"]);
        AssertThat(GraphicsPresets.PerformanceValues.Keys.OrderBy(k => k).ToArray())
            .IsEqual(GraphicsPresets.QualityValues.Keys.OrderBy(k => k).ToArray());

        model.ApplyGraphicsPreset("android");
        AssertThat(model.CurrentGraphicsPreset()).IsEqual("android");
    }

    [TestCase]
    public void TestEveryPresetSettingExists()
    {
        foreach (string key in GraphicsPresets.Members)
        {
            AssertThat(RuntimeCVarSet.All.Any(d => d.Name == key)).OverrideFailureMessage(key + " is not a setting").IsTrue();
        }
    }
}
