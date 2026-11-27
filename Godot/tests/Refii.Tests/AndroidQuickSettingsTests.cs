namespace Refii.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.AndroidRuntime;
using Refii.Launcher.Core;

[TestSuite]
public class AndroidQuickSettingsTests
{
    [BeforeTest]
    public void StandInForAPhone() => GraphicsPresets.Offered = true;

    [AfterTest]
    public void BackToThisPlatform() => GraphicsPresets.Offered = Refii.Core.Engine_Platform.IsAndroid;

    private static string Config(string text)
    {
        string path = Path.Combine(Refii.Tests.TestTemp.Root, "refii-quick-" + Guid.NewGuid().ToString("N") + ".toml");
        File.WriteAllText(path, text);
        return path;
    }

    /// <summary>Hiding and restoring the overlay preserves the existing layout opacity.</summary>
    [TestCase]
    public void TouchControlsCanBeHiddenAndRestored()
    {
        string directory = Directory.CreateTempSubdirectory("refii-touch-").FullName;
        string config = Path.Combine(directory, "refii.toml");
        string otherTitle = Path.Combine(directory, "refiii.toml");
        try
        {
            File.WriteAllText(config, "touch_opacity = 0.7\n");
            AssertThat(AndroidQuickSettings.HideTouchControls(config)).IsFalse();
            AndroidQuickSettings.SetHideTouchControls(config, true);
            AssertThat(AndroidQuickSettings.HideTouchControls(otherTitle)).IsTrue();
            AndroidQuickSettings.SetHideTouchControls(otherTitle, false);
            AssertThat(AndroidQuickSettings.HideTouchControls(config)).IsFalse();
            AssertThat(AndroidQuickSettings.TouchOpacity(config)).IsEqual(0.7f);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestCase]
    public void TestFrameCapRowOffersUncappedTwentyThirtyFortySixty()
    {
        AssertThat(AndroidQuickSettings.FrameCaps).ContainsExactly(0, 20, 30, 40, 60);
        AssertThat(AndroidQuickSettings.FrameCapChoiceLabel(0)).IsEqual("\u221E");
        AssertThat(AndroidQuickSettings.FrameCapChoiceLabel(30)).IsEqual("30");
        AssertThat(AndroidQuickSettings.FrameCapLabel(0)).IsEqual("Frame limit");
        AssertThat(AndroidQuickSettings.FrameCapLabel(60)).IsEqual("Frame limit");
        // A hand-set cap the row doesn't offer is named, since no button stands pressed for it.
        AssertThat(AndroidQuickSettings.FrameCapLabel(45)).IsEqual("Frame limit: 45 FPS");
    }

    [TestCase]
    public void TestOptionsOffersLowMediumHighAndUltra()
    {
        AssertThat(AndroidQuickSettings.Presets).ContainsExactly(
            GraphicsPresets.Low, GraphicsPresets.Medium, GraphicsPresets.High, GraphicsPresets.Ultra);
        AssertThat(AndroidQuickSettings.PresetLabel(GraphicsPresets.Low)).IsEqual("Preset: Low");
        AssertThat(AndroidQuickSettings.PresetLabel(GraphicsPresets.Medium)).IsEqual("Preset: Medium");
        AssertThat(AndroidQuickSettings.PresetLabel(GraphicsPresets.High)).IsEqual("Preset: High");
        AssertThat(AndroidQuickSettings.PresetLabel(GraphicsPresets.Ultra)).IsEqual("Preset: Ultra");
    }

    [TestCase]
    public void TestCapAndPresetChangeIndependently()
    {
        string config = Config("sun_shadow_splits = 2\nlog_file = \"x.log\"\n");
        try
        {
            var live = new Dictionary<string, string>();
            AndroidQuickSettings.ApplyPreset(config, GraphicsPresets.Low, (k, v) => live[k] = v);
            AndroidQuickSettings.SetFrameCap(config, 60);

            // A cap of 60 still reads as Low, and every preset setting went to the game.
            AssertThat(AndroidQuickSettings.Preset(config)).IsEqual(GraphicsPresets.Low);
            AssertThat(AndroidQuickSettings.FrameCap(config)).IsEqual(60);
            AssertThat(live.Count).IsEqual(GraphicsPresets.LowValues.Count);
            AssertThat(live["render_scale_percent"]).IsEqual("40");

            // Switching preset keeps the cap and the settings outside the preset.
            AndroidQuickSettings.ApplyPreset(config, GraphicsPresets.High, (_, _) => { });
            var values = RuntimeConfig.ReadValues(config);
            AssertThat(AndroidQuickSettings.Preset(config)).IsEqual(GraphicsPresets.High);
            AssertThat(values["max_fps"]).IsEqual("60");
            AssertThat(values["sun_shadow_splits"]).IsEqual("2");
            AssertThat(values["log_file"]).IsEqual("x.log");
        }
        finally { File.Delete(config); }
    }

    [TestCase]
    public void TestTheFpsCounterIsOffUntilTurnedOn()
    {
        string config = Config("max_fps = 30\n");
        try
        {
            AssertThat(AndroidQuickSettings.FpsCounter(config)).IsFalse();
            AndroidQuickSettings.SetFpsCounter(config, true);
            AssertThat(AndroidQuickSettings.FpsCounter(config)).IsTrue();
            AssertThat(RuntimeConfig.ReadValues(config)["fps_counter"]).IsEqual("true");
            AssertThat(RuntimeConfig.ReadValues(config)["max_fps"]).IsEqual("30");
            AssertThat(AndroidQuickSettings.FpsCounterLabel(true)).IsEqual("FPS counter: On");
            AssertThat(AndroidQuickSettings.FpsCounterLabel(false)).IsEqual("FPS counter: Off");
        }
        finally { File.Delete(config); }
    }

    [TestCase]
    public void TestTheLauncherShowsUntilSkippingIsTurnedOn()
    {
        string config = Config("max_fps = 30\n");
        try
        {
            AssertThat(AndroidQuickSettings.SkipLauncher(config)).IsFalse();
            AndroidQuickSettings.SetSkipLauncher(config, true);
            AssertThat(AndroidQuickSettings.SkipLauncher(config)).IsTrue();
            AssertThat(RuntimeConfig.ReadValues(config)["android_skip_launcher"]).IsEqual("true");
            AssertThat(RuntimeConfig.ReadValues(config)["max_fps"]).IsEqual("30");
            AndroidQuickSettings.SetSkipLauncher(config, false);
            AssertThat(AndroidQuickSettings.SkipLauncher(config)).IsFalse();
            AssertThat(AndroidQuickSettings.SkipLauncherLabel(true)).IsEqual("Skip launcher: On");
            AssertThat(AndroidQuickSettings.SkipLauncherLabel(false)).IsEqual("Skip launcher: Off");
        }
        finally { File.Delete(config); }
    }

    [TestCase]
    public void TestVibrationStepsThroughStrengthsAndOff()
    {
        // Absent is half strength, as the launcher's defaults; a tap steps 100, 75, 50, 25, off and
        // keeps the rest of the file.
        string config = Config("max_fps = 30\n");
        try
        {
            AssertThat(AndroidQuickSettings.VibrationLevel(config)).IsEqual(50);
            AssertThat(AndroidQuickSettings.NextVibrationLevel(100)).IsEqual(75);
            AssertThat(AndroidQuickSettings.NextVibrationLevel(25)).IsEqual(0);
            AssertThat(AndroidQuickSettings.NextVibrationLevel(0)).IsEqual(100);
            AssertThat(AndroidQuickSettings.NextVibrationLevel(60)).IsEqual(100);
            AndroidQuickSettings.SetVibrationLevel(config, 50);
            var values = RuntimeConfig.ReadValues(config);
            // The keys GameInput reads for its rumble, joypad and phone alike.
            AssertThat(values["controller_vibration"]).IsEqual("true");
            AssertThat(values["vibration_strength"]).IsEqual("50");
            AssertThat(values["max_fps"]).IsEqual("30");
            AssertThat(AndroidQuickSettings.VibrationLabel(50)).IsEqual("Vibration: 50%");
            // Off is the switch; the strength stays for when it is turned back on.
            AndroidQuickSettings.SetVibrationLevel(config, 0);
            values = RuntimeConfig.ReadValues(config);
            AssertThat(values["controller_vibration"]).IsEqual("false");
            AssertThat(values["vibration_strength"]).IsEqual("50");
            AssertThat(AndroidQuickSettings.VibrationLevel(config)).IsEqual(0);
            AssertThat(AndroidQuickSettings.VibrationLabel(0)).IsEqual("Vibration: Off");
            AndroidQuickSettings.SetVibrationLevel(config, 100);
            AssertThat(AndroidQuickSettings.VibrationLevel(config)).IsEqual(100);
        }
        finally { File.Delete(config); }
    }

    private static AndroidGraphicsOption Row(string key) => System.Array.Find(AndroidGraphicsOptions.All, o => o.Key == key)!;

    [TestCase]
    public void TestGraphicsRowsStepAndStopAtTheirEnds()
    {
        AndroidGraphicsOption scale = Row("render_scale_percent");
        AssertThat(AndroidGraphicsOptions.Step(scale, "50", 1)).IsEqual("60");
        AssertThat(AndroidGraphicsOptions.Step(scale, "50", -1)).IsEqual("45");
        AssertThat(AndroidGraphicsOptions.Step(scale, "100", 1)).IsEqual("100");
        AssertThat(AndroidGraphicsOptions.CanStep(scale, "100", 1)).IsFalse();
        AssertThat(AndroidGraphicsOptions.CanStep(scale, "30", -1)).IsFalse();
        // A hand-set value moves from the step nearest it, and either way it is off the steps.
        AssertThat(AndroidGraphicsOptions.Step(scale, "52", 1)).IsEqual("60");
        AssertThat(AndroidGraphicsOptions.CanStep(scale, "101", 1)).IsTrue();
        AssertThat(AndroidGraphicsOptions.Label(scale, "67")).IsEqual("67%");
        // Each row's own words: All, Off, the detail names, booleans.
        AssertThat(AndroidGraphicsOptions.Label(Row("placed_light_limit"), "0")).IsEqual("All");
        AssertThat(AndroidGraphicsOptions.Label(Row("sun_shadow_distance"), "0")).IsEqual("Off");
        AssertThat(AndroidGraphicsOptions.Label(Row("sun_shadow_distance"), "120")).IsEqual("120 m");
        AssertThat(AndroidGraphicsOptions.Label(Row("mesh_lod_pixels"), "2")).IsEqual("High");
        AssertThat(AndroidGraphicsOptions.Label(Row("mesh_lod_pixels"), "3")).IsEqual("Medium");
        AssertThat(AndroidGraphicsOptions.Label(Row("prop_cull_distance_scale"), "0.6")).IsEqual("60%");
        AssertThat(AndroidGraphicsOptions.Label(Row("placed_light_shadows"), "true")).IsEqual("On");
        AssertThat(AndroidGraphicsOptions.Step(Row("placed_light_shadows"), "false", 1)).IsEqual("true");
        AssertThat(AndroidGraphicsOptions.Label(Row("sun_shadow_filter"), "0")).IsEqual("Off");
        AssertThat(AndroidGraphicsOptions.Label(Row("sun_shadow_filter"), "2")).IsEqual("Medium");
        AssertThat(AndroidGraphicsOptions.Label(Row("sun_shadow_map"), "2048")).IsEqual("Medium");
        AssertThat(AndroidGraphicsOptions.Step(Row("sun_shadow_map"), "1024", 1)).IsEqual("2048");
        // Each row says how much it costs: the render scale and sun shadows most, bloom least.
        AssertThat(Row("render_scale_percent").Impact).IsEqual(AndroidGraphicsImpact.High);
        AssertThat(Row("sun_shadow_distance").Impact).IsEqual(AndroidGraphicsImpact.High);
        AssertThat(Row("bloom_scale").Impact).IsEqual(AndroidGraphicsImpact.Low);
        AssertThat(AndroidGraphicsOptions.ImpactLabel(AndroidGraphicsImpact.Medium)).IsEqual("Perf impact: Medium");
    }

    [TestCase]
    public void TestLinkedRowsWriteThePresetsOwnCombinations()
    {
        // Distant detail moves the trees' and the ground's coarser levels with the models', as each preset does.
        AssertThat(AndroidGraphicsOptions.Writes(Row("mesh_lod_pixels"), "4"))
            .ContainsExactly(("mesh_lod_pixels", "4"), ("tree_lod_bias", "0.5"), ("terrain_lod_bias", "0.5"));
        AssertThat(AndroidGraphicsOptions.Writes(Row("mesh_lod_pixels"), "1"))
            .ContainsExactly(("mesh_lod_pixels", "1"), ("tree_lod_bias", "1"), ("terrain_lod_bias", "1"));
        // The sun shadow's quality and softness change nothing while the shadow distance is 0.
        AssertThat(AndroidGraphicsOptions.Applies(Row("sun_shadow_filter"), _ => "0")).IsFalse();
        AssertThat(AndroidGraphicsOptions.Applies(Row("sun_shadow_map"), _ => "80")).IsTrue();
        AssertThat(AndroidGraphicsOptions.Applies(Row("render_scale_percent"), _ => "0")).IsTrue();
        // A value set by hand off the steps writes only the row's own key.
        AssertThat(AndroidGraphicsOptions.Writes(Row("mesh_lod_pixels"), "2.5")).ContainsExactly(("mesh_lod_pixels", "2.5"));
        AssertThat(AndroidGraphicsOptions.Writes(Row("sun_shadow_distance"), "120"))
            .ContainsExactly(("sun_shadow_distance", "120"), ("sun_shadow_reach_scale", "1"));
        AssertThat(AndroidGraphicsOptions.Writes(Row("placed_light_limit"), "32"))
            .ContainsExactly(("placed_light_limit", "32"), ("placed_light_range", "65.0"));

        // Every preset's value of a row is one of its steps, with the linked settings that step writes, so
        // stepping a row back to a preset's value makes that preset again.
        foreach (string preset in AndroidQuickSettings.Presets)
        {
            IReadOnlyDictionary<string, string> values = GraphicsPresets.Values(preset)!;
            foreach (AndroidGraphicsOption option in AndroidGraphicsOptions.All)
            {
                if (!values.TryGetValue(option.Key, out string? value)) continue;
                AssertThat(option.Steps.Any(step => AsNumber(step)
                        == AsNumber(value))).OverrideFailureMessage($"{preset}: {option.Key} = {value} is not a step").IsTrue();
                foreach ((string key, string written) in AndroidGraphicsOptions.Writes(option, value))
                {
                    AssertThat(AsNumber(values[key])).OverrideFailureMessage($"{preset}: {option.Name} writes {key} = {written}")
                        .IsEqual(AsNumber(written));
                }
            }
        }
    }

    private static double AsNumber(string value) => bool.TryParse(value, out bool on)
        ? (on ? 1 : 0)
        : double.Parse(value.Trim('"'), System.Globalization.CultureInfo.InvariantCulture);

    [TestCase]
    public void TestAPhoneNamesItsSettingsInAPlayersWords()
    {
        bool offered = GraphicsPresets.Offered;
        try
        {
            GraphicsPresets.Offered = true;
            RuntimeCVarDefinition scale = RuntimeCVarSet.All.First(d => d.Name == "render_scale_percent");
            AssertThat(RuntimeSettingsModel.FormatDisplayName(scale)).IsEqual("Resolution");
            AssertThat(RuntimeSettingsModel.FormatDescription(scale)).StartsWith("How sharp the world is drawn");
            // Every setting a preset decides has a phone name, and none of the names is a key's dev words.
            foreach (string key in GraphicsPresets.Members)
            {
                string? name = AndroidSettingNames.Name(key);
                AssertThat(name).OverrideFailureMessage(key + " has no phone name").IsNotNull();
                foreach (string word in new[] { "LOD", "Prop", "Entity", "Fixture", "Atlas", "Cull", "Lite", "Android", "_" })
                {
                    AssertThat(name!.Contains(word, System.StringComparison.Ordinal))
                        .OverrideFailureMessage($"{key}: \"{name}\" says {word}").IsFalse();
                }
            }

            GraphicsPresets.Offered = false;
            AssertThat(RuntimeSettingsModel.FormatDisplayName(scale)).IsEqual("Render scale percent");
        }
        finally { GraphicsPresets.Offered = offered; }
    }

    [TestCase]
    public void TestAGraphicsRowMakesThePresetCustomAndThePresetMovesTheRows()
    {
        string config = Config("max_fps = 30\n");
        try
        {
            AndroidQuickSettings.ApplyPreset(config, GraphicsPresets.High, (_, _) => { });
            AssertThat(AndroidQuickSettings.Value(config, "render_scale_percent")).IsEqual("50");
            // A row goes to the file and the game, and the preset reads Custom, stored as the launcher stores it.
            var live = new Dictionary<string, string>();
            AndroidQuickSettings.SetGraphics(config, "render_scale_percent", "67", (k, v) => live[k] = v);
            AssertThat(live["render_scale_percent"]).IsEqual("67");
            AssertThat(AndroidQuickSettings.Value(config, "render_scale_percent")).IsEqual("67");
            AssertThat(AndroidQuickSettings.Preset(config)).IsEqual(GraphicsPresets.Custom);
            AssertThat(RuntimeConfig.ReadValues(config)[GraphicsPresets.Key].Trim('"')).IsEqual(GraphicsPresets.Custom);
            // Back to the preset's value, it is that preset again.
            AndroidQuickSettings.SetGraphics(config, "render_scale_percent", "50", (_, _) => { });
            AssertThat(AndroidQuickSettings.Preset(config)).IsEqual(GraphicsPresets.High);
            // Stepping the preset moves every row it sets; the rest (bloom) and the cap stay.
            AndroidQuickSettings.SetGraphics(config, "bloom_scale", "0.5", (_, _) => { });
            AndroidQuickSettings.ApplyPreset(config, GraphicsPresets.Low, (_, _) => { });
            AssertThat(AndroidQuickSettings.Value(config, "render_scale_percent")).IsEqual("40");
            AssertThat(AndroidQuickSettings.Value(config, "sun_shadow_distance")).IsEqual("0");
            AssertThat(AndroidQuickSettings.Value(config, "placed_light_limit")).IsEqual("4");
            AssertThat(AndroidQuickSettings.Value(config, "bloom_scale")).IsEqual("0.5");
            AssertThat(AndroidQuickSettings.FrameCap(config)).IsEqual(30);
            AssertThat(AndroidQuickSettings.Value(config, "placed_light_shadows")).IsEqual("false");
        }
        finally { File.Delete(config); }

        // An absent setting reads as its default.
        string bare = Config("max_fps = 30\n");
        try { AssertThat(AndroidQuickSettings.Value(bare, "placed_light_shadows")).IsEqual("true"); }
        finally { File.Delete(bare); }
    }
}
