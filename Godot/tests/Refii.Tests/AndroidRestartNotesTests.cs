namespace Refii.Tests;

using System.Collections.Generic;
using System.Linq;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.AndroidRuntime;
using Refii.Launcher.Core;

[TestSuite]
public class AndroidRestartNotesTests
{
    [TestCase]
    public void TestDescriptionsThatWaitForTheNextStartAreReadInAnyOfTheirWordings()
    {
        AssertThat(AndroidRestartNotes.DescriptionNeedsRestart("Lighter shading. Applies at the next launch.")).IsTrue();
        AssertThat(AndroidRestartNotes.DescriptionNeedsRestart("Lighter shading. Takes effect at the next launch.")).IsTrue();
        AssertThat(AndroidRestartNotes.DescriptionNeedsRestart("Lighter shading. applies on next launch.")).IsTrue();
        AssertThat(AndroidRestartNotes.DescriptionNeedsRestart("Lighter shading. Applies at the next start.")).IsTrue();
        AssertThat(AndroidRestartNotes.DescriptionNeedsRestart("Thinner text. Takes effect the next time the game starts.")).IsTrue();
        AssertThat(AndroidRestartNotes.DescriptionNeedsRestart("Fewer vertices. Applies to models loaded after the next launch.")).IsTrue();
        // The agreed line itself, which descriptions end with.
        AssertThat(AndroidRestartNotes.DescriptionNeedsRestart("Culls behind buildings. " + AndroidRestartNotes.SettingNote)).IsTrue();
        // The occlusion row has the phone's wording on a phone (upstream's on desktop, where these tests run).
        AssertThat(AndroidRestartNotes.DescriptionNeedsRestart(Refii.Launcher.Core.RuntimeCVarsAndroid.PhoneOcclusionText)).IsTrue();
        foreach (string key in new[] { "occlusion_auto_metres", "occlusion_rays_per_thread", "occlusion_bvh_quality" })
        {
            AssertThat(AndroidRestartNotes.NeedsRestart(key)).OverrideFailureMessage(key).IsTrue();
        }
        // Live, at a loading screen, or a passing mention of the next start that is not the setting's own rule.
        AssertThat(AndroidRestartNotes.DescriptionNeedsRestart("Softer picture. Takes effect live.")).IsFalse();
        AssertThat(AndroidRestartNotes.DescriptionNeedsRestart("Fewer plants. Takes effect at the next loading screen.")).IsFalse();
        AssertThat(AndroidRestartNotes.DescriptionNeedsRestart("A phone on a named preset takes its newer values once, on its next start.")).IsFalse();
        AssertThat(AndroidRestartNotes.DescriptionNeedsRestart("")).IsFalse();
        AssertThat(AndroidRestartNotes.DescriptionNeedsRestart(null)).IsFalse();
    }

    [TestCase]
    public void TestShaderSettingsNeedARestartAndTheLiveRowsDoNot()
    {
        AssertThat(AndroidRestartNotes.NeedsRestart("android_modern_lighting")).IsTrue();
        AssertThat(AndroidRestartNotes.NeedsRestart(Refii.Models.Engine_ShaderVariant.NormalMapsKey)).IsTrue();
        AssertThat(AndroidRestartNotes.NeedsRestart("max_fps")).IsFalse();
        AssertThat(AndroidRestartNotes.NeedsRestart("render_scale_percent")).IsFalse();
        AssertThat(AndroidRestartNotes.NeedsRestart("prop_cull_distance_scale")).IsFalse();
        AssertThat(AndroidRestartNotes.NeedsRestart("no_such_setting")).IsFalse();
    }

    [TestCase]
    public void TestTheRestartFollowsEachKeysOwnDescription()
    {
        foreach (string key in new[] { "android_modern_lighting", Refii.Models.Engine_ShaderVariant.NormalMapsKey, "max_fps", "render_scale_percent", "prop_cull_distance_scale" })
        {
            RuntimeCVarDefinition definition = RuntimeCVarSet.All.First(candidate => candidate.Name == key);
            AssertThat(AndroidRestartNotes.NeedsRestart(key)).IsEqual(definition.Description.Contains("next launch", System.StringComparison.OrdinalIgnoreCase) ||
                definition.Description.Contains("next start", System.StringComparison.OrdinalIgnoreCase));
        }
    }

    [TestCase]
    public void TestOnlyAChangedValueCounts()
    {
        var current = new Dictionary<string, string> { ["android_modern_lighting"] = "true", ["render_scale_percent"] = "50" };
        AssertThat(AndroidRestartNotes.ChangesNeedRestart(current, new[] { ("android_modern_lighting", "true") })).IsFalse();
        AssertThat(AndroidRestartNotes.ChangesNeedRestart(current, new[] { ("android_modern_lighting", "\"TRUE\"") })).IsFalse();
        AssertThat(AndroidRestartNotes.ChangesNeedRestart(current, new[] { ("android_modern_lighting", "false") })).IsTrue();
        AssertThat(AndroidRestartNotes.ChangesNeedRestart(current, new[] { ("render_scale_percent", "75") })).IsFalse();
        // Absent stands at its default: writing the default changes nothing.
        AssertThat(AndroidRestartNotes.ChangesNeedRestart(new Dictionary<string, string>(),
            new[] { ("android_modern_lighting", RuntimeCVarSet.All.First(d => d.Name == "android_modern_lighting").DefaultValue) })).IsFalse();
    }

    [TestCase]
    public void TestLowToHighNeedsARestartAndTheSamePresetDoesNot()
    {
        bool offered = GraphicsPresets.Offered;
        GraphicsPresets.Offered = true;
        try
        {
            var low = new Dictionary<string, string>();
            GraphicsPresets.Apply(GraphicsPresets.Low, low);
            var high = new Dictionary<string, string>();
            GraphicsPresets.Apply(GraphicsPresets.High, high);
            // Low and High differ in at least one setting compiled into the shaders.
            AssertThat(GraphicsPresets.Values(GraphicsPresets.High)!
                .Any(pair => AndroidRestartNotes.NeedsRestart(pair.Key) && pair.Value != low.GetValueOrDefault(pair.Key))).IsTrue();
            AssertThat(AndroidRestartNotes.PresetNeedsRestart(low, GraphicsPresets.High)).IsTrue();
            AssertThat(AndroidRestartNotes.PresetNeedsRestart(high, GraphicsPresets.High)).IsFalse();
            AssertThat(AndroidRestartNotes.PresetNeedsRestart(low, GraphicsPresets.Low)).IsFalse();
            AssertThat(AndroidRestartNotes.PresetNeedsRestart(low, GraphicsPresets.Custom)).IsFalse();
        }
        finally
        {
            GraphicsPresets.Offered = offered;
        }
    }

    [TestCase]
    public void TestTheNotesAreCalm()
    {
        foreach (string note in new[] { AndroidRestartNotes.SettingNote, AndroidRestartNotes.PresetNote })
        {
            AssertThat(note).Contains("next time you start the game");
            AssertThat(note.Contains('!')).IsFalse();
            AssertThat(note.ToLowerInvariant()).NotContains("warning").NotContains("error").NotContains("restart");
        }
    }
}
