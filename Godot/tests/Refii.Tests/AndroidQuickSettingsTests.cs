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
}
