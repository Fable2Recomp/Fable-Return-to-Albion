namespace Refii.Tests.Launcher;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.AndroidRuntime;
using Refii.Launcher.Core;

/// <summary>
/// The phone in upstream's tabbed launcher settings (<see cref="AndroidSettingsLayout"/>): what a player sets
/// in each tab's main list, everything else under Advanced, the preset card's line, sliders that take the
/// phone's values, resets to the phone's preset, and the titles' files kept in step
/// (<see cref="AndroidLauncherSettings.ShareWithOtherTitles"/>).
/// </summary>
[TestSuite]
public sealed class AndroidSettingsLayoutTests
{
    [BeforeTest]
    public void StandInForAPhone() => GraphicsPresets.Offered = true;

    [AfterTest]
    public void BackToThisPlatform() => GraphicsPresets.Offered = Refii.Core.Engine_Platform.IsAndroid;

    /// <summary>A phone leaves the Keys and Debug tabs out; a desktop keeps every tab.</summary>
    [TestCase]
    public void APhoneHidesTheKeysAndDebugTabs()
    {
        AssertThat(AndroidSettingsLayout.HidesTab(SettingsTab.Keybindings)).IsTrue();
        AssertThat(AndroidSettingsLayout.HidesTab(SettingsTab.Developer)).IsTrue();
        AssertThat(AndroidSettingsLayout.HidesTab(SettingsTab.Display)).IsFalse();
        AssertThat(AndroidSettingsLayout.HidesTab(SettingsTab.Graphics)).IsFalse();
        GraphicsPresets.Offered = false;
        AssertThat(AndroidSettingsLayout.HidesTab(SettingsTab.Keybindings)).IsFalse();
        AssertThat(AndroidSettingsLayout.HidesTab(SettingsTab.Developer)).IsFalse();
    }

