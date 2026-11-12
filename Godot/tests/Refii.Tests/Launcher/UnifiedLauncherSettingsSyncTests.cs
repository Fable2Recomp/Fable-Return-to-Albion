namespace Refii.Tests.Launcher;

using System;
using System.Collections.Generic;
using System.IO;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Core;
using Refii.Launcher.Core;

/// <summary>
/// Verifies dual-TOML settings synchronization in <see cref="RuntimeSettingsModel"/>:
/// shared engine CVars synchronize across <c>refii.toml</c> and <c>refiii.toml</c>,
/// per-title roots remain strictly isolated, and title-owned CVars never leak across titles.
/// </summary>
[TestSuite]
public class UnifiedLauncherSettingsSyncTests
{
    private string _tempDir = string.Empty;
    private Engine_Title _savedActive = Engine_Title.Fable2;

    [BeforeTest]
    public void SetUp()
    {
        _savedActive = Engine_Title.Active;
        _tempDir = Path.Combine(Refii.Tests.TestTemp.Root, "refii_unified_settings_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [AfterTest]
    public void TearDown()
    {
        Engine_Title.Active = _savedActive;
        RuntimeCVarSet.Refresh();
        if (_tempDir.Length != 0 && Directory.Exists(_tempDir))
        {
            try
            {
                Directory.Delete(_tempDir, recursive: true);
            }
            catch (Exception)
            {
            }
        }
    }

    [TestCase]
    public void TestSharedSettingsSyncAcrossBothTomlFilesWhileTitleOwnedAndRootsStayIsolated()
    {
        string f2Path = Path.Combine(_tempDir, "refii.toml");
        string f3Path = Path.Combine(_tempDir, "refiii.toml");
        string f2GameRoot = Path.Combine(_tempDir, "assets", "Fable2").Replace('\\', '/');
        string f3GameRoot = Path.Combine(_tempDir, "assets", "Fable3").Replace('\\', '/');
        Directory.CreateDirectory(f2GameRoot);
        Directory.CreateDirectory(f3GameRoot);

        File.WriteAllText(f2Path,
            $"game_data_root = \"{f2GameRoot}\"\n" +
            "display_resolution = \"1920x1080\"\n" +
            "decorate_mode_enabled = false\n" +
            "padmap_p2_a = \"A\"\n" +
            "vfs_trace_vfs = false\n");

        File.WriteAllText(f3Path,
            $"game_data_root = \"{f3GameRoot}\"\n" +
            "display_resolution = \"1280x720\"\n");

        var model = new RuntimeSettingsModel();
        model.ReloadFromPaths(f2Path, f3Path, ensureDefaults: false);

        // Mutate shared settings (display, performance preset, keybinding) and Fable 2-owned settings:
        model.Values["display_resolution"] = "2560x1440";
        model.ApplyDesktopPreset(DesktopGraphicsPresets.High);
        model.Values["key_sprint"] = "Shift";
        model.Values["decorate_mode_enabled"] = "true";
        model.Values["padmap_p2_a"] = "B";
        model.Values["vfs_trace_vfs"] = "true";
        model.Save();

        SortedDictionary<string, string> f2Saved = RuntimeConfig.ReadValues(f2Path);
        SortedDictionary<string, string> f3Saved = RuntimeConfig.ReadValues(f3Path);

        // Shared settings synced to both files:
        AssertThat(f2Saved["display_resolution"]).IsEqual("2560x1440");
        AssertThat(f3Saved["display_resolution"]).IsEqual("2560x1440");
        AssertThat(f2Saved[DesktopGraphicsPresets.Key]).IsEqual(DesktopGraphicsPresets.High);
        AssertThat(f3Saved[DesktopGraphicsPresets.Key]).IsEqual(DesktopGraphicsPresets.High);
        AssertThat(f2Saved["key_sprint"]).IsEqual("Shift");
        AssertThat(f3Saved["key_sprint"]).IsEqual("Shift");

        // Per-title roots remain strictly isolated:
        AssertThat(f2Saved["game_data_root"]).IsEqual(f2GameRoot);
        AssertThat(f3Saved["game_data_root"]).IsEqual(f3GameRoot);

        // Fable 2-owned settings saved in refii.toml, never leaked into refiii.toml:
        AssertThat(f2Saved["decorate_mode_enabled"]).IsEqual("true");
        AssertThat(f2Saved["padmap_p2_a"]).IsEqual("B");
        AssertThat(f2Saved["vfs_trace_vfs"]).IsEqual("true");

        AssertThat(f3Saved.ContainsKey("decorate_mode_enabled")).IsFalse();
        AssertThat(f3Saved.ContainsKey("padmap_p2_a")).IsFalse();
        AssertThat(f3Saved.ContainsKey("vfs_trace_vfs")).IsFalse();
    }

    [TestCase]
    public void TestSavingWhenFable3TomlDoesNotExistYetCreatesItWithoutCopyingFable2Root()
    {
        string f2Path = Path.Combine(_tempDir, "refii.toml");
        string f3Path = Path.Combine(_tempDir, "refiii.toml");
        string customF2Root = Path.Combine(_tempDir, "custom_fable2_disc").Replace('\\', '/');
        Directory.CreateDirectory(customF2Root);

        File.WriteAllText(f2Path, $"game_data_root = \"{customF2Root}\"\nmax_fps = 60\n");

        var model = new RuntimeSettingsModel();
        model.ReloadFromPaths(f2Path, f3Path, ensureDefaults: false);
        model.Values["max_fps"] = "120";
        model.Save();

        AssertThat(File.Exists(f3Path)).IsTrue();
        SortedDictionary<string, string> f2Saved = RuntimeConfig.ReadValues(f2Path);
        SortedDictionary<string, string> f3Saved = RuntimeConfig.ReadValues(f3Path);

        AssertThat(f2Saved["max_fps"]).IsEqual("120");
        AssertThat(f3Saved["max_fps"]).IsEqual("120");
        AssertThat(f2Saved["game_data_root"]).IsEqual(customF2Root);
        AssertThat(f3Saved["game_data_root"]).IsNotEqual(customF2Root);
    }

    [TestCase]
    public void TestResetTabToDefaultsSyncsResetAcrossBothTitleConfigs()
    {
        string f2Path = Path.Combine(_tempDir, "refii.toml");
        string f3Path = Path.Combine(_tempDir, "refiii.toml");

        var model = new RuntimeSettingsModel();
        model.ReloadFromPaths(f2Path, f3Path, ensureDefaults: false);
        model.Values["max_fps"] = "45";
        model.Save();

        AssertThat(RuntimeConfig.ReadValues(f2Path)["max_fps"]).IsEqual("45");
        AssertThat(RuntimeConfig.ReadValues(f3Path)["max_fps"]).IsEqual("45");

        model.ResetTabToDefaults(SettingsTab.Display);

        AssertThat(RuntimeConfig.ReadValues(f2Path)["max_fps"]).IsEqual("0");
        AssertThat(RuntimeConfig.ReadValues(f3Path)["max_fps"]).IsEqual("0");
    }
}
