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
    public void TestOnlyUltraWarnsBeforeApplying()
    {
        AssertThat(GraphicsPresets.PickWarning(GraphicsPresets.Ultra)).Contains("not intended for casual use");
        AssertThat(GraphicsPresets.PickWarning(GraphicsPresets.Ultra)).Contains("performance issues");
        AssertThat(GraphicsPresets.PickWarning(GraphicsPresets.High)).IsNull();
        AssertThat(GraphicsPresets.PickWarning(GraphicsPresets.Medium)).IsNull();
        AssertThat(GraphicsPresets.PickWarning(GraphicsPresets.Low)).IsNull();
        AssertThat(GraphicsPresets.PickWarning(GraphicsPresets.Custom)).IsNull();
    }

    [TestCase]
    public void TestHighPresetWritesItsValuesAndReadsBack()
    {
        var model = new RuntimeSettingsModel();
        model.ApplyGraphicsPreset("android");

        AssertThat(model.CurrentGraphicsPreset()).IsEqual("android");
        AssertThat(model.Values[GraphicsPresets.Key]).IsEqual("android");
        foreach ((string key, string value) in GraphicsPresets.HighValues)
        {
            AssertThat(model.Values[key]).IsEqual(value);
        }
    }

    /// <summary>Every preset thins the distant trees: the launcher writes the setting's own default (off) otherwise.</summary>
    [TestCase]
    public void TestEveryPresetThinsTheDistantTrees()
    {
        AssertThat(GraphicsPresets.HighValues["tree_card_lod"]).IsEqual("true");
        AssertThat(GraphicsPresets.LowValues["tree_card_lod"]).IsEqual("true");
        AssertThat(GraphicsPresets.UltraValues["tree_card_lod"]).IsEqual("true");
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
        var values = new Dictionary<string, string>(GraphicsPresets.HighValues);
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

    /// <summary>Low asks for the low-memory tier on any phone; the others leave it to the phone's memory, and a phone already on Low takes it.</summary>
    [TestCase]
    public void TestLowTakesTheLowMemoryTier()
    {
        AssertThat(GraphicsPresets.LowValues["android_low_ram_mode"]).IsEqual("on");
        AssertThat(GraphicsPresets.MediumValues["android_low_ram_mode"]).IsEqual("auto");
        AssertThat(GraphicsPresets.HighValues["android_low_ram_mode"]).IsEqual("auto");
        AssertThat(GraphicsPresets.UltraValues["android_low_ram_mode"]).IsEqual("auto");
        var low = new Dictionary<string, string>
        {
            [GraphicsPresets.Key] = GraphicsPresets.Low, [GraphicsPresets.RevisionKey] = "12", ["android_low_ram_mode"] = "auto",
        };
        AssertThat(GraphicsPresets.Upgrade(low)).IsTrue();
        AssertThat(low["android_low_ram_mode"]).IsEqual("on");
        var custom = new Dictionary<string, string>
        {
            [GraphicsPresets.Key] = GraphicsPresets.Custom, [GraphicsPresets.RevisionKey] = "12", ["android_low_ram_mode"] = "auto",
        };
        GraphicsPresets.Upgrade(custom);
        AssertThat(custom["android_low_ram_mode"]).IsEqual("auto");
    }

    /// <summary>The first preset survives the next read: the fixture shadow migration has nothing left to do.</summary>
    [TestCase]
    public void TestTheFirstPresetSurvivesTheNextRead()
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"refii-first-preset-{System.Guid.NewGuid():N}.toml");
        try
        {
            var fresh = new Dictionary<string, string> { ["sun_shadow_map"] = "1024" };
            AssertThat(AndroidLauncherSettings.ApplyPresetOnce(fresh, "Adreno (TM) 640")).IsTrue();
            AssertThat(RuntimeConfig.WriteValues(path, fresh, out string _)).IsTrue();
            SortedDictionary<string, string> read = RuntimeConfig.ReadValues(path);
            AssertThat(read["placed_light_shadows"]).IsEqual("false");
            AssertThat(GraphicsPresets.Current(read)).IsEqual(GraphicsPresets.Low);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [TestCase]
    public void TestAPhoneGetsHighOnceAndKeepsThePlayersChoice()
    {
        var fresh = new Dictionary<string, string> { ["render_scale_percent"] = "59" };
        AssertThat(AndroidLauncherSettings.ApplyPresetOnce(fresh, "Adreno (TM) 830")).IsTrue();
        AssertThat(fresh[GraphicsPresets.Key]).IsEqual("android");
        AssertThat(GraphicsPresets.Current(fresh)).IsEqual("android");

        // Chosen once already, whatever was chosen: nothing is rewritten.
        var chosen = new Dictionary<string, string> { [GraphicsPresets.Key] = "custom", ["render_scale_percent"] = "80" };
        AssertThat(AndroidLauncherSettings.ApplyPresetOnce(chosen)).IsFalse();
        AssertThat(chosen["render_scale_percent"]).IsEqual("80");
        AssertThat(chosen.Count).IsEqual(2);
    }

    [TestCase]
    public void TestAnOlderAdrenoStartsOnLow()
    {
        AssertThat(AndroidLauncherSettings.FirstPreset("Adreno (TM) 640")).IsEqual((GraphicsPresets.Low, GraphicsPresets.AndroidFrameCap));
        AssertThat(AndroidLauncherSettings.FirstPreset("Adreno (TM) 660")).IsEqual((GraphicsPresets.Low, GraphicsPresets.AndroidFrameCap));
        AssertThat(AndroidLauncherSettings.FirstPreset("Adreno (TM) 740")).IsEqual((GraphicsPresets.High, GraphicsPresets.AndroidFrameCap));
        AssertThat(AndroidLauncherSettings.FirstPreset("Adreno (TM) 830")).IsEqual((GraphicsPresets.High, GraphicsPresets.AndroidFrameCap));
        AssertThat(AndroidLauncherSettings.FirstPreset("Mali-G715")).IsEqual((GraphicsPresets.High, GraphicsPresets.AndroidFrameCap));
        AssertThat(AndroidLauncherSettings.FirstPreset("")).IsEqual((GraphicsPresets.High, GraphicsPresets.AndroidFrameCap));

        var s10e = new Dictionary<string, string>();
        AssertThat(AndroidLauncherSettings.ApplyPresetOnce(s10e, "Adreno (TM) 640")).IsTrue();
        AssertThat(GraphicsPresets.Current(s10e)).IsEqual(GraphicsPresets.Low);
        AssertThat(s10e["max_fps"]).IsEqual("30");
    }

    [TestCase]
    public void TestAPhoneStillOnAPresetTakesItsNewerValuesOnce()
    {
        // Written by the 2026-10-25 preview: High's values then (labelled Quality), and no revision recorded.
        var quality = new Dictionary<string, string>(GraphicsPresets.HighValues)
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
        GraphicsPresets.Apply(GraphicsPresets.Low, chosen);
        AssertThat(GraphicsPresets.Upgrade(chosen)).IsFalse();
    }

    [TestCase]
    public void TestAHighPhoneTakesTheMemoryAndCoreSettingsAtRevision8()
    {
        // Written by revision 7: High's values then (labelled Quality), the five settings Low-only.
        var quality = new Dictionary<string, string>(GraphicsPresets.HighValues)
        {
            ["android_guest_prime_core"] = "false", ["android_dotnet_gc"] = "", ["android_loh_compaction"] = "false",
            ["anim_collapse_constant_tracks"] = "false", ["fx_prefetch_prepare"] = "false",
            [GraphicsPresets.Key] = GraphicsPresets.High, [GraphicsPresets.RevisionKey] = "7",
        };
        AssertThat(GraphicsPresets.Revision).IsGreaterEqual(8);
        AssertThat(GraphicsPresets.Upgrade(quality)).IsTrue();
        AssertThat(quality["android_guest_prime_core"]).IsEqual("true");
        AssertThat(quality["android_dotnet_gc"]).IsEqual("GCgen0size=2000000 GCHighMemPercent=61");
        AssertThat(quality["android_loh_compaction"]).IsEqual("true");
        AssertThat(quality["anim_collapse_constant_tracks"]).IsEqual("true");
        AssertThat(quality["fx_prefetch_prepare"]).IsEqual("true");
        AssertThat(GraphicsPresets.Current(quality)).IsEqual(GraphicsPresets.High);

        // The arrival fit stays Low's and Medium's; the five are the same in both presets.
        AssertThat(quality["motion_arrival_fit"]).IsEqual("false");
        AssertThat(GraphicsPresets.LowValues["motion_arrival_fit"]).IsEqual("true");
        foreach (string key in new[] { "android_guest_prime_core", "android_dotnet_gc", "android_loh_compaction",
                                       "anim_collapse_constant_tracks", "fx_prefetch_prepare" })
        {
            AssertThat(GraphicsPresets.LowValues[key]).IsEqual(GraphicsPresets.HighValues[key]);
        }
    }

    [TestCase]
    public void TestALowPhoneTakesTheTreeAndGroundSettingsAtRevision11()
    {
        var phone = new Dictionary<string, string>
        {
            [GraphicsPresets.Key] = GraphicsPresets.Low, [GraphicsPresets.RevisionKey] = "10",
        };
        AssertThat(GraphicsPresets.Revision).IsGreaterEqual(11);
        AssertThat(GraphicsPresets.Upgrade(phone)).IsTrue();
        AssertThat(phone["tree_far_reach"]).IsEqual("300");
        AssertThat(phone["tree_lod_bias"]).IsEqual("0.5");
        AssertThat(phone["terrain_lod_bias"]).IsEqual("0.5");
        AssertThat(phone["terrain_normal_maps"]).IsEqual("false");
        AssertThat(phone["foliage_density"]).IsEqual("0.5");
        AssertThat(GraphicsPresets.Current(phone)).IsEqual(GraphicsPresets.Low);

        // High keeps the settings' own defaults, so a High phone without the keys stays on it.
        var balanced = new Dictionary<string, string>();
        GraphicsPresets.Apply(GraphicsPresets.High, balanced);
        foreach (string key in new[] { "tree_far_reach", "tree_lod_bias", "terrain_lod_bias", "terrain_normal_maps", "foliage_density" })
        {
            balanced.Remove(key);
        }
        AssertThat(GraphicsPresets.Current(balanced)).IsEqual(GraphicsPresets.High);
    }

    [TestCase]
    public void TestAHighPhoneSoftensTheSunShadowAtRevision14()
    {
        // Written by revision 13: the map the host fixed at 1024 and no filter, which is Hard.
        var phone = new Dictionary<string, string>(GraphicsPresets.HighValues)
        {
            [GraphicsPresets.Key] = GraphicsPresets.High, [GraphicsPresets.RevisionKey] = "13",
        };
        phone.Remove("sun_shadow_filter");
        AssertThat(GraphicsPresets.Revision).IsGreaterEqual(14);
        AssertThat(GraphicsPresets.Upgrade(phone)).IsTrue();
        AssertThat(phone["sun_shadow_map"]).IsEqual("2048");
        AssertThat(phone["sun_shadow_filter"]).IsEqual("2");
        AssertThat(GraphicsPresets.Current(phone)).IsEqual(GraphicsPresets.High);

        // Ultra keeps High's map and filters at Soft Medium.
        var ultra = new Dictionary<string, string>
        {
            [GraphicsPresets.Key] = GraphicsPresets.Ultra, [GraphicsPresets.RevisionKey] = "13",
        };
        AssertThat(GraphicsPresets.Upgrade(ultra)).IsTrue();
        AssertThat(ultra["sun_shadow_map"]).IsEqual("2048");
        AssertThat(ultra["sun_shadow_filter"]).IsEqual("3");
    }

    [TestCase]
    public void TestThePresetsAreLowMediumHighAndUltra()
    {
        RuntimeCVarDefinition definition = RuntimeCVarSet.All.First(d => d.Name == GraphicsPresets.Key);
        AssertThat(definition.DefaultValue).IsEqual("android");
        RuntimeCVarOption[] options = RuntimeSettingsModel.OptionsFor(definition, new Dictionary<string, string>()).ToArray();
        AssertThat(options.Select(option => option.Value).ToArray()).IsEqual(new[] { "android_low", "android_mid", "android", "android_high", "custom" });
        AssertThat(options.Select(option => option.Label).ToArray()).IsEqual(new[] { "Low", "Medium", "High", "Ultra", "Custom" });
        // The stored names predate the labels: "android" has been Quality, then Balanced, and is High.
        AssertThat(GraphicsPresets.Label("android")).IsEqual("High");
        // "android_low" was Performance, then Ultra Performance: a phone on it keeps its values.
        AssertThat(GraphicsPresets.Label("android_low")).IsEqual("Low");
        AssertThat(GraphicsPresets.Label("android_mid")).IsEqual("Medium");
        AssertThat(GraphicsPresets.Label("android_high")).IsEqual("Ultra");
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
    public void TestLowIsHighWithItsCompromises()
    {
        var model = new RuntimeSettingsModel();
        model.ApplyGraphicsPreset("android_low");
        AssertThat(model.CurrentGraphicsPreset()).IsEqual("android_low");
        AssertThat(model.Values["render_scale_percent"]).IsEqual("40");
        AssertThat(model.Values["sun_shadow_distance"]).IsEqual("0");
        // The settings it does not change are High's.
        AssertThat(model.Values["entity_draw_distance_scale"]).IsEqual(GraphicsPresets.HighValues["entity_draw_distance_scale"]);
        AssertThat(GraphicsPresets.LowValues.Keys.OrderBy(k => k).ToArray())
            .IsEqual(GraphicsPresets.HighValues.Keys.OrderBy(k => k).ToArray());

        model.ApplyGraphicsPreset("android");
        AssertThat(model.CurrentGraphicsPreset()).IsEqual("android");
    }

    [TestCase]
    public void TestMediumSitsBetweenLowAndHigh()
    {
        var model = new RuntimeSettingsModel();
        model.ApplyGraphicsPreset("android_mid");
        AssertThat(model.CurrentGraphicsPreset()).IsEqual("android_mid");
        AssertThat(GraphicsPresets.MediumValues.Keys.OrderBy(k => k).ToArray())
            .IsEqual(GraphicsPresets.HighValues.Keys.OrderBy(k => k).ToArray());

        // Every number it sets lies between the two (inclusive), each setting's cost rising or falling with it;
        // a far reach of 0 is the whole reach.
        static double At(IReadOnlyDictionary<string, string> preset, string key) =>
            key == "tree_far_reach" && preset[key] == "0" ? double.MaxValue
                : double.Parse(preset[key], System.Globalization.CultureInfo.InvariantCulture);
        foreach ((string key, string value) in GraphicsPresets.MediumValues)
        {
            if (!double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))
            {
                continue;
            }

            double low = At(GraphicsPresets.LowValues, key), high = At(GraphicsPresets.HighValues, key);
            AssertThat(At(GraphicsPresets.MediumValues, key)).OverrideFailureMessage(key)
                .IsBetween(System.Math.Min(low, high), System.Math.Max(low, high));
        }

        // The shadowless picture is Low's.
        AssertThat(GraphicsPresets.MediumValues["sun_shadow_distance"]).IsEqual("0");
        AssertThat(GraphicsPresets.MediumValues["lite_shadowless"]).IsEqual("true");
    }

    [TestCase]
    public void TestUltraIsHighWithFewerCompromises()
    {
        var model = new RuntimeSettingsModel();
        model.ApplyGraphicsPreset("android_high");
        AssertThat(model.CurrentGraphicsPreset()).IsEqual("android_high");
        AssertThat(model.Values[GraphicsPresets.Key]).IsEqual("android_high");
        AssertThat(GraphicsPresets.UltraValues.Keys.OrderBy(k => k).ToArray())
            .IsEqual(GraphicsPresets.HighValues.Keys.OrderBy(k => k).ToArray());

        // Each setting it changes from High costs more, never less.
        string[] costlier = { "render_scale_percent", "prop_cull_distance_scale", "sun_shadow_distance",
                              "sun_shadow_reach_scale", "placed_light_limit", "placed_light_range",
                              "sun_shadow_filter" };
        foreach (string key in costlier)
        {
            AssertThat(double.Parse(GraphicsPresets.UltraValues[key], System.Globalization.CultureInfo.InvariantCulture))
                .OverrideFailureMessage(key).IsGreater(double.Parse(GraphicsPresets.HighValues[key], System.Globalization.CultureInfo.InvariantCulture));
        }

        AssertThat(double.Parse(GraphicsPresets.UltraValues["mesh_lod_pixels"], System.Globalization.CultureInfo.InvariantCulture))
            .IsLess(double.Parse(GraphicsPresets.HighValues["mesh_lod_pixels"], System.Globalization.CultureInfo.InvariantCulture));

        // Everything else, the same-picture shortcuts among it, is High's.
        foreach ((string key, string value) in GraphicsPresets.HighValues)
        {
            if (!costlier.Contains(key) && key != "mesh_lod_pixels")
            {
                AssertThat(GraphicsPresets.UltraValues[key]).OverrideFailureMessage(key).IsEqual(value);
            }
        }
    }

    [TestCase]
    public void TestAPhoneOnHighStaysOnItAfterTheRename()
    {
        // A phone on the old Quality stored "android": it reads as High, keeps its values, and
        // does not move to the new Ultra.
        var phone = new Dictionary<string, string>();
        GraphicsPresets.Apply(GraphicsPresets.High, phone);
        AssertThat(phone[GraphicsPresets.Key]).IsEqual("android");
        AssertThat(GraphicsPresets.Upgrade(phone)).IsFalse();
        AssertThat(GraphicsPresets.Current(phone)).IsEqual(GraphicsPresets.High);
        AssertThat(phone["render_scale_percent"]).IsEqual("60");
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
