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

    private const string LowRam = "android_low_ram_mode";

    /// <summary>Revision 22: the low-memory tier is the phone's own setting, in no preset, so changing it by hand
    /// leaves the preset as it was.</summary>
    [TestCase]
    public void TestTheLowMemoryTierIsInNoPreset()
    {
        foreach (string preset in new[] { GraphicsPresets.Low, GraphicsPresets.Medium, GraphicsPresets.High, GraphicsPresets.Ultra })
        {
            AssertThat(GraphicsPresets.Values(preset)!.ContainsKey(LowRam)).OverrideFailureMessage(preset).IsFalse();
        }

        AssertThat(GraphicsPresets.Members.Contains(LowRam)).IsFalse();
        AssertThat(RuntimeCVarSet.All.First(d => d.Name == LowRam).DefaultValue).IsEqual("auto");

        var phone = new Dictionary<string, string>();
        GraphicsPresets.Apply(GraphicsPresets.Low, phone);
        AssertThat(phone.ContainsKey(LowRam)).IsFalse();
        foreach (string tier in new[] { "on", "off", "auto" })
        {
            phone[LowRam] = tier;
            AssertThat(GraphicsPresets.Current(phone)).OverrideFailureMessage(tier).IsEqual(GraphicsPresets.Low);
        }

        var model = new RuntimeSettingsModel();
        model.ApplyGraphicsPreset(GraphicsPresets.High);
        model.Values[LowRam] = "on";
        AssertThat(model.RefreshGraphicsPreset(LowRam)).IsFalse();
        AssertThat(model.Values[GraphicsPresets.Key]).IsEqual(GraphicsPresets.High);
    }

    /// <summary>Revision 22: a phone stored on Low before 22 with the tier "on" had it from Low (13-21), so it goes back
    /// to auto once.</summary>
    [TestCase]
    public void TestALowPhoneBefore22GivesBackTheTierLowSetOn()
    {
        var low = new Dictionary<string, string>
        {
            [GraphicsPresets.Key] = GraphicsPresets.Low, [GraphicsPresets.RevisionKey] = "19", [LowRam] = "on",
        };
        AssertThat(GraphicsPresets.Upgrade(low)).IsTrue();
        AssertThat(low[LowRam]).IsEqual("auto");
        AssertThat(GraphicsPresets.Current(low)).IsEqual(GraphicsPresets.Low);

        // Once: set on again afterwards, it stays.
        low[LowRam] = "on";
        AssertThat(GraphicsPresets.Upgrade(low)).IsFalse();
        AssertThat(low[LowRam]).IsEqual("on");

        // With no revision recorded (before revision 2), as at any revision below 22.
        var preview = new Dictionary<string, string> { [GraphicsPresets.Key] = GraphicsPresets.Low, [LowRam] = "on" };
        GraphicsPresets.Upgrade(preview);
        AssertThat(preview[LowRam]).IsEqual("auto");
    }

    /// <summary>Revision 22: Low (lean), retired at 19, was Low's and set the tier on too: it goes back to auto as Low does.</summary>
    [TestCase]
    public void TestALowLeanPhoneGivesBackTheTierLowSetOn()
    {
        var lean = new Dictionary<string, string>
        {
            [GraphicsPresets.Key] = "android_low_lean", [GraphicsPresets.RevisionKey] = "18", [LowRam] = "on",
        };
        AssertThat(GraphicsPresets.Upgrade(lean)).IsTrue();
        AssertThat(lean[LowRam]).IsEqual("auto");
        AssertThat(lean[GraphicsPresets.Key]).IsEqual(GraphicsPresets.Low);
    }

    /// <summary>Revision 22: a Low phone whose tier is off or auto keeps it; the tier is only given back where Low set it.</summary>
    [TestCase]
    public void TestALowPhoneBefore22KeepsATierLowDidNotSet()
    {
        foreach (string tier in new[] { "off", "auto" })
        {
            var low = new Dictionary<string, string>
            {
                [GraphicsPresets.Key] = GraphicsPresets.Low, [GraphicsPresets.RevisionKey] = "19", [LowRam] = tier,
            };
            GraphicsPresets.Upgrade(low);
            AssertThat(low[LowRam]).OverrideFailureMessage(tier).IsEqual(tier);
        }

        // Without the key, nothing is written: the setting's own default (auto) stands.
        var bare = new Dictionary<string, string> { [GraphicsPresets.Key] = GraphicsPresets.Low, [GraphicsPresets.RevisionKey] = "19" };
        GraphicsPresets.Upgrade(bare);
        AssertThat(bare.ContainsKey(LowRam)).IsFalse();
    }

    /// <summary>Revision 22: a phone on Low already at 22 keeps "on", which the player set.</summary>
    [TestCase]
    public void TestALowPhoneAt22KeepsTheTierItSet()
    {
        var low = new Dictionary<string, string>();
        GraphicsPresets.Apply(GraphicsPresets.Low, low);
        low[LowRam] = "on";
        AssertThat(GraphicsPresets.Upgrade(low)).IsFalse();
        AssertThat(low[LowRam]).IsEqual("on");
    }

    /// <summary>Revision 22: a phone on Medium, High, Ultra or Custom before 22 keeps whatever tier it has.</summary>
    [TestCase]
    public void TestOtherPhonesBefore22KeepTheirTier()
    {
        foreach (string preset in new[] { GraphicsPresets.Medium, GraphicsPresets.High, GraphicsPresets.Ultra, GraphicsPresets.Custom })
        {
            var phone = new Dictionary<string, string>
            {
                [GraphicsPresets.Key] = preset, [GraphicsPresets.RevisionKey] = "19", [LowRam] = "on",
            };
            AssertThat(GraphicsPresets.Upgrade(phone)).OverrideFailureMessage(preset).IsTrue();
            AssertThat(phone[LowRam]).OverrideFailureMessage(preset).IsEqual("on");
        }
    }

    /// <summary>The first preset survives the next read: the fixture shadow migration has nothing left to do.</summary>
    [TestCase]
    public void TestTheFirstPresetSurvivesTheNextRead()
    {
        string path = System.IO.Path.Combine(Refii.Tests.TestTemp.Root, $"refii-first-preset-{System.Guid.NewGuid():N}.toml");
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
        // Every tree to its full reach since revision 22 (200 m at 21, 300 m from 11).
        AssertThat(phone["tree_far_reach"]).IsEqual("0");
        AssertThat(phone["tree_lod_bias"]).IsEqual("0.5");
        AssertThat(phone["terrain_lod_bias"]).IsEqual("0.5");
        AssertThat(phone["terrain_normal_maps"]).IsEqual("false");
        AssertThat(phone["foliage_density"]).IsEqual("0.25");
        AssertThat(GraphicsPresets.Current(phone)).IsEqual(GraphicsPresets.Low);

        // High keeps the settings' own defaults, so a High phone without the keys stays on it.
        var balanced = new Dictionary<string, string>();
        GraphicsPresets.Apply(GraphicsPresets.High, balanced);
        foreach (string key in new[] { "tree_far_reach", "terrain_lod_bias" })
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
        AssertThat(options.Select(option => option.Value).ToArray())
            .IsEqual(new[] { "android_low", "android_mid", "android", "android_high", "custom" });
        AssertThat(options.Select(option => option.Label).ToArray()).IsEqual(new[] { "Low", "Medium", "High", "Ultra", "Custom" });
        // Each choice's label is the preset's own.
        foreach (RuntimeCVarOption option in options)
        {
            AssertThat(GraphicsPresets.Label(option.Value)).OverrideFailureMessage(option.Value).IsEqual(option.Label);
        }
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
        string config = System.IO.Path.Combine(Refii.Tests.TestTemp.Root, "refii-preset-" + System.Guid.NewGuid().ToString("N") + ".toml");
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
                              "sun_shadow_filter", "tree_lod_bias", "foliage_density", "corpse_lifetime_seconds",
                              // The tree pictures from three times the distance (revision 23): more models drawn.
                              AndroidTreeCards.StartKey };
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
            // The far trees' sway: Ultra lets every tree sway (0), High stills those past 150 m.
            if (!costlier.Contains(key) && key != "mesh_lod_pixels" && key != Refii.Level.Engine_TreeSwayFar.Key &&
                key != "terrain_normal_maps" &&
                key != Refii.Models.Engine_TreeCardLods.ClusterKey && key != Refii.Models.Engine_TreeCardLods.TrunkKey)
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
        AssertThat(phone["render_scale_percent"]).IsEqual("50");
    }

    /// <summary>
    /// Revision 19: Low takes Low (lean)'s settings and Low (lean) is gone; each preset sets its lighting
    /// model. Low runs the modern model, Medium, High and Ultra the fable one; a phone that stored Low
    /// (lean) moves to Low; and Low to High is High as revision 18 had it, for every setting Low sets.
    /// </summary>
    [TestCase]
    public void TestRevision19FoldsLowLeanIntoLowAndNamesEachLightingModel()
    {
        AssertThat(GraphicsPresets.Revision).IsGreaterEqual(19);
        AssertThat(GraphicsPresets.LowValues["android_modern_lighting"]).IsEqual("true");
        AssertThat(GraphicsPresets.MediumValues["android_modern_lighting"]).IsEqual("false");
        AssertThat(GraphicsPresets.HighValues["android_modern_lighting"]).IsEqual("false");
        AssertThat(GraphicsPresets.UltraValues["android_modern_lighting"]).IsEqual("false");

        // Revision 18's Low (lean), stored by name: Low's values and name on the next start.
        var lean = new Dictionary<string, string>
        {
            [GraphicsPresets.Key] = "android_low_lean", [GraphicsPresets.RevisionKey] = "18",
            ["android_modern_lighting"] = "true", ["lite_normal_maps"] = "fast", ["lite_fog"] = "table",
        };
        AssertThat(GraphicsPresets.Upgrade(lean)).IsTrue();
        AssertThat(lean[GraphicsPresets.Key]).IsEqual(GraphicsPresets.Low);
        AssertThat(lean[GraphicsPresets.RevisionKey]).IsEqual(GraphicsPresets.Revision.ToString());
        AssertThat(lean["lite_normal_maps"]).IsEqual("off");
        AssertThat(GraphicsPresets.Current(lean)).IsEqual(GraphicsPresets.Low);
        AssertThat(GraphicsPresets.Label("android_low_lean")).IsEqual("Low");

        // High as revision 18 wrote it: its own values then, and the settings it did not name at their defaults.
        var high18 = new Dictionary<string, string>(System.StringComparer.Ordinal)
        {
            ["android_modern_lighting"] = "false", ["lite_fog"] = "lean", ["lite_ground_ao"] = "near",
            ["lite_prop_fragment_world"] = "false", ["lite_pack_lightmap_uv"] = "false", ["lite_fold_modern_model"] = "false",
        };
        foreach (string key in high18.Keys)
        {
            AssertThat(RuntimeCVarSet.All.First(d => d.Name == key).DefaultValue).OverrideFailureMessage(key).IsEqual(high18[key]);
        }

        var phone = new Dictionary<string, string>();
        GraphicsPresets.Apply(GraphicsPresets.Low, phone);
        GraphicsPresets.Apply(GraphicsPresets.High, phone);
        AssertThat(GraphicsPresets.Current(phone)).IsEqual(GraphicsPresets.High);
        foreach (string key in GraphicsPresets.LowValues.Keys)
        {
            string expected = high18.TryGetValue(key, out string? was) ? was : GraphicsPresets.HighValues[key];
            AssertThat(phone[key]).OverrideFailureMessage(key).IsEqual(expected);
        }
        // Revision 18's High settings are all still High's, unchanged.
        AssertThat(phone["render_scale_percent"]).IsEqual("50");
        AssertThat(phone["lite_normal_maps"]).IsEqual("full");
        AssertThat(phone["lite_skip_black_lights"]).IsEqual("true");
    }

    /// <summary>
    /// Revision 20: Low draws tree leaves flat-lit and props vertex-lit; Medium, High and Ultra light both per pixel, so
    /// leaving Low puts them back, and a phone that stored Low at 19 takes both on its next start.
    /// </summary>
    [TestCase]
    public void TestRevision20FlatLeavesInLowOnly()
    {
        AssertThat(GraphicsPresets.Revision).IsGreaterEqual(20);
        string key = Refii.AndroidRuntime.AndroidFlatLeaves.Key;
        AssertThat(GraphicsPresets.LowValues[key]).IsEqual("true");
        AssertThat(GraphicsPresets.MediumValues[key]).IsEqual("false");
        AssertThat(GraphicsPresets.HighValues[key]).IsEqual("false");
        AssertThat(GraphicsPresets.UltraValues[key]).IsEqual("false");
        AssertThat(RuntimeCVarSet.All.First(d => d.Name == key).DefaultValue).IsEqual("false");

        var low19 = new Dictionary<string, string> { [GraphicsPresets.Key] = GraphicsPresets.Low, [GraphicsPresets.RevisionKey] = "19" };
        AssertThat(GraphicsPresets.Upgrade(low19)).IsTrue();
        AssertThat(low19[key]).IsEqual("true");

        var phone = new Dictionary<string, string>();
        GraphicsPresets.Apply(GraphicsPresets.Low, phone);
        GraphicsPresets.Apply(GraphicsPresets.Medium, phone);
        AssertThat(phone[key]).IsEqual("false");

        string props = Refii.AndroidRuntime.AndroidVertexLitProps.Key;
        AssertThat(GraphicsPresets.LowValues[props]).IsEqual("true");
        AssertThat(GraphicsPresets.MediumValues[props]).IsEqual("false");
        AssertThat(GraphicsPresets.HighValues[props]).IsEqual("false");
        AssertThat(GraphicsPresets.UltraValues[props]).IsEqual("false");
        AssertThat(low19[props]).IsEqual("true");
        AssertThat(phone[props]).IsEqual("false");

        // The fable model's fold and the plants' folds, the same picture, in every preset.
        string fold = Refii.Models.Engine_ShaderVariant.FoldFableModelKey;
        string plants = Refii.Models.Engine_ShaderSpecialisation.VegetationFoldsKey;
        string nearestFirst = Refii.AndroidRuntime.AndroidOpaqueOrder.FrontToBackKey;
        foreach (var values in new[] { GraphicsPresets.LowValues, GraphicsPresets.MediumValues, GraphicsPresets.HighValues, GraphicsPresets.UltraValues })
        {
            AssertThat(values[fold]).IsEqual("true");
            AssertThat(values[plants]).IsEqual("true");
            AssertThat(values[nearestFirst]).IsEqual("true");
        }
    }

    /// <summary>Revision 21: Low, Medium and High thin trees built from leaf clusters and far trunks; Ultra draws every triangle.</summary>
    [TestCase]
    public void TestRevision21ThinsLeafClustersBelowUltra()
    {
        foreach (string key in new[] { Refii.Models.Engine_TreeCardLods.ClusterKey, Refii.Models.Engine_TreeCardLods.TrunkKey })
        {
            AssertThat(GraphicsPresets.LowValues[key]).IsEqual("true");
            AssertThat(GraphicsPresets.MediumValues[key]).IsEqual("true");
            AssertThat(GraphicsPresets.HighValues[key]).IsEqual("true");
            AssertThat(GraphicsPresets.UltraValues[key]).IsEqual("false");
            AssertThat(RuntimeCVarSet.All.First(d => d.Name == key).DefaultValue).IsEqual("false");

            var phone = new Dictionary<string, string>();
            GraphicsPresets.Apply(GraphicsPresets.High, phone);
            AssertThat(phone[key]).IsEqual("true");
            GraphicsPresets.Apply(GraphicsPresets.Ultra, phone);
            AssertThat(phone[key]).IsEqual("false");
        }
    }

    /// <summary>Revision 21: Low swaps trees to their far models at half the stated distance (never nearer than
    /// 40 m, Engine_TreeReach); Medium did too until revision 22, and High and Ultra at the stated one.</summary>
    [TestCase]
    public void TestRevision21HandsTreesOverSoonerInLow()
    {
        string key = Refii.Level.Engine_TreeReach.TreeHandoverKey;
        AssertThat(GraphicsPresets.LowValues[key]).IsEqual("0.5");
        AssertThat(GraphicsPresets.HighValues[key]).IsEqual("1");
        AssertThat(GraphicsPresets.UltraValues[key]).IsEqual("1");
        AssertThat(RuntimeCVarSet.All.First(d => d.Name == key).DefaultValue).IsEqual("1");

        var phone = new Dictionary<string, string>();
        GraphicsPresets.Apply(GraphicsPresets.Low, phone);
        AssertThat(phone[key]).IsEqual("0.5");
        GraphicsPresets.Apply(GraphicsPresets.High, phone);
        AssertThat(phone[key]).IsEqual("1");
    }

    /// <summary>Revision 21: Low lights hedges and other cut-out props flat, and Medium too since revision 23; High and
    /// Ultra per pixel.</summary>
    [TestCase]
    public void TestRevision21LightsCutOutPropsFlatBelowHigh()
    {
        string key = Refii.AndroidRuntime.AndroidFlatCutouts.Key;
        AssertThat(GraphicsPresets.LowValues[key]).IsEqual("true");
        AssertThat(GraphicsPresets.MediumValues[key]).IsEqual("true");
        AssertThat(GraphicsPresets.HighValues[key]).IsEqual("false");
        AssertThat(GraphicsPresets.UltraValues[key]).IsEqual("false");
        AssertThat(RuntimeCVarSet.All.First(d => d.Name == key).DefaultValue).IsEqual("false");
    }

    /// <summary>Revision 21: every preset generates scenery's two coarser far levels.</summary>
    [TestCase]
    public void TestRevision21GeneratesFarLevelsInEveryPreset()
    {
        string key = Refii.Models.Engine_MdlAutoLod.FarKey;
        AssertThat(GraphicsPresets.LowValues[key]).IsEqual("true");
        AssertThat(GraphicsPresets.MediumValues[key]).IsEqual("true");
        AssertThat(GraphicsPresets.HighValues[key]).IsEqual("true");
        AssertThat(GraphicsPresets.UltraValues[key]).IsEqual("true");
        AssertThat(RuntimeCVarSet.All.First(d => d.Name == key).DefaultValue).IsEqual("false");
    }

    /// <summary>Revision 21: Low batches the undergrowth in 16 m cells; the other presets keep 32 m.</summary>
    [TestCase]
    public void TestRevision21BatchesUndergrowthInSmallerCellsInLowOnly()
    {
        string key = Refii.Level.Engine_LevelProps.FoliageCellKey;
        AssertThat(GraphicsPresets.LowValues[key]).IsEqual("16");
        AssertThat(GraphicsPresets.MediumValues[key]).IsEqual("0");
        AssertThat(GraphicsPresets.HighValues[key]).IsEqual("0");
        AssertThat(GraphicsPresets.UltraValues[key]).IsEqual("0");
    }

    /// <summary>Revision 21: Low, which casts no sun shadow, runs vertex shaders in world space; the other presets in view space.</summary>
    [TestCase]
    public void TestRevision21RunsVertexShadersInWorldSpaceInLowOnly()
    {
        string key = Refii.Models.Engine_ShaderVariant.WorldVertexKey;
        AssertThat(GraphicsPresets.LowValues[key]).IsEqual("true");
        AssertThat(GraphicsPresets.LowValues["sun_shadow_distance"]).IsEqual("0");
        AssertThat(GraphicsPresets.MediumValues[key]).IsEqual("false");
        AssertThat(GraphicsPresets.HighValues[key]).IsEqual("false");
        AssertThat(GraphicsPresets.UltraValues[key]).IsEqual("false");
    }

    /// <summary>
    /// Revision 21: a phone that stored Low at revision 20, with that revision's values, takes Low's new values on its
    /// next start and still reads as Low; a Medium phone at 20 takes the far generated levels.
    /// </summary>
    [TestCase]
    public void TestRevision21UpgradesALowPhoneStoredAtRevision20()
    {
        AssertThat(GraphicsPresets.Revision).IsGreaterEqual(20);
        var phone = new Dictionary<string, string>();
        GraphicsPresets.Apply(GraphicsPresets.Low, phone);

        // Revision 20's Low: 300 m trees, half the undergrowth, and none of revision 21's keys written.
        phone[GraphicsPresets.RevisionKey] = "19";
        phone["tree_far_reach"] = "300";
        phone["foliage_density"] = "0.5";
        foreach (string added in new[]
        {
            Refii.Level.Engine_TreeReach.TreeHandoverKey, Refii.AndroidRuntime.AndroidFlatCutouts.Key,
            Refii.Level.Engine_LevelProps.FoliageCellKey, Refii.Models.Engine_ShaderVariant.WorldVertexKey,
            Refii.Models.Engine_MdlAutoLod.FarKey,
        })
        {
            phone.Remove(added);
        }

        AssertThat(GraphicsPresets.Upgrade(phone)).IsTrue();
        AssertThat(phone[GraphicsPresets.RevisionKey]).IsEqual(GraphicsPresets.Revision.ToString());
        // The current Low's reach: the whole of it since revision 22.
        AssertThat(phone["tree_far_reach"]).IsEqual(GraphicsPresets.LowValues["tree_far_reach"]);
        AssertThat(phone["foliage_density"]).IsEqual("0.25");
        AssertThat(phone[Refii.Level.Engine_TreeReach.TreeHandoverKey]).IsEqual("0.5");
        AssertThat(phone[Refii.AndroidRuntime.AndroidFlatCutouts.Key]).IsEqual("true");
        AssertThat(phone[Refii.Level.Engine_LevelProps.FoliageCellKey]).IsEqual("16");
        AssertThat(phone[Refii.Models.Engine_ShaderVariant.WorldVertexKey]).IsEqual("true");
        AssertThat(phone[Refii.Models.Engine_MdlAutoLod.FarKey]).IsEqual("true");
        AssertThat(GraphicsPresets.Current(phone)).IsEqual(GraphicsPresets.Low);
        AssertThat(GraphicsPresets.Upgrade(phone)).IsFalse();

        var medium = new Dictionary<string, string> { [GraphicsPresets.Key] = GraphicsPresets.Medium, [GraphicsPresets.RevisionKey] = "19" };
        AssertThat(GraphicsPresets.Upgrade(medium)).IsTrue();
        AssertThat(medium[Refii.Models.Engine_MdlAutoLod.FarKey]).IsEqual("true");
        AssertThat(GraphicsPresets.Current(medium)).IsEqual(GraphicsPresets.Medium);
    }

    /// <summary>Revision 22: Low and Medium draw far trees as pictures (High and Ultra too since revision 23).</summary>
    [TestCase]
    public void TestRevision22DrawsFarTreesAsPicturesInLowAndMedium()
    {
        AssertThat(GraphicsPresets.Revision).IsGreaterEqual(20);
        string key = AndroidTreeCards.Key;
        AssertThat(GraphicsPresets.LowValues[key]).IsEqual("true");
        AssertThat(GraphicsPresets.MediumValues[key]).IsEqual("true");
        AssertThat(RuntimeCVarSet.All.First(d => d.Name == key).DefaultValue).IsEqual("false");

        var phone = new Dictionary<string, string>();
        GraphicsPresets.Apply(GraphicsPresets.Medium, phone);
        AssertThat(phone[key]).IsEqual("true");
        AssertThat(GraphicsPresets.Current(phone)).IsEqual(GraphicsPresets.Medium);
    }

    /// <summary>Revision 22: with the pictures on, Low draws trees to their full reach and Medium hands them over at the
    /// stated distance; Low keeps half.</summary>
    [TestCase]
    public void TestRevision22GivesLowItsTreeReachAndMediumItsHandOver()
    {
        AssertThat(GraphicsPresets.LowValues["tree_far_reach"]).IsEqual("0");
        AssertThat(GraphicsPresets.MediumValues["tree_far_reach"]).IsEqual("0");
        AssertThat(GraphicsPresets.HighValues["tree_far_reach"]).IsEqual("0");
        AssertThat(GraphicsPresets.UltraValues["tree_far_reach"]).IsEqual("0");

        string handover = Refii.Level.Engine_TreeReach.TreeHandoverKey;
        AssertThat(GraphicsPresets.LowValues[handover]).IsEqual("0.5");
        AssertThat(GraphicsPresets.MediumValues[handover]).IsEqual("1");
        AssertThat(GraphicsPresets.HighValues[handover]).IsEqual("1");
        AssertThat(GraphicsPresets.UltraValues[handover]).IsEqual("1");
    }

    /// <summary>Revision 22: every preset trims the fog (the same picture to rounding).</summary>
    [TestCase]
    public void TestRevision22TrimsTheFogInEveryPreset()
    {
        string key = AndroidFogTrims.Key;
        AssertThat(GraphicsPresets.LowValues[key]).IsEqual("true");
        AssertThat(GraphicsPresets.MediumValues[key]).IsEqual("true");
        AssertThat(GraphicsPresets.HighValues[key]).IsEqual("true");
        AssertThat(GraphicsPresets.UltraValues[key]).IsEqual("true");
        AssertThat(RuntimeCVarSet.All.First(d => d.Name == key).DefaultValue).IsEqual("false");
    }

    /// <summary>
    /// Revision 22: a phone that stored Low or Medium at revision 21, with that revision's values, takes the new ones on its
    /// next start and still reads as its preset; a High phone takes the fog trims (and, at 23, the tree pictures).
    /// </summary>
    [TestCase]
    public void TestRevision22UpgradesPhonesStoredAtRevision21()
    {
        var low = new Dictionary<string, string>();
        GraphicsPresets.Apply(GraphicsPresets.Low, low);
        // Revision 21's Low: trees to 200 m, the low-memory tier set on by Low, and none of revision 22's keys written.
        low[GraphicsPresets.RevisionKey] = "19";
        low["tree_far_reach"] = "200";
        low["android_low_ram_mode"] = "on";
        low.Remove(AndroidTreeCards.Key);
        low.Remove(AndroidFogTrims.Key);
        AssertThat(GraphicsPresets.Upgrade(low)).IsTrue();
        AssertThat(low[GraphicsPresets.RevisionKey]).IsEqual(GraphicsPresets.Revision.ToString());
        AssertThat(low["tree_far_reach"]).IsEqual("0");
        AssertThat(low["android_low_ram_mode"]).IsEqual("auto");
        AssertThat(low[AndroidTreeCards.Key]).IsEqual("true");
        AssertThat(low[AndroidFogTrims.Key]).IsEqual("true");
        AssertThat(GraphicsPresets.Current(low)).IsEqual(GraphicsPresets.Low);
        AssertThat(GraphicsPresets.Upgrade(low)).IsFalse();

        var medium = new Dictionary<string, string>();
        GraphicsPresets.Apply(GraphicsPresets.Medium, medium);
        // Revision 21's Medium: trees handed over at half the distance.
        medium[GraphicsPresets.RevisionKey] = "19";
        medium[Refii.Level.Engine_TreeReach.TreeHandoverKey] = "0.5";
        medium.Remove(AndroidTreeCards.Key);
        medium.Remove(AndroidFogTrims.Key);
        AssertThat(GraphicsPresets.Upgrade(medium)).IsTrue();
        AssertThat(medium[Refii.Level.Engine_TreeReach.TreeHandoverKey]).IsEqual("1");
        AssertThat(medium[AndroidTreeCards.Key]).IsEqual("true");
        AssertThat(medium[AndroidFogTrims.Key]).IsEqual("true");
        AssertThat(GraphicsPresets.Current(medium)).IsEqual(GraphicsPresets.Medium);

        var high = new Dictionary<string, string> { [GraphicsPresets.Key] = GraphicsPresets.High, [GraphicsPresets.RevisionKey] = "19" };
        AssertThat(GraphicsPresets.Upgrade(high)).IsTrue();
        AssertThat(high[AndroidFogTrims.Key]).IsEqual("true");
        AssertThat(high[AndroidTreeCards.Key]).IsEqual(GraphicsPresets.HighValues[AndroidTreeCards.Key]);
        AssertThat(GraphicsPresets.Current(high)).IsEqual(GraphicsPresets.High);
    }

    /// <summary>Revision 23: every preset draws far trees as pictures, High and below from the stated distance and Ultra
    /// from three times it.</summary>
    [TestCase]
    public void TestRevision23DrawsFarTreesAsPicturesInEveryPreset()
    {
        AssertThat(GraphicsPresets.Revision).IsGreaterEqual(20);
        foreach (var values in new[] { GraphicsPresets.LowValues, GraphicsPresets.MediumValues, GraphicsPresets.HighValues, GraphicsPresets.UltraValues })
        {
            AssertThat(values[AndroidTreeCards.Key]).IsEqual("true");
        }

        string start = AndroidTreeCards.StartKey;
        AssertThat(GraphicsPresets.LowValues[start]).IsEqual("1");
        AssertThat(GraphicsPresets.MediumValues[start]).IsEqual("1");
        AssertThat(GraphicsPresets.HighValues[start]).IsEqual("1");
        AssertThat(GraphicsPresets.UltraValues[start]).IsEqual("3");
        AssertThat(RuntimeCVarSet.All.First(d => d.Name == start).DefaultValue).IsEqual("1");

        var phone = new Dictionary<string, string>();
        GraphicsPresets.Apply(GraphicsPresets.Ultra, phone);
        AssertThat(phone[start]).IsEqual("3");
        GraphicsPresets.Apply(GraphicsPresets.High, phone);
        AssertThat(phone[start]).IsEqual("1");
        AssertThat(GraphicsPresets.Current(phone)).IsEqual(GraphicsPresets.High);
    }

    /// <summary>Revision 23: every preset skips the sun-shadow lookup past its fade and on faces turned from the sun, and a
    /// lamp's maths out of its range (engine patch 0014, the same picture); the lamp buffer skip stays out.</summary>
    [TestCase]
    public void TestRevision23SkipsShadowAndLampWorkInEveryPreset()
    {
        string[] skips =
        {
            AndroidEnginePatchSwitches.SunShadowBeyondFadeKey, AndroidEnginePatchSwitches.SunShadowFacingAwayKey,
            AndroidEnginePatchSwitches.LampOutOfRangeKey,
        };
        foreach (var values in new[] { GraphicsPresets.LowValues, GraphicsPresets.MediumValues, GraphicsPresets.HighValues, GraphicsPresets.UltraValues })
        {
            foreach (string key in skips)
            {
                AssertThat(values[key]).OverrideFailureMessage(key).IsEqual("true");
            }

            AssertThat(values.ContainsKey(AndroidEnginePatchSwitches.LampBufferSkipsUnlitKey)).IsFalse();
        }

        foreach (string key in skips)
        {
            AssertThat(RuntimeCVarSet.All.First(d => d.Name == key).DefaultValue).OverrideFailureMessage(key).IsEqual("false");
        }
    }

    /// <summary>Revision 23: Medium lights hedges and other cut-out props flat, as Low does; High and Ultra per pixel.</summary>
    [TestCase]
    public void TestRevision23LightsCutOutPropsFlatInMedium()
    {
        string key = AndroidFlatCutouts.Key;
        AssertThat(GraphicsPresets.MediumValues[key]).IsEqual(GraphicsPresets.LowValues[key]);
        AssertThat(GraphicsPresets.MediumValues[key]).IsEqual("true");
        AssertThat(GraphicsPresets.HighValues[key]).IsEqual("false");
        AssertThat(GraphicsPresets.UltraValues[key]).IsEqual("false");

        var phone = new Dictionary<string, string>();
        GraphicsPresets.Apply(GraphicsPresets.Medium, phone);
        AssertThat(phone[key]).IsEqual("true");
        GraphicsPresets.Apply(GraphicsPresets.High, phone);
        AssertThat(phone[key]).IsEqual("false");
    }

    /// <summary>Revision 23: Low drops the highlight of props lit at their corners; Medium, High and Ultra light props per
    /// pixel and keep it.</summary>
    [TestCase]
    public void TestRevision23DropsCornerHighlightsInLowOnly()
    {
        string key = AndroidVertexLitProps.NoSpecularKey;
        AssertThat(GraphicsPresets.LowValues[key]).IsEqual("true");
        AssertThat(GraphicsPresets.LowValues[AndroidVertexLitProps.Key]).IsEqual("true");
        AssertThat(GraphicsPresets.MediumValues[key]).IsEqual("false");
        AssertThat(GraphicsPresets.HighValues[key]).IsEqual("false");
        AssertThat(GraphicsPresets.UltraValues[key]).IsEqual("false");
        AssertThat(RuntimeCVarSet.All.First(d => d.Name == key).DefaultValue).IsEqual("false");

        var phone = new Dictionary<string, string>();
        GraphicsPresets.Apply(GraphicsPresets.Low, phone);
        GraphicsPresets.Apply(GraphicsPresets.Medium, phone);
        AssertThat(phone[key]).IsEqual("false");
        AssertThat(GraphicsPresets.Current(phone)).IsEqual(GraphicsPresets.Medium);
    }

    /// <summary>Revision 23's other change is a setting default, not a preset member: the low-memory tier keeps every
    /// material shortcut (lite_material_folds_low_ram "all").</summary>
    [TestCase]
    public void TestRevision23TheLowMemoryTierKeepsTheMaterialShortcuts()
    {
        string key = Refii.Models.Engine_ShaderSpecialisation.MaterialFoldsLowRamKey;
        AssertThat(RuntimeCVarSet.All.First(d => d.Name == key).DefaultValue).IsEqual("all");
        AssertThat(GraphicsPresets.Members.Contains(key)).IsFalse();
        AssertThat(GraphicsPresets.HighValues[Refii.Models.Engine_ShaderSpecialisation.MaterialFoldsKey]).IsEqual("true");
    }

    /// <summary>
    /// Revision 23: a phone that stored each preset at revision 22, with that revision's values, takes the new ones on its
    /// next start and still reads as its preset.
    /// </summary>
    [TestCase]
    public void TestRevision23UpgradesPhonesStoredAtRevision22()
    {
        string[] added =
        {
            AndroidTreeCards.StartKey, AndroidEnginePatchSwitches.SunShadowBeyondFadeKey,
            AndroidEnginePatchSwitches.SunShadowFacingAwayKey, AndroidEnginePatchSwitches.LampOutOfRangeKey,
            AndroidVertexLitProps.NoSpecularKey,
        };
        foreach (string preset in new[] { GraphicsPresets.Low, GraphicsPresets.Medium, GraphicsPresets.High, GraphicsPresets.Ultra })
        {
            var phone = new Dictionary<string, string>();
            GraphicsPresets.Apply(preset, phone);
            // Revision 22's values: none of 23's keys written, High and Ultra without the tree pictures, Medium's
            // cut-outs per pixel.
            phone[GraphicsPresets.RevisionKey] = "19";
            foreach (string key in added)
            {
                phone.Remove(key);
            }

            if (preset == GraphicsPresets.High || preset == GraphicsPresets.Ultra)
            {
                phone[AndroidTreeCards.Key] = "false";
            }

            if (preset == GraphicsPresets.Medium)
            {
                phone[AndroidFlatCutouts.Key] = "false";
            }

            AssertThat(GraphicsPresets.Upgrade(phone)).OverrideFailureMessage(preset).IsTrue();
            AssertThat(phone[GraphicsPresets.RevisionKey]).IsEqual(GraphicsPresets.Revision.ToString());
            foreach ((string key, string value) in GraphicsPresets.Values(preset)!)
            {
                AssertThat(phone[key]).OverrideFailureMessage(preset + " " + key).IsEqual(value);
            }

            AssertThat(phone[AndroidTreeCards.Key]).OverrideFailureMessage(preset).IsEqual("true");
            AssertThat(phone[AndroidEnginePatchSwitches.LampOutOfRangeKey]).OverrideFailureMessage(preset).IsEqual("true");
            AssertThat(GraphicsPresets.Current(phone)).OverrideFailureMessage(preset).IsEqual(preset);
            AssertThat(GraphicsPresets.Upgrade(phone)).OverrideFailureMessage(preset).IsFalse();
        }
    }

    /// <summary>Revision 24: every preset culls behind town buildings while one is near (occlusion_culling "auto"); the
    /// setting's own default, the desktop's, stays off, and the buffer's settings stay out of the presets.</summary>
    [TestCase]
    public void TestRevision24CullsBehindTownBuildingsInEveryPreset()
    {
        AssertThat(GraphicsPresets.Revision).IsGreaterEqual(20);
        string key = AndroidOcclusionAuto.Key;
        foreach (var values in new[] { GraphicsPresets.LowValues, GraphicsPresets.MediumValues, GraphicsPresets.HighValues, GraphicsPresets.UltraValues })
        {
            AssertThat(values[key]).IsEqual("auto");
            AssertThat(AndroidOcclusionAuto.Parse(values[key])).IsEqual(AndroidOcclusionAuto.Mode.Auto);
            AssertThat(values.ContainsKey(AndroidOcclusionAuto.MetresKey)).IsFalse();
        }

        AssertThat(RuntimeCVarSet.All.First(d => d.Name == key).DefaultValue).IsEqual("false");
        AssertThat(RuntimeCVarsAndroid.AllKeys.Contains(key)).IsFalse();
    }

    /// <summary>Revision 24: a desktop keeps occlusion culling off by default and takes no preset value for it: the
    /// presets are a phone's, and its launcher still shows the row.</summary>
    [TestCase]
    public void TestRevision24LeavesTheDesktopsOcclusionCullingOff()
    {
        GraphicsPresets.Offered = false;
        string key = AndroidOcclusionAuto.Key;
        AssertThat(AndroidLauncherSettings.Shows(key)).IsTrue();
        AssertThat(AndroidSettingsLayout.Baseline(key)).IsNull();
        var model = new RuntimeSettingsModel();
        model.Values.Remove(GraphicsPresets.Key);
        AssertThat(model.RefreshGraphicsPreset(key)).IsFalse();
        AssertThat(model.Values.ContainsKey(GraphicsPresets.Key)).IsFalse();

        // A desktop file never stores a preset, so nothing upgrades it.
        var desktop = new Dictionary<string, string> { [key] = "false" };
        AssertThat(GraphicsPresets.Upgrade(desktop)).IsFalse();
        AssertThat(desktop[key]).IsEqual("false");
    }

    /// <summary>Revision 24: a bare true or false stored before the setting took auto still reads as on or off; a phone
    /// on a preset moves to auto on its next start, a custom phone keeps what it set.</summary>
    [TestCase]
    public void TestRevision24MovesAPresetPhonesOcclusionCullingToAuto()
    {
        string key = AndroidOcclusionAuto.Key;
        AssertThat(AndroidOcclusionAuto.Parse("true")).IsEqual(AndroidOcclusionAuto.Mode.On);
        AssertThat(AndroidOcclusionAuto.Parse("false")).IsEqual(AndroidOcclusionAuto.Mode.Off);

        foreach (string stored in new[] { "false", "true" })
        {
            var phone = new Dictionary<string, string>();
            GraphicsPresets.Apply(GraphicsPresets.Medium, phone);
            phone[GraphicsPresets.RevisionKey] = "19";
            phone[key] = stored;
            AssertThat(GraphicsPresets.Upgrade(phone)).IsTrue();
            AssertThat(phone[key]).OverrideFailureMessage(stored).IsEqual("auto");
            AssertThat(GraphicsPresets.Current(phone)).IsEqual(GraphicsPresets.Medium);

            var custom = new Dictionary<string, string>();
            GraphicsPresets.Apply(GraphicsPresets.Medium, custom);
            custom[GraphicsPresets.Key] = GraphicsPresets.Custom;
            custom[GraphicsPresets.RevisionKey] = "19";
            custom[key] = stored;
            AssertThat(GraphicsPresets.Upgrade(custom)).IsTrue();
            AssertThat(custom[key]).OverrideFailureMessage(stored).IsEqual(stored);
            AssertThat(GraphicsPresets.Current(custom)).IsEqual(GraphicsPresets.Custom);
        }

        // Setting it by hand on a phone now reads as custom.
        var hand = new Dictionary<string, string>();
        GraphicsPresets.Apply(GraphicsPresets.High, hand);
        hand[key] = "true";
        AssertThat(GraphicsPresets.Current(hand)).IsEqual(GraphicsPresets.Custom);
    }

    /// <summary>Revision 24: every preset leaves Godot's ambient block out of the fable model's lite builds and puts
    /// static props on a build per bake mode; both are off by default. Low's modern model never takes the first.</summary>
    [TestCase]
    public void TestRevision24TakesTheAmbientAndBakeModeBuildsInEveryPreset()
    {
        string ambient = AndroidFableAmbientOff.Key;
        string bake = Refii.Models.Engine_ShaderSpecialisation.BakeVariantsKey;
        foreach (var values in new[] { GraphicsPresets.LowValues, GraphicsPresets.MediumValues, GraphicsPresets.HighValues, GraphicsPresets.UltraValues })
        {
            AssertThat(values[ambient]).IsEqual("true");
            AssertThat(values[bake]).IsEqual("true");
        }

        AssertThat(RuntimeCVarSet.All.First(d => d.Name == ambient).DefaultValue).IsEqual("false");
        AssertThat(RuntimeCVarSet.All.First(d => d.Name == bake).DefaultValue).IsEqual("false");
        AssertThat(GraphicsPresets.LowValues["android_modern_lighting"]).IsEqual("true");
        AssertThat(AndroidFableAmbientOff.ChosenFor(true, true, modernModel: true, fable2: true)).IsFalse();
        AssertThat(GraphicsPresets.MediumValues["android_modern_lighting"]).IsEqual("false");
        AssertThat(AndroidFableAmbientOff.ChosenFor(true, true, modernModel: false, fable2: true)).IsTrue();
        // The bake-mode builds are drawn under the loading screen with the rest.
        AssertThat(GraphicsPresets.HighValues[Refii.Models.Engine_PipelineWarmup.ConfigKey]).IsEqual("true");
    }

    /// <summary>
    /// Revision 24: a phone that stored each preset at revision 23, with that revision's values, takes the new ones on its
    /// next start and still reads as its preset.
    /// </summary>
    [TestCase]
    public void TestRevision24UpgradesPhonesStoredAtRevision23()
    {
        foreach (string preset in new[] { GraphicsPresets.Low, GraphicsPresets.Medium, GraphicsPresets.High, GraphicsPresets.Ultra })
        {
            var phone = new Dictionary<string, string>();
            GraphicsPresets.Apply(preset, phone);
            // Revision 23's values: occlusion culling and the fable ambient at their defaults (as the launcher wrote
            // them), bake-mode builds off.
            phone[GraphicsPresets.RevisionKey] = "19";
            phone[AndroidOcclusionAuto.Key] = "false";
            phone.Remove(AndroidFableAmbientOff.Key);
            phone[Refii.Models.Engine_ShaderSpecialisation.BakeVariantsKey] = "false";
            AssertThat(GraphicsPresets.Current(phone)).OverrideFailureMessage(preset).IsEqual(GraphicsPresets.Custom);

            AssertThat(GraphicsPresets.Upgrade(phone)).OverrideFailureMessage(preset).IsTrue();
            AssertThat(phone[GraphicsPresets.RevisionKey]).IsEqual(GraphicsPresets.Revision.ToString());
            foreach ((string key, string value) in GraphicsPresets.Values(preset)!)
            {
                AssertThat(phone[key]).OverrideFailureMessage(preset + " " + key).IsEqual(value);
            }

            AssertThat(phone[AndroidOcclusionAuto.Key]).OverrideFailureMessage(preset).IsEqual("auto");
            AssertThat(phone[AndroidFableAmbientOff.Key]).OverrideFailureMessage(preset).IsEqual("true");
            AssertThat(phone[Refii.Models.Engine_ShaderSpecialisation.BakeVariantsKey]).OverrideFailureMessage(preset).IsEqual("true");
            AssertThat(GraphicsPresets.Current(phone)).OverrideFailureMessage(preset).IsEqual(preset);
            AssertThat(GraphicsPresets.Upgrade(phone)).OverrideFailureMessage(preset).IsFalse();
        }
    }

    /// <summary>Revision 25: every preset keeps the far-tree pictures' heading as the camera turns
    /// (tree_billboard_turn_degrees 0), and so does the setting's own default.</summary>
    [TestCase]
    public void TestRevision25StopsTurnRecapturesInEveryPreset()
    {
        AssertThat(GraphicsPresets.Revision).IsGreaterEqual(20);
        string key = AndroidTreeCards.TurnKey;
        foreach (var values in new[] { GraphicsPresets.LowValues, GraphicsPresets.MediumValues, GraphicsPresets.HighValues, GraphicsPresets.UltraValues })
        {
            AssertThat(values[key]).IsEqual("0");
            AssertThat(AndroidTreeCards.Levers(values).Turn).IsEqual(0f);
        }

        AssertThat(GraphicsPresets.Members.Contains(key)).IsTrue();
        AssertThat(RuntimeCVarSet.All.First(d => d.Name == key).DefaultValue).IsEqual("0");
        AssertThat(AndroidTreeCards.TurnDefault).IsEqual(0f);
    }

    /// <summary>
    /// Revision 25: a phone that stored each preset at revision 24, with the turn at that revision's 10 (as the launcher
    /// wrote the old default), takes 0 on its next start and still reads as its preset; a custom phone keeps its 10.
    /// </summary>
    [TestCase]
    public void TestRevision25UpgradesPhonesStoredAtRevision24()
    {
        string key = AndroidTreeCards.TurnKey;
        foreach (string preset in new[] { GraphicsPresets.Low, GraphicsPresets.Medium, GraphicsPresets.High, GraphicsPresets.Ultra })
        {
            var phone = new Dictionary<string, string>();
            GraphicsPresets.Apply(preset, phone);
            phone[GraphicsPresets.RevisionKey] = "19";
            phone[key] = "10";
            AssertThat(GraphicsPresets.Current(phone)).OverrideFailureMessage(preset).IsEqual(GraphicsPresets.Custom);

            AssertThat(GraphicsPresets.Upgrade(phone)).OverrideFailureMessage(preset).IsTrue();
            AssertThat(phone[GraphicsPresets.RevisionKey]).IsEqual(GraphicsPresets.Revision.ToString());
            AssertThat(phone[key]).OverrideFailureMessage(preset).IsEqual("0");
            AssertThat(GraphicsPresets.Current(phone)).OverrideFailureMessage(preset).IsEqual(preset);
            AssertThat(GraphicsPresets.Upgrade(phone)).OverrideFailureMessage(preset).IsFalse();

            var custom = new Dictionary<string, string>();
            GraphicsPresets.Apply(preset, custom);
            custom[GraphicsPresets.Key] = GraphicsPresets.Custom;
            custom[GraphicsPresets.RevisionKey] = "19";
            custom[key] = "10";
            AssertThat(GraphicsPresets.Upgrade(custom)).OverrideFailureMessage(preset).IsTrue();
            AssertThat(custom[key]).OverrideFailureMessage(preset).IsEqual("10");
            AssertThat(GraphicsPresets.Current(custom)).OverrideFailureMessage(preset).IsEqual(GraphicsPresets.Custom);
        }
    }

    /// <summary>Revision 26: every preset wakes the heat-haze layer only for haze on screen; the setting's own default
    /// (the desktop's) stays off.</summary>
    [TestCase]
    public void TestRevision26WakesTheHazeLayerOnlyInViewInEveryPreset()
    {
        AssertThat(GraphicsPresets.Revision).IsGreaterEqual(20);
        string key = AndroidDisplacementGate.Key;
        foreach (var values in new[] { GraphicsPresets.LowValues, GraphicsPresets.MediumValues, GraphicsPresets.HighValues, GraphicsPresets.UltraValues })
        {
            AssertThat(values[key]).IsEqual("true");
        }

        AssertThat(GraphicsPresets.Members.Contains(key)).IsTrue();
        AssertThat(RuntimeCVarSet.All.First(d => d.Name == key).DefaultValue).IsEqual("false");
    }

    /// <summary>
    /// Revision 26: a phone that stored each preset at revision 25, without the key, takes it on its next start and still
    /// reads as its preset; a custom phone keeps the setting off.
    /// </summary>
    [TestCase]
    public void TestRevision26UpgradesPhonesStoredAtRevision25()
    {
        string key = AndroidDisplacementGate.Key;
        foreach (string preset in new[] { GraphicsPresets.Low, GraphicsPresets.Medium, GraphicsPresets.High, GraphicsPresets.Ultra })
        {
            var phone = new Dictionary<string, string>();
            GraphicsPresets.Apply(preset, phone);
            phone[GraphicsPresets.RevisionKey] = "19";
            phone.Remove(key);
            AssertThat(GraphicsPresets.Current(phone)).OverrideFailureMessage(preset).IsEqual(GraphicsPresets.Custom);

            AssertThat(GraphicsPresets.Upgrade(phone)).OverrideFailureMessage(preset).IsTrue();
            AssertThat(phone[GraphicsPresets.RevisionKey]).IsEqual(GraphicsPresets.Revision.ToString());
            AssertThat(phone[key]).OverrideFailureMessage(preset).IsEqual("true");
            AssertThat(GraphicsPresets.Current(phone)).OverrideFailureMessage(preset).IsEqual(preset);
            AssertThat(GraphicsPresets.Upgrade(phone)).OverrideFailureMessage(preset).IsFalse();

            var custom = new Dictionary<string, string>();
            GraphicsPresets.Apply(preset, custom);
            custom[GraphicsPresets.Key] = GraphicsPresets.Custom;
            custom[GraphicsPresets.RevisionKey] = "19";
            custom.Remove(key);
            AssertThat(GraphicsPresets.Upgrade(custom)).OverrideFailureMessage(preset).IsTrue();
            AssertThat(custom.ContainsKey(key)).OverrideFailureMessage(preset).IsFalse();
            AssertThat(GraphicsPresets.Current(custom)).OverrideFailureMessage(preset).IsEqual(GraphicsPresets.Custom);
        }
    }

    /// <summary>Revision 28: every preset copies and redraws only the heat haze's part of the screen; the setting's own
    /// default (the desktop's) stays off.</summary>
    [TestCase]
    public void TestRevision28DrawsTheHazeOnlyWhereItIsInEveryPreset()
    {
        AssertThat(GraphicsPresets.Revision).IsGreaterEqual(20);
        string key = AndroidDisplacementRect.Key;
        foreach (var values in new[] { GraphicsPresets.LowValues, GraphicsPresets.MediumValues, GraphicsPresets.HighValues, GraphicsPresets.UltraValues })
        {
            AssertThat(values[key]).IsEqual("true");
        }

        AssertThat(GraphicsPresets.Members.Contains(key)).IsTrue();
        AssertThat(RuntimeCVarSet.All.First(d => d.Name == key).DefaultValue).IsEqual("false");
    }

    /// <summary>
    /// Revision 28: a phone that stored each preset at revision 27, without the key, takes it on its next start and still
    /// reads as its preset; a custom phone keeps the setting off.
    /// </summary>
    [TestCase]
    public void TestRevision28UpgradesPhonesStoredAtRevision27()
    {
        string key = AndroidDisplacementRect.Key;
        foreach (string preset in new[] { GraphicsPresets.Low, GraphicsPresets.Medium, GraphicsPresets.High, GraphicsPresets.Ultra })
        {
            var phone = new Dictionary<string, string>();
            GraphicsPresets.Apply(preset, phone);
            phone[GraphicsPresets.RevisionKey] = "19";
            phone.Remove(key);
            AssertThat(GraphicsPresets.Current(phone)).OverrideFailureMessage(preset).IsEqual(GraphicsPresets.Custom);

            AssertThat(GraphicsPresets.Upgrade(phone)).OverrideFailureMessage(preset).IsTrue();
            AssertThat(phone[GraphicsPresets.RevisionKey]).IsEqual(GraphicsPresets.Revision.ToString());
            AssertThat(phone[key]).OverrideFailureMessage(preset).IsEqual("true");
            AssertThat(GraphicsPresets.Current(phone)).OverrideFailureMessage(preset).IsEqual(preset);
            AssertThat(GraphicsPresets.Upgrade(phone)).OverrideFailureMessage(preset).IsFalse();

            var custom = new Dictionary<string, string>();
            GraphicsPresets.Apply(preset, custom);
            custom[GraphicsPresets.Key] = GraphicsPresets.Custom;
            custom[GraphicsPresets.RevisionKey] = "19";
            custom.Remove(key);
            AssertThat(GraphicsPresets.Upgrade(custom)).OverrideFailureMessage(preset).IsTrue();
            AssertThat(custom.ContainsKey(key)).OverrideFailureMessage(preset).IsFalse();
            AssertThat(GraphicsPresets.Current(custom)).OverrideFailureMessage(preset).IsEqual(GraphicsPresets.Custom);
        }
    }

    /// <summary>Revision 27: Medium draws hedges and bushes depth first; Low, High and Ultra do not, and the setting's own
    /// default stays off.</summary>
    [TestCase]
    public void TestRevision27DrawsHedgesDepthFirstInMediumOnly()
    {
        AssertThat(GraphicsPresets.Revision).IsGreaterEqual(20);
        string key = AndroidCutoutPrepass.Key;
        AssertThat(GraphicsPresets.MediumValues[key]).IsEqual("true");
        foreach (var values in new[] { GraphicsPresets.LowValues, GraphicsPresets.HighValues, GraphicsPresets.UltraValues })
        {
            AssertThat(values[key]).IsEqual("false");
        }

        AssertThat(GraphicsPresets.Members.Contains(key)).IsTrue();
        AssertThat(RuntimeCVarSet.All.First(d => d.Name == key).DefaultValue).IsEqual("false");
        // The scope a preset gets is the key's default: the two-sided builds.
        AssertThat(RuntimeCVarSet.All.First(d => d.Name == AndroidCutoutPrepass.ScopeKey).DefaultValue).IsEqual("two_sided");
        // A restart-only setting: moving to or from Medium shows the preset's restart line.
        var low = new Dictionary<string, string>();
        GraphicsPresets.Apply(GraphicsPresets.Low, low);
        AssertThat(AndroidRestartNotes.PresetNeedsRestart(low, GraphicsPresets.Medium)).IsTrue();
    }

    /// <summary>
    /// Revision 27: a phone that stored each preset at revision 26, without the key, takes the preset's value on its next
    /// start (on for Medium, off for the rest) and still reads as its preset; a custom phone keeps the setting unset.
    /// </summary>
    [TestCase]
    public void TestRevision27UpgradesPhonesStoredAtRevision26()
    {
        string key = AndroidCutoutPrepass.Key;
        foreach (string preset in new[] { GraphicsPresets.Low, GraphicsPresets.Medium, GraphicsPresets.High, GraphicsPresets.Ultra })
        {
            var phone = new Dictionary<string, string>();
            GraphicsPresets.Apply(preset, phone);
            phone[GraphicsPresets.RevisionKey] = "19";
            phone.Remove(key);
            if (preset == GraphicsPresets.Medium)
            {
                AssertThat(GraphicsPresets.Current(phone)).IsEqual(GraphicsPresets.Custom);
            }

            AssertThat(GraphicsPresets.Upgrade(phone)).OverrideFailureMessage(preset).IsTrue();
            AssertThat(phone[GraphicsPresets.RevisionKey]).IsEqual(GraphicsPresets.Revision.ToString());
            AssertThat(phone[key]).OverrideFailureMessage(preset).IsEqual(preset == GraphicsPresets.Medium ? "true" : "false");
            AssertThat(GraphicsPresets.Current(phone)).OverrideFailureMessage(preset).IsEqual(preset);
            AssertThat(GraphicsPresets.Upgrade(phone)).OverrideFailureMessage(preset).IsFalse();

            var custom = new Dictionary<string, string>();
            GraphicsPresets.Apply(preset, custom);
            custom[GraphicsPresets.Key] = GraphicsPresets.Custom;
            custom[GraphicsPresets.RevisionKey] = "19";
            custom.Remove(key);
            AssertThat(GraphicsPresets.Upgrade(custom)).OverrideFailureMessage(preset).IsTrue();
            AssertThat(custom.ContainsKey(key)).OverrideFailureMessage(preset).IsFalse();
            if (preset == GraphicsPresets.Medium)
            {
                // Without the key a Medium-like custom phone is not Medium; the others' off is the default, so theirs
                // still match their preset's values.
                AssertThat(GraphicsPresets.Current(custom)).IsEqual(GraphicsPresets.Custom);
            }
        }
    }

    /// <summary>Revision 20: Low draws the 2D layer at the layout size; the other presets at the screen's pixels.</summary>
    [TestCase]
    public void TestRevision20DrawsTheCanvasAtLayoutSizeInLowOnly()
    {
        string key = Refii.AndroidRuntime.AndroidCanvasResolution.Key;
        AssertThat(GraphicsPresets.LowValues[key]).IsEqual("true");
        AssertThat(GraphicsPresets.MediumValues[key]).IsEqual("false");
        AssertThat(GraphicsPresets.HighValues[key]).IsEqual("false");
        AssertThat(GraphicsPresets.UltraValues[key]).IsEqual("false");
        AssertThat(RuntimeCVarSet.All.First(d => d.Name == key).DefaultValue).IsEqual("false");

        var phone = new Dictionary<string, string>();
        GraphicsPresets.Apply(GraphicsPresets.Low, phone);
        GraphicsPresets.Apply(GraphicsPresets.High, phone);
        AssertThat(phone[key]).IsEqual("false");
        AssertThat(GraphicsPresets.Current(phone)).IsEqual(GraphicsPresets.High);
    }

    [TestCase]
    public void TestRevision20AddsCorpseLifetimePerPreset()
    {
        AssertThat(GraphicsPresets.Revision).IsGreaterEqual(20);
        AssertThat(GraphicsPresets.LowValues["corpse_lifetime_seconds"]).IsEqual("0");
        AssertThat(GraphicsPresets.MediumValues["corpse_lifetime_seconds"]).IsEqual("10");
        AssertThat(GraphicsPresets.HighValues["corpse_lifetime_seconds"]).IsEqual("20");
        AssertThat(GraphicsPresets.UltraValues["corpse_lifetime_seconds"]).IsEqual("45");

        var rev19High = new Dictionary<string, string>(GraphicsPresets.HighValues)
        {
            [GraphicsPresets.Key] = GraphicsPresets.High,
            [GraphicsPresets.RevisionKey] = "19",
        };
        rev19High.Remove("corpse_lifetime_seconds");

        AssertThat(GraphicsPresets.Upgrade(rev19High)).IsTrue();
        AssertThat(rev19High[GraphicsPresets.RevisionKey]).IsEqual(GraphicsPresets.Revision.ToString());
        AssertThat(rev19High["corpse_lifetime_seconds"]).IsEqual("20");
        AssertThat(GraphicsPresets.Current(rev19High)).IsEqual(GraphicsPresets.High);
    }

    [TestCase]
    public void TestRevision21UpgradesPhonesPreviouslyAtRevision20()
    {
        AssertThat(GraphicsPresets.Revision).IsEqual(21);

        var rev20Low = new Dictionary<string, string>
        {
            [GraphicsPresets.Key] = GraphicsPresets.Low,
            [GraphicsPresets.RevisionKey] = "20",
            [LowRam] = "on",
            [AndroidOcclusionAuto.Key] = "false",
        };
        AssertThat(GraphicsPresets.Upgrade(rev20Low)).IsTrue();
        AssertThat(rev20Low[GraphicsPresets.RevisionKey]).IsEqual("21");
        AssertThat(rev20Low[AndroidTreeCards.Key]).IsEqual("true");
        AssertThat(rev20Low[AndroidOcclusionAuto.Key]).IsEqual("auto");
        AssertThat(rev20Low[LowRam]).IsEqual("auto");
        AssertThat(GraphicsPresets.Current(rev20Low)).IsEqual(GraphicsPresets.Low);

        var rev20High = new Dictionary<string, string>
        {
            [GraphicsPresets.Key] = GraphicsPresets.High,
            [GraphicsPresets.RevisionKey] = "20",
            [AndroidOcclusionAuto.Key] = "false",
        };
        AssertThat(GraphicsPresets.Upgrade(rev20High)).IsTrue();
        AssertThat(rev20High[GraphicsPresets.RevisionKey]).IsEqual("21");
        AssertThat(rev20High[AndroidTreeCards.Key]).IsEqual("true");
        AssertThat(rev20High[AndroidOcclusionAuto.Key]).IsEqual("auto");
        AssertThat(GraphicsPresets.Current(rev20High)).IsEqual(GraphicsPresets.High);
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
