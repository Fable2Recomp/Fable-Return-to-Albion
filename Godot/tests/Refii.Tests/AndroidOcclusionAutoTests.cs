using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using GdUnit4;
using Godot;
using Refii.AndroidRuntime;
using Refii.Launcher.Core;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>
/// `occlusion_culling = auto` (2026-11-16): the buildings' occluders are built as with true, but the
/// viewport culls only while one is near the camera. Off by default; auto in every preset since revision 24. The
/// occlusion buffer's settings beside it are in no preset.
/// </summary>
[TestSuite]
public sealed class AndroidOcclusionAutoTests
{
    private static string File(string resPath, [CallerFilePath] string source = "") =>
        System.IO.File.ReadAllText(Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(source)!, "../..", resPath["res://".Length..]))).Replace("\r\n", "\n");

    private static readonly string[] Keys =
    {
        AndroidOcclusionAuto.Key, AndroidOcclusionAuto.MetresKey, AndroidOcclusionAuto.RaysKey, AndroidOcclusionAuto.BuildQualityKey,
    };

    [TestCase]
    public void KeysKeepTodaysDefaultsAreNamedAndOnlyTheModeIsInThePresets()
    {
        RuntimeCVarDefinition mode = RuntimeCVarsGodot.Definitions.Single(d => d.Name == AndroidOcclusionAuto.Key);
        AssertThat(mode.Type).IsEqual(RuntimeCVarType.Bool);
        AssertThat(mode.DefaultValue).IsEqual("false");
        AssertThat(RuntimeCVarsAndroid.PhoneOcclusionChoices).IsEqual("false|Off,true|On,auto|Auto");
        AssertThat(RuntimeCVarsGodot.Definitions.Single(d => d.Name == AndroidOcclusionAuto.MetresKey).DefaultValue).IsEqual("50");
        AssertThat(RuntimeCVarsGodot.Definitions.Single(d => d.Name == AndroidOcclusionAuto.RaysKey).DefaultValue)
            .IsEqual(AndroidOcclusionAuto.DefaultRays.ToString());
        AssertThat(RuntimeCVarsGodot.Definitions.Single(d => d.Name == AndroidOcclusionAuto.BuildQualityKey).DefaultValue)
            .IsEqual(AndroidOcclusionAuto.DefaultBuildQuality.ToString());

        foreach (string key in Keys)
        {
            AssertThat(AndroidSettingNames.Keys.Contains(key)).IsTrue();
            foreach (string preset in new[] { GraphicsPresets.Low, GraphicsPresets.Medium, GraphicsPresets.High, GraphicsPresets.Ultra })
            {
                // The mode is auto in every preset (revision 24); the buffer's own settings are in none.
                if (key == AndroidOcclusionAuto.Key)
                {
                    AssertThat(GraphicsPresets.Values(preset)![key]).OverrideFailureMessage(preset).IsEqual("auto");
                }
                else
                {
                    AssertThat(GraphicsPresets.Values(preset)!.ContainsKey(key)).OverrideFailureMessage(key + " in " + preset).IsFalse();
                }
            }
        }
    }

    [TestCase]
    public void DesktopKeepsUpstreamsOnOffRow()
    {
        // These tests run on desktop: the row offers upstream's bool toggle, and the buffer's rows are hidden.
        RuntimeCVarDefinition row = RuntimeCVarsGodot.Definitions.Single(d => d.Name == AndroidOcclusionAuto.Key);
        AssertThat(row.Type).IsEqual(RuntimeCVarType.Bool);
        AssertThat(row.AllowedValues).IsNull();
        AssertThat(row.DefaultValue).IsEqual("false");
        AssertThat(row.Description).EndsWith("Read at launch.");
        AssertThat(AndroidLauncherSettings.Shows(AndroidOcclusionAuto.Key)).IsTrue();
        foreach (string key in Keys.Skip(1))
        {
            AssertThat(AndroidLauncherSettings.Shows(key)).OverrideFailureMessage(key).IsFalse();
        }

        var auto = new Dictionary<string, string> { [AndroidOcclusionAuto.Key] = "auto" };
        var on = new Dictionary<string, string> { [AndroidOcclusionAuto.Key] = "true" };
        AssertThat(AndroidOcclusionAuto.ModeFor(auto, android: false)).IsEqual(AndroidOcclusionAuto.Mode.Off);
        AssertThat(AndroidOcclusionAuto.ModeFor(on, android: false)).IsEqual(AndroidOcclusionAuto.Mode.On);
        AssertThat(AndroidOcclusionAuto.ModeFor(auto, android: true)).IsEqual(AndroidOcclusionAuto.Mode.Auto);
        AssertThat(AndroidOcclusionAuto.ModeFor(new Dictionary<string, string>(), android: true)).IsEqual(AndroidOcclusionAuto.Mode.Off);
    }

    [TestCase]
    public void ConfigFormattingKeepsBoolToggleAndAutoValue()
    {
        RuntimeCVarDefinition row = RuntimeCVarsGodot.Definitions.Single(d => d.Name == AndroidOcclusionAuto.Key);
        AssertThat(RuntimeConfig.FormatTomlValue(row, "false")).IsEqual("false");
        AssertThat(RuntimeConfig.FormatTomlValue(row, "true")).IsEqual("true");
        AssertThat(RuntimeConfig.FormatTomlValue(row, "auto")).IsEqual("\"auto\"");
    }

    [TestCase]
    public void ModeReadsTrueFalseAndAuto()
    {
        AssertThat(AndroidOcclusionAuto.Parse("true")).IsEqual(AndroidOcclusionAuto.Mode.On);
        AssertThat(AndroidOcclusionAuto.Parse("1")).IsEqual(AndroidOcclusionAuto.Mode.On);
        AssertThat(AndroidOcclusionAuto.Parse("\"TRUE\"")).IsEqual(AndroidOcclusionAuto.Mode.On);
        AssertThat(AndroidOcclusionAuto.Parse("auto")).IsEqual(AndroidOcclusionAuto.Mode.Auto);
        AssertThat(AndroidOcclusionAuto.Parse(" Auto ")).IsEqual(AndroidOcclusionAuto.Mode.Auto);
        AssertThat(AndroidOcclusionAuto.Parse("false")).IsEqual(AndroidOcclusionAuto.Mode.Off);
        AssertThat(AndroidOcclusionAuto.Parse("0")).IsEqual(AndroidOcclusionAuto.Mode.Off);
        AssertThat(AndroidOcclusionAuto.Parse("")).IsEqual(AndroidOcclusionAuto.Mode.Off);
        AssertThat(AndroidOcclusionAuto.Parse(null)).IsEqual(AndroidOcclusionAuto.Mode.Off);
        AssertThat(AndroidOcclusionAuto.Parse("maybe")).IsEqual(AndroidOcclusionAuto.Mode.Off);
    }

    [TestCase]
    public void AutoSwitchesOnNearAndOffPastHalfAgainFurther()
    {
        // Off: on at the distance, not beyond it.
        AssertThat(AndroidOcclusionAuto.Decide(false, 50f, 50f)).IsTrue();
        AssertThat(AndroidOcclusionAuto.Decide(false, 60f, 50f)).IsFalse();
        AssertThat(AndroidOcclusionAuto.Decide(false, float.PositiveInfinity, 50f)).IsFalse();
        // On: stays on in the band, off past 1.5 times.
        AssertThat(AndroidOcclusionAuto.Decide(true, 60f, 50f)).IsTrue();
        AssertThat(AndroidOcclusionAuto.Decide(true, 75f, 50f)).IsTrue();
        AssertThat(AndroidOcclusionAuto.Decide(true, 76f, 50f)).IsFalse();
        AssertThat(AndroidOcclusionAuto.Decide(true, float.PositiveInfinity, 50f)).IsFalse();
    }

    [TestCase]
    public void DistanceIsToTheBoxsNearestPoint()
    {
        var box = new Aabb(new Vector3(0, 0, 0), new Vector3(10, 10, 10));
        AssertThat(AndroidOcclusionAuto.Distance(box, new Vector3(5, 5, 5))).IsEqual(0f);
        AssertThat(Mathf.Abs(AndroidOcclusionAuto.Distance(box, new Vector3(13, 5, 5)) - 3f) < 1e-4f).IsTrue();
        AssertThat(Mathf.Abs(AndroidOcclusionAuto.Distance(box, new Vector3(13, 14, 5)) - 5f) < 1e-4f).IsTrue();
    }

    [TestCase]
    public void BufferSettingsDefaultToGodotsAndClamp()
    {
        var none = new Dictionary<string, string>();
        AssertThat(AndroidOcclusionAuto.Metres(none)).IsEqual(AndroidOcclusionAuto.DefaultMetres);
        AssertThat(AndroidOcclusionAuto.Rays(none)).IsEqual(512);
        AssertThat(AndroidOcclusionAuto.BuildQuality(none)).IsEqual(2);

        var set = new Dictionary<string, string>
        {
            [AndroidOcclusionAuto.MetresKey] = "1000",
            [AndroidOcclusionAuto.RaysKey] = "8",
            [AndroidOcclusionAuto.BuildQualityKey] = "7",
        };
        AssertThat(AndroidOcclusionAuto.Metres(set)).IsEqual(500f);
        AssertThat(AndroidOcclusionAuto.Rays(set)).IsEqual(32);
        AssertThat(AndroidOcclusionAuto.BuildQuality(set)).IsEqual(2);

        set[AndroidOcclusionAuto.MetresKey] = "30";
        set[AndroidOcclusionAuto.RaysKey] = "256";
        set[AndroidOcclusionAuto.BuildQualityKey] = "0";
        AssertThat(AndroidOcclusionAuto.Metres(set)).IsEqual(30f);
        AssertThat(AndroidOcclusionAuto.Rays(set)).IsEqual(256);
        AssertThat(AndroidOcclusionAuto.BuildQuality(set)).IsEqual(0);
    }

    [TestCase]
    public void SharedHooksAreInPlace()
    {
        string occluders = File("res://src/Engine/level/props/Engine_BuildingOccluders.cs");
        AssertThat(occluders).Contains("Enabled => _enabled ??= Refii.AndroidRuntime.AndroidOcclusionAuto.Builds;");
        AssertThat(occluders).Contains("Refii.AndroidRuntime.AndroidOcclusionAuto.Tick();");
        AssertThat(occluders).Contains("Refii.AndroidRuntime.AndroidOcclusionAuto.Track(instance, mesh, placements);");
        AssertThat(occluders).Contains("if (!Refii.AndroidRuntime.AndroidOcclusionAuto.Auto && instance.GetViewport() is { UseOcclusionCulling: false } viewport)");
        AssertThat(occluders).Contains("Refii.AndroidRuntime.AndroidOcclusionAuto.Clear();");
        AssertThat(File("res://src/launcher/settings/RuntimeCVarSet.cs")).Contains("..RuntimeCVarsAndroid.OcclusionCulling,");
    }
}
