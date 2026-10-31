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
    public void TestPresetStepsThroughPerformanceBalancedAndQuality()
    {
        AssertThat(AndroidQuickSettings.NextPreset(GraphicsPresets.Performance)).IsEqual(GraphicsPresets.Balanced);
        AssertThat(AndroidQuickSettings.NextPreset(GraphicsPresets.Balanced)).IsEqual(GraphicsPresets.Quality);
        AssertThat(AndroidQuickSettings.NextPreset(GraphicsPresets.Quality)).IsEqual(GraphicsPresets.Performance);
        AssertThat(AndroidQuickSettings.NextPreset(GraphicsPresets.Custom)).IsEqual(GraphicsPresets.Performance);
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
            AndroidQuickSettings.ApplyPreset(config, GraphicsPresets.Performance, (k, v) => live[k] = v);
            AndroidQuickSettings.SetFrameCap(config, 60);

            // A cap of 60 still reads as Performance, and every preset setting went to the game.
            AssertThat(AndroidQuickSettings.Preset(config)).IsEqual(GraphicsPresets.Performance);
            AssertThat(AndroidQuickSettings.FrameCap(config)).IsEqual(60);
            AssertThat(live.Count).IsEqual(GraphicsPresets.PerformanceValues.Count);
            AssertThat(live["render_scale_percent"]).IsEqual("30");

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
    public void TestVibrationIsTheLaunchersRumbleSwitch()
    {
        // Absent is on, as the launcher's default; a tap flips it and keeps the rest of the file.
        string config = Config("max_fps = 30\n");
        try
        {
            AssertThat(AndroidQuickSettings.Vibration(config)).IsTrue();
            AndroidQuickSettings.SetVibration(config, false);
            var values = RuntimeConfig.ReadValues(config);
            // The key GameInput gates its rumble on, joypad and phone alike.
            AssertThat(values["controller_vibration"]).IsEqual("false");
            AssertThat(values["max_fps"]).IsEqual("30");
            AssertThat(AndroidQuickSettings.Vibration(config)).IsFalse();
            AssertThat(AndroidQuickSettings.VibrationLabel(false)).IsEqual("Vibration: Off");
            AndroidQuickSettings.SetVibration(config, true);
            AssertThat(AndroidQuickSettings.Vibration(config)).IsTrue();
            AssertThat(AndroidQuickSettings.VibrationLabel(true)).IsEqual("Vibration: On");
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
            AssertThat(AndroidQuickSettings.Preset(config)).IsEqual(GraphicsPresets.Balanced);
            // Stepping the preset moves every row it sets; the rest (bloom) and the cap stay.
            AndroidQuickSettings.SetGraphics(config, "bloom_scale", "0.5", (_, _) => { });
            AndroidQuickSettings.ApplyPreset(config, GraphicsPresets.Performance, (_, _) => { });
            AssertThat(AndroidQuickSettings.Value(config, "render_scale_percent")).IsEqual("30");
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