    private static string NewFolder()
    {
        string root = Path.Combine(Refii.Tests.TestTemp.Root, "android-layout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static RuntimeSettingsModel Model(string root)
    {
        var model = new RuntimeSettingsModel();
        model.ReloadFromPaths(Path.Combine(root, "refii.toml"), Path.Combine(root, "refiii.toml"));
        return model;
    }

    private static IEnumerable<string> Names(List<(string Section, List<RuntimeCVarDefinition> Rows)> sections) =>
        sections.SelectMany(section => section.Rows).Select(row => row.Name);

    /// <summary>Each tab's main list on a phone is what a player sets there; the rest is under Advanced,
    /// and the desktop's window, VSync, upscaler and desktop-preset rows are nowhere.</summary>
    [TestCase]
    public void PhoneTabsListWhatAPlayerSetsAndHideTheDesktopsRows()
    {
        string root = NewFolder();
        try
        {
            RuntimeSettingsModel model = Model(root);
            var essential = new Dictionary<string, SettingsTab>(StringComparer.Ordinal);
            var everywhere = new HashSet<string>(StringComparer.Ordinal);
            foreach (SettingsTab tab in Enum.GetValues<SettingsTab>())
            {
                var (main, advanced) = model.BuildTabSections(tab, includeContextualHidden: true);
                foreach (string name in Names(main))
                {
                    essential[name] = tab;
                }
                everywhere.UnionWith(Names(main));
                everywhere.UnionWith(Names(advanced));
            }

            // The preset is the card above the Display and Graphics lists, not a row.
            foreach (string name in AndroidLauncherSettings.PlayerFacing.Where(name => name != GraphicsPresets.Key))
            {
                AssertThat(essential.ContainsKey(name)).OverrideFailureMessage($"'{name}' is not in a tab's main list").IsTrue();
            }

            foreach ((string name, SettingsTab tab) in essential.Where(pair => pair.Value != SettingsTab.Developer && pair.Value != SettingsTab.Keybindings))
            {
                AssertThat(AndroidLauncherSettings.PlayerFacing.Contains(name))
                    .OverrideFailureMessage($"'{name}' is in {tab}'s main list on a phone").IsTrue();
            }

            AssertThat(essential["fps_counter"]).IsEqual(SettingsTab.Display);
            AssertThat(essential[AndroidBakedShadows.Key]).IsEqual(SettingsTab.Graphics);
            AssertThat(essential[AndroidStartup.SkipVideosKey]).IsEqual(SettingsTab.Gameplay);
            AssertThat(essential["vibration_strength"]).IsEqual(SettingsTab.Controls);
            AssertThat(essential["coop_split_screen_orientation"]).IsEqual(SettingsTab.Gameplay);

            foreach (string desktopOnly in new[] { "fullscreen", "borderless", "monitor", "display_aspect", "display_resolution",
                         "vsync_mode", "render_scaler", "fsr_quality", "msaa", "taa", "rendering_driver", DesktopGraphicsPresets.Key })
            {
                AssertThat(everywhere.Contains(desktopOnly)).OverrideFailureMessage($"'{desktopOnly}' shows on a phone").IsFalse();
            }

            // Searching never offers the preset as a row of its own either.
            AssertThat(model.SearchAll("preset", includeDeveloper: true).SelectMany(group => group.Rows)
                .Any(row => row.Name is GraphicsPresets.Key or DesktopGraphicsPresets.Key)).IsFalse();
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>Every phone-only setting the launcher shows on a phone is in a tab, the phone's tuning in its
    /// own Advanced section, named as the phone names it; none waits for the desktop's lighting dialog,
    /// which does not list them.</summary>
    [TestCase]
    public void PhoneSettingsAreReachableInTheirOwnSection()
    {
        string root = NewFolder();
        try
        {
            RuntimeSettingsModel model = Model(root);
            var listed = new HashSet<string>(StringComparer.Ordinal);
            foreach (SettingsTab tab in Enum.GetValues<SettingsTab>())
            {
                var (main, advanced) = model.BuildTabSections(tab, includeContextualHidden: true);
                listed.UnionWith(Names(main));
                listed.UnionWith(Names(advanced));
            }

            foreach (RuntimeCVarDefinition definition in RuntimeCVarSet.All.Where(d => RuntimeCVarsAndroid.AllKeys.Contains(d.Name)))
            {
                if (!RuntimeConfig.ShouldShowCVar(definition) || definition.Name == GraphicsPresets.Key)
                {
                    continue;
                }

                SettingPresentation meta = DesktopSettingMetadata.Classify(definition);
                AssertThat(meta.HandledByLightingModal).OverrideFailureMessage($"'{definition.Name}' is left to the lighting dialog").IsFalse();
                AssertThat(listed.Contains(definition.Name)).OverrideFailureMessage($"'{definition.Name}' is in no tab").IsTrue();
                if (!AndroidLauncherSettings.PlayerFacing.Contains(definition.Name) && meta.Tab != SettingsTab.Developer)
                {
                    AssertThat(meta.Section).IsEqual(AndroidSettingsLayout.PhoneTuning);
                }
            }

            RuntimeCVarDefinition scale = RuntimeCVarSet.All.First(d => d.Name == "render_scale_percent");
            AssertThat(DesktopSettingMetadata.Label(scale)).IsEqual("Resolution");
            AssertThat(DesktopSettingMetadata.Classify(scale).Summary).IsNull();
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>A desktop's layout is the shared one, untouched.</summary>
    [TestCase]
    public void DesktopLayoutIsTheSharedOne()
    {
        GraphicsPresets.Offered = false;
        foreach (RuntimeCVarDefinition definition in RuntimeCVarSet.All)
        {
            SettingPresentation shared = new(SettingsTab.Display, "Any", SettingTier.Essential, "Any", Range: new NumericRange(50, 200, 5));
            AssertThat(ReferenceEquals(AndroidSettingsLayout.Present(definition, shared), shared)).IsTrue();
        }

        AssertThat(AndroidSettingsLayout.Baseline("render_scale_percent")).IsNull();
        AssertThat(AndroidSettingsLayout.Baseline("max_fps")).IsNull();
        AssertThat(AndroidSettingsLayout.TabDefaults()).IsNull();
        RuntimeCVarDefinition scale = RuntimeCVarSet.All.First(d => d.Name == "render_scale_percent");
        AssertThat(DesktopSettingMetadata.Classify(scale).Range!.Value.Min).IsEqual(50.0);
    }

    /// <summary>Every value a phone preset or an Options row writes fits the launcher's slider for it and lands
    /// on its step, so the launcher shows Low's 40% resolution and Distant detail 4 as they are.</summary>
    [TestCase]
    public void SlidersTakeEveryValueAPhoneWrites()
    {
        var written = new Dictionary<string, HashSet<double>>(StringComparer.Ordinal);
        void Add(string key, string text)
        {
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            {
                if (!written.TryGetValue(key, out HashSet<double>? set)) written[key] = set = new HashSet<double>();
                set.Add(value);
            }
        }

        foreach (string preset in AndroidQuickSettings.Presets)
        {
            foreach ((string key, string value) in GraphicsPresets.Values(preset)!) Add(key, value);
        }
        foreach (AndroidGraphicsOption option in AndroidGraphicsOptions.All)
        {
            foreach (string step in option.Steps) Add(option.Key, step);
            foreach ((string key, string[] values) in option.Linked) foreach (string value in values) Add(key, value);
        }

        int checkedSliders = 0;
        foreach (RuntimeCVarDefinition definition in RuntimeCVarSet.All.Where(d => written.ContainsKey(d.Name)))
        {
            if (DesktopSettingMetadata.Classify(definition).Range is not { } range)
            {
                continue;
            }

            checkedSliders++;
            foreach (double value in written[definition.Name])
            {
                AssertThat(value >= range.Min - 1e-9 && value <= range.Max + 1e-9)
                    .OverrideFailureMessage($"{definition.Name} = {value} is outside {range.Min}..{range.Max}").IsTrue();
                double steps = (value - range.Min) / range.Step;
                AssertThat(Math.Abs(steps - Math.Round(steps)) < 1e-6)
                    .OverrideFailureMessage($"{definition.Name} = {value} is off the slider's {range.Step} step from {range.Min}").IsTrue();
            }
        }

        AssertThat(checkedSliders).IsGreater(3);
        RuntimeCVarDefinition scale = RuntimeCVarSet.All.First(d => d.Name == "render_scale_percent");
        AssertThat(DesktopSettingMetadata.Classify(scale).Range!.Value.Min).IsLessEqual(30.0);
        RuntimeCVarDefinition lod = RuntimeCVarSet.All.First(d => d.Name == "mesh_lod_pixels");
        AssertThat(DesktopSettingMetadata.Classify(lod).Range!.Value.Max).IsGreaterEqual(4.0);
    }

    /// <summary>A phone's row and tab resets go back to the phone's High preset and 30 FPS cap, not the
    /// desktop's defaults: resetting the Display tab leaves the cap at 30 and the resolution at 50%.</summary>
    [TestCase]
    public void ResetsGoBackToThePhonesHighPreset()
    {
        AssertThat(AndroidSettingsLayout.Baseline("max_fps")).IsEqual(GraphicsPresets.AndroidFrameCap);
        AssertThat(AndroidSettingsLayout.Baseline("render_scale_percent")).IsEqual(GraphicsPresets.HighValues["render_scale_percent"]);
        AssertThat(AndroidSettingsLayout.Baseline("camera_invert_x")).IsNull();

        string root = NewFolder();
        try
        {
            RuntimeSettingsModel model = Model(root);
            model.ApplyGraphicsPreset(GraphicsPresets.High);
            model.Values["max_fps"] = "0";
            model.Values["render_scale_percent"] = "85";
            model.ResetTabToDefaults(SettingsTab.Display);
            AssertThat(model.Values["max_fps"]).IsEqual("30");
            AssertThat(model.Values["render_scale_percent"]).IsEqual(GraphicsPresets.HighValues["render_scale_percent"]);
            AssertThat(model.CurrentGraphicsPreset()).IsEqual(GraphicsPresets.High);
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>The preset card's line names what each preset gives, and Custom says why it shows.</summary>
    [TestCase]
    public void PresetCardLineForEveryPreset()
    {
        var lines = AndroidQuickSettings.Presets.Select(AndroidSettingsLayout.Summary).ToList();
        AssertThat(lines.Distinct().Count()).IsEqual(AndroidQuickSettings.Presets.Length);
        AssertThat(lines.All(line => line.Length > 20)).IsTrue();
        AssertThat(AndroidSettingsLayout.Summary(GraphicsPresets.Custom)).Contains("by hand");
        AssertThat(AndroidSettingsLayout.Summary(GraphicsPresets.Low)).Contains("40%");
    }

    /// <summary>
    /// A phone that starts on Fable 3 keeps its preset in the launcher: the first preset written to refiii.toml
    /// reaches refii.toml, which the launcher's settings read first, instead of the launcher creating
    /// refii.toml from the desktop's defaults and saving those over the phone's preset and cap.
    /// </summary>
    [TestCase]
    public void TheOtherTitlesFileIsCreatedWithTheSharedSettings()
    {
        string root = NewFolder();
        try
        {
            string fable3 = Path.Combine(root, "refiii.toml");
            string fable2 = Path.Combine(root, "refii.toml");
            var values = new SortedDictionary<string, string>(StringComparer.Ordinal) { ["game_data_root"] = Path.Combine(root, "f3game") };
            var before = new Dictionary<string, string>(values, StringComparer.Ordinal);
            AssertThat(AndroidLauncherSettings.ApplyPresetOnce(values, "Adreno (TM) 640")).IsTrue();
            AssertThat(RuntimeConfig.WriteValuesForTitle(Refii.Core.Engine_Title.Fable3, fable3, values, out _)).IsTrue();
            AssertThat(AndroidLauncherSettings.ShareWithOtherTitles(fable3, before, values)).IsTrue();

            AssertThat(File.Exists(fable2)).IsTrue();
            var shared = RuntimeConfig.ReadValues(fable2);
            AssertThat(shared[GraphicsPresets.Key]).IsEqual(GraphicsPresets.Low);
            AssertThat(shared["max_fps"]).IsEqual(GraphicsPresets.AndroidFrameCap);
            AssertThat(shared.TryGetValue("game_data_root", out string? root2) && root2 == values["game_data_root"]).IsFalse();

            RuntimeSettingsModel model = Model(root);
            AssertThat(model.CurrentGraphicsPreset()).IsEqual(GraphicsPresets.Low);
            AssertThat(model.Values["max_fps"]).IsEqual(GraphicsPresets.AndroidFrameCap);
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>
    /// Options in the game writes the active title's file; what it changed reaches the other title's, which
    /// the launcher would otherwise show and save back: only the changed shared settings, never a title's own.
    /// </summary>
    [TestCase]
    public void OptionsChangesReachTheOtherTitlesFile()
    {
        string root = NewFolder();
        try
        {
            string fable2 = Path.Combine(root, "refii.toml");
            string fable3 = Path.Combine(root, "refiii.toml");
            var f2 = new SortedDictionary<string, string>(StringComparer.Ordinal);
            GraphicsPresets.Apply(GraphicsPresets.High, f2);
            f2["max_fps"] = "30";
            // As ApplyPresetOnce and RuntimeConfig's migration leave it on a phone.
            f2["placed_light_shadows_world"] = "false";
            AssertThat(RuntimeConfig.WriteValuesForTitle(Refii.Core.Engine_Title.Fable2, fable2, f2, out _)).IsTrue();
            var f3 = new SortedDictionary<string, string>(f2, StringComparer.Ordinal)
            {
                ["camera_invert_x"] = "true",
                ["game_data_root"] = Path.Combine(root, "f3game"),
            };
            AssertThat(RuntimeConfig.WriteValuesForTitle(Refii.Core.Engine_Title.Fable3, fable3, f3, out _)).IsTrue();

            // Fable 3 is being played: Options writes its file.
            var before = RuntimeConfig.ReadValues(fable3);
            var after = new SortedDictionary<string, string>(before, StringComparer.Ordinal)
            {
                ["max_fps"] = "20",
                ["game_data_root"] = Path.Combine(root, "elsewhere"),
            };
            GraphicsPresets.Apply(GraphicsPresets.Medium, after);
            AssertThat(RuntimeConfig.WriteValuesForTitle(Refii.Core.Engine_Title.Fable3, fable3, after, out _)).IsTrue();
            AssertThat(AndroidLauncherSettings.ShareWithOtherTitles(fable3, before, after)).IsTrue();

            var shared = RuntimeConfig.ReadValues(fable2);
            AssertThat(shared["max_fps"]).IsEqual("20");
            AssertThat(GraphicsPresets.Current(shared)).IsEqual(GraphicsPresets.Medium);
            // Unchanged in Fable 3's file, so Fable 2's own choice stays.
            AssertThat(RuntimeConfig.NormalizeBoolValue(shared["camera_invert_x"])).IsEqual("false");
            AssertThat(shared.TryGetValue("game_data_root", out string? f2Root) && f2Root.Contains("elsewhere")).IsFalse();

            // The launcher now shows what Options chose.
            RuntimeSettingsModel model = Model(root);
            AssertThat(model.Values["max_fps"]).IsEqual("20");
            AssertThat(model.CurrentGraphicsPreset()).IsEqual(GraphicsPresets.Medium);
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>The in-game Options writer itself shares: a frame cap set on refii.toml reaches refiii.toml.</summary>
    [TestCase]
    public void QuickSettingsShareTheFrameCap()
    {
        string root = NewFolder();
        try
        {
            string fable2 = Path.Combine(root, "refii.toml");
            string fable3 = Path.Combine(root, "refiii.toml");
            File.WriteAllText(fable2, "max_fps = 30\n");
            File.WriteAllText(fable3, "max_fps = 30\ndeveloper_menu_enabled = false\n");
            AndroidQuickSettings.SetFrameCap(fable2, 40);
            AssertThat(RuntimeConfig.ReadValues(fable3)["max_fps"]).IsEqual("40");

            // A file that is not a title's has no siblings: nothing else is written beside it.
            string other = Path.Combine(root, "other.toml");
            File.WriteAllText(other, "max_fps = 30\n");
            AndroidQuickSettings.SetFrameCap(other, 60);
            AssertThat(RuntimeConfig.ReadValues(fable3)["max_fps"]).IsEqual("40");
        }
        finally { Directory.Delete(root, true); }
    }
}
