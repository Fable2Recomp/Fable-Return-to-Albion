using System;
using System.IO;
using GdUnit4;
using Refii.AndroidRuntime;
using Refii.Launcher.Core;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>Shared desktop settings and Android installation paths across first launch, edits and restart.</summary>
[TestSuite]
public sealed class AndroidLauncherSettingsTests
{
    /// <summary>The graphics preset is written only where it is offered: a phone.</summary>
    [BeforeTest]
    public void StandInForAPhone() => GraphicsPresets.Offered = true;

    [AfterTest]
    public void BackToThisPlatform() => GraphicsPresets.Offered = Refii.Core.Engine_Platform.IsAndroid;

    /// <summary>Automatic native paths follow the selected game while user options and explicit paths survive shared-panel rewrites.</summary>
    [TestCase]
    public void DesktopSettingsAndroidBootLifecycle()
    {
        string root = Path.Combine(Path.GetTempPath(), "refii-android-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string config = Path.Combine(root, "refii.toml");
            string game = Path.Combine(root, "prepared-game");
            Directory.CreateDirectory(game);
            File.WriteAllText(config, "max_fps = 0\nsun_shadow_map = 1024\nsun_shadow_splits = 2\nmaterials_modernized = false\nlog_file = \"\"\n");
            AndroidLauncherSettings.Prepare(root, game);
            AssertThat(RuntimeConfig.EnsureDefaults(config, out _)).IsTrue();
            var values = RuntimeConfig.ReadValues(config);
            AssertThat(values["log_file"]).IsEqual(Path.Combine(root, "logs", "refii.log"));
            AssertThat(Path.GetFullPath(values["game_data_root"])).IsEqual(game);
            // The first start gives a configuration that never chose a graphics preset the
            // Android one and the 30 FPS cap; other settings outside it keep their seeded values.
            AssertThat(values[GraphicsPresets.Key]).IsEqual("android");
            AssertThat(values["max_fps"]).IsEqual("30");
            AssertThat(values["sun_shadow_splits"]).IsEqual("2");
            AssertThat(values["materials_modernized"]).IsEqual("false");
            values["sun_shadow_map"] = "2048";
            values["log_file"] = Path.Combine(root, "custom.log");
            AssertThat(RuntimeConfig.WriteValues(config, values, out _)).IsTrue();
            byte[] saved = File.ReadAllBytes(config);
            AndroidLauncherSettings.Prepare(root, game);
            AssertThat(File.ReadAllBytes(config).AsSpan().SequenceEqual(saved)).IsTrue();
            AssertThat(RuntimeConfig.ReadValues(config)["sun_shadow_map"]).IsEqual("2048");
            // A full desktop defaults pass can expand an automatic data path into
            // <runtime>/assets. Android must still select the actual extracted ISO.
            values["game_data_root"] = Path.Combine(root, "assets");
            values["log_file"] = "";
            AssertThat(RuntimeConfig.WriteValues(config, values, out _)).IsTrue();
            AndroidLauncherSettings.Prepare(root, game);
            AssertThat(Path.GetFullPath(RuntimeConfig.ReadValues(config)["game_data_root"])).IsEqual(game);
            AssertThat(RuntimeConfig.ReadValues(config)["log_file"]).IsEqual(Path.Combine(root, "logs", "refii.log"));
            values = RuntimeConfig.ReadValues(config);
            values["game_data_root"] = Path.Combine(Path.GetDirectoryName(root)!, "phone-installs", "install-" + Guid.NewGuid().ToString("N"), "game");
            AssertThat(RuntimeConfig.WriteValues(config, values, out _)).IsTrue();
            AndroidLauncherSettings.Prepare(root, game);
            AssertThat(Path.GetFullPath(RuntimeConfig.ReadValues(config)["game_data_root"])).IsEqual(game);
        }
        finally { Directory.Delete(root, true); }
    }
}
