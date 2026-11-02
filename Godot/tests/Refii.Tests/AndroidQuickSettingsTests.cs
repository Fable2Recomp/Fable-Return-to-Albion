namespace Refii.Tests;

using System;
using System.Collections.Generic;
using System.IO;
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
        string path = Path.Combine(Path.GetTempPath(), "refii-quick-" + Guid.NewGuid().ToString("N") + ".toml");
        File.WriteAllText(path, text);
        return path;
    }

    [TestCase]
    public void TestFrameCapStepsTwentyThirtySixtyUncapped()
    {
        AssertThat(AndroidQuickSettings.NextFrameCap(20)).IsEqual(30);
        AssertThat(AndroidQuickSettings.NextFrameCap(30)).IsEqual(60);
        AssertThat(AndroidQuickSettings.NextFrameCap(60)).IsEqual(0);
        AssertThat(AndroidQuickSettings.NextFrameCap(0)).IsEqual(20);
        // A hand-set cap steps to the first.
        AssertThat(AndroidQuickSettings.NextFrameCap(45)).IsEqual(20);
        AssertThat(AndroidQuickSettings.FrameCapLabel(0)).IsEqual("FPS: Uncapped");
        AssertThat(AndroidQuickSettings.FrameCapLabel(60)).IsEqual("FPS: 60");
    }

    [TestCase]
    public void TestOptionsOffersUltraPerformancePerformanceBalancedAndQuality()
    {
        AssertThat(AndroidQuickSettings.Presets).ContainsExactly(
            GraphicsPresets.UltraPerformance, GraphicsPresets.Performance, GraphicsPresets.Balanced, GraphicsPresets.Quality);
        AssertThat(AndroidQuickSettings.PresetLabel(GraphicsPresets.UltraPerformance)).IsEqual("Preset: Ultra Performance");
        AssertThat(AndroidQuickSettings.PresetLabel(GraphicsPresets.Performance)).IsEqual("Preset: Performance");
        AssertThat(AndroidQuickSettings.PresetLabel(GraphicsPresets.Balanced)).IsEqual("Preset: Balanced");
        AssertThat(AndroidQuickSettings.PresetLabel(GraphicsPresets.Quality)).IsEqual("Preset: Quality");
    }

    [TestCase]
    public void TestCapAndPresetChangeIndependently()
    {
        string config = Config("sun_shadow_map = 1024\nlog_file = \"x.log\"\n");
        try
        {
            var live = new Dictionary<string, string>();
            AndroidQuickSettings.ApplyPreset(config, GraphicsPresets.UltraPerformance, (k, v) => live[k] = v);
            AndroidQuickSettings.SetFrameCap(config, 60);

            // A cap of 60 still reads as Performance, and every preset setting went to the game.
            AssertThat(AndroidQuickSettings.Preset(config)).IsEqual(GraphicsPresets.UltraPerformance);
            AssertThat(AndroidQuickSettings.FrameCap(config)).IsEqual(60);
            AssertThat(live.Count).IsEqual(GraphicsPresets.UltraPerformanceValues.Count);
            AssertThat(live["render_scale_percent"]).IsEqual("40");

            // Switching preset keeps the cap and the settings outside the preset.
            AndroidQuickSettings.ApplyPreset(config, GraphicsPresets.Balanced, (_, _) => { });
            var values = RuntimeConfig.ReadValues(config);
            AssertThat(AndroidQuickSettings.Preset(config)).IsEqual(GraphicsPresets.Balanced);
            AssertThat(values["max_fps"]).IsEqual("60");
            AssertThat(values["sun_shadow_map"]).IsEqual("1024");
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
        AssertThat(AndroidGraphicsOptions.Step(scale, "50", -1)).IsEqual("40");
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
        AssertThat(AndroidGraphicsOptions.Label(Row("mesh_lod_pixels"), "2")).IsEqual("Medium");
        AssertThat(AndroidGraphicsOptions.Label(Row("prop_cull_distance_scale"), "0.6")).IsEqual("60%");
        AssertThat(AndroidGraphicsOptions.Label(Row("placed_light_shadows"), "true")).IsEqual("On");
        AssertThat(AndroidGraphicsOptions.Step(Row("placed_light_shadows"), "false", 1)).IsEqual("true");
        // Each row says how much it costs: the render scale and sun shadows most, bloom least.
        AssertThat(Row("render_scale_percent").Impact).IsEqual(AndroidGraphicsImpact.High);
        AssertThat(Row("sun_shadow_distance").Impact).IsEqual(AndroidGraphicsImpact.High);
        AssertThat(Row("bloom_scale").Impact).IsEqual(AndroidGraphicsImpact.Low);
        AssertThat(AndroidGraphicsOptions.ImpactLabel(AndroidGraphicsImpact.Medium)).IsEqual("Perf impact: Medium");
    }

    [TestCase]
    public void TestAGraphicsRowMakesThePresetCustomAndThePresetMovesTheRows()
    {
        string config = Config("max_fps = 30\n");
        try
        {
            AndroidQuickSettings.ApplyPreset(config, GraphicsPresets.Balanced, (_, _) => { });
            AssertThat(AndroidQuickSettings.Value(config, "render_scale_percent")).IsEqual("60");
            // A row goes to the file and the game, and the preset reads Custom, stored as the launcher stores it.
            var live = new Dictionary<string, string>();
            AndroidQuickSettings.SetGraphics(config, "render_scale_percent", "67", (k, v) => live[k] = v);
            AssertThat(live["render_scale_percent"]).IsEqual("67");
            AssertThat(AndroidQuickSettings.Value(config, "render_scale_percent")).IsEqual("67");
            AssertThat(AndroidQuickSettings.Preset(config)).IsEqual(GraphicsPresets.Custom);
            AssertThat(RuntimeConfig.ReadValues(config)[GraphicsPresets.Key].Trim('"')).IsEqual(GraphicsPresets.Custom);
            // Back to the preset's value, it is that preset again.
            AndroidQuickSettings.SetGraphics(config, "render_scale_percent", "60", (_, _) => { });
            AssertThat(AndroidQuickSettings.Preset(config)).IsEqual(GraphicsPresets.Balanced);
            // Stepping the preset moves every row it sets; the rest (bloom) and the cap stay.
            AndroidQuickSettings.SetGraphics(config, "bloom_scale", "0.5", (_, _) => { });
            AndroidQuickSettings.ApplyPreset(config, GraphicsPresets.UltraPerformance, (_, _) => { });
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
