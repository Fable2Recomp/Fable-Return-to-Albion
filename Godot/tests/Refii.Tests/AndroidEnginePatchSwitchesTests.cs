using System.Collections.Generic;
using System.Linq;
using GdUnit4;
using Refii.AndroidRuntime;
using Refii.Launcher.Core;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>The switches of godot-patches/0014-0016 (AndroidEnginePatchSwitches).</summary>
[TestSuite]
public sealed class AndroidEnginePatchSwitchesTests
{
    private static IEnumerable<string> Keys =>
        AndroidEnginePatchSwitches.Bools.Select(b => b.Key).Append(AndroidEnginePatchSwitches.FrontToBackReachKey);

    /// <summary>Unset is the stock engine: every skip off and bands at every distance. The reach is clamped.</summary>
    [TestCase]
    public void ReadsTheKeysAndClampsTheReach()
    {
        (bool[] bools, int reach) = AndroidEnginePatchSwitches.Wanted(new Dictionary<string, string>());
        AssertThat(bools.Any(b => b)).IsFalse();
        AssertThat(reach).IsEqual(0);

        var values = AndroidEnginePatchSwitches.Bools.ToDictionary(b => b.Key, _ => "true");
        values[AndroidEnginePatchSwitches.FrontToBackReachKey] = "32";
        (bools, reach) = AndroidEnginePatchSwitches.Wanted(values);
        AssertThat(bools.All(b => b)).IsTrue();
        AssertThat(reach).IsEqual(32);

        values[AndroidEnginePatchSwitches.FrontToBackReachKey] = "-5";
        AssertThat(AndroidEnginePatchSwitches.Wanted(values).Reach).IsEqual(0);
        values[AndroidEnginePatchSwitches.FrontToBackReachKey] = "100000";
        AssertThat(AndroidEnginePatchSwitches.Wanted(values).Reach).IsEqual(AndroidEnginePatchSwitches.MaxReach);
    }

    /// <summary>Each key drives its own engine setting, all under Mobile's renderer settings.</summary>
    [TestCase]
    public void EachKeyHasItsOwnEngineSetting()
    {
        var settings = AndroidEnginePatchSwitches.Bools.Select(b => b.Setting)
            .Append(AndroidEnginePatchSwitches.FrontToBackReachSetting).ToList();
        AssertThat(settings.Distinct().Count()).IsEqual(settings.Count);
        AssertThat(settings.All(s => s.StartsWith("rendering/renderer/") && s.EndsWith("_mobile"))).IsTrue();
    }

    /// <summary>The switches every preset turns on since revision 23; the others are in no preset.</summary>
    private static readonly string[] InEveryPreset =
    {
        AndroidEnginePatchSwitches.SunShadowBeyondFadeKey, AndroidEnginePatchSwitches.SunShadowFacingAwayKey,
        AndroidEnginePatchSwitches.LampOutOfRangeKey,
    };

    /// <summary>Launcher rows at the stock behaviour, live and named for a phone; three on in every preset, the rest in none.</summary>
    [TestCase]
    public void RegisteredOffLiveNamedAndInTheirPresets()
    {
        foreach (string key in Keys)
        {
            RuntimeCVarDefinition row = RuntimeCVarSet.All.First(d => d.Name == key);
            AssertThat(row.DefaultValue).OverrideFailureMessage(key).IsEqual(key == AndroidEnginePatchSwitches.FrontToBackReachKey ? "0" : "false");
            AssertThat(row.Description).OverrideFailureMessage(key).EndsWith("Takes effect live.");
            AssertThat(RuntimeCVarsAndroid.AllKeys.Contains(key)).OverrideFailureMessage(key).IsTrue();
            foreach (var preset in new[] { GraphicsPresets.LowValues, GraphicsPresets.MediumValues, GraphicsPresets.HighValues, GraphicsPresets.UltraValues })
            {
                if (InEveryPreset.Contains(key))
                {
                    AssertThat(preset[key]).OverrideFailureMessage(key).IsEqual("true");
                }
                else
                {
                    AssertThat(preset.ContainsKey(key)).OverrideFailureMessage(key + " is in a preset").IsFalse();
                }
            }

            bool offered = GraphicsPresets.Offered;
            try
            {
                GraphicsPresets.Offered = true;
                AssertThat(AndroidSettingNames.Name(key)).OverrideFailureMessage(key).IsNotNull();
            }
            finally { GraphicsPresets.Offered = offered; }
        }
    }

    /// <summary>
    /// The mod-API rule for the two switches that change what a mod shader's own light() sees: docs/android.md's mod-API
    /// section says what a mod can no longer assume on a phone and names the settings that turn it back, the settings'
    /// descriptions say it in place (players do not read repo docs), and the presets carry both keys (so the section's
    /// "every graphics preset" holds).
    /// </summary>
    [TestCase]
    public void TheModApiDocSaysWhatAModLightFunctionLoses()
    {
        string facing = AndroidEnginePatchSwitches.SunShadowFacingAwayKey, range = AndroidEnginePatchSwitches.LampOutOfRangeKey;
        foreach (var preset in new[] { GraphicsPresets.LowValues, GraphicsPresets.MediumValues, GraphicsPresets.HighValues, GraphicsPresets.UltraValues })
        {
            AssertThat(preset[facing]).IsEqual("true");
            AssertThat(preset[range]).IsEqual("true");
        }

        string facingRow = RuntimeCVarSet.All.First(d => d.Name == facing).Description;
        AssertThat(facingRow).Contains("light()");
        AssertThat(facingRow).Contains("97 degrees");
        AssertThat(facingRow).NotContains("android.md");
        string rangeRow = RuntimeCVarSet.All.First(d => d.Name == range).Description;
        AssertThat(rangeRow).Contains("light()");
        AssertThat(rangeRow).Contains("not called");
        AssertThat(rangeRow).NotContains("android.md");

        // The phone-only parts of the mod API live in docs/android.md: the shipped guide (Godot/docs) may not mention
        // Android (AndroidSeparationTests).
        if (RepoRoot() is not { } root)
        {
            return;
        }

        string text = System.IO.File.ReadAllText(System.IO.Path.Combine(root, "docs", "android.md"));
        int at = text.IndexOf("\n## A mod's own light() on a phone\n", System.StringComparison.Ordinal);
        AssertThat(at).OverrideFailureMessage("docs/android.md: no section on a mod's own light()").IsGreater(0);
        int end = text.IndexOf("\n## ", at + 1, System.StringComparison.Ordinal);
        string section = end < 0 ? text[at..] : text[at..end];
        foreach (string said in new[] { "97 degrees", "dot(NORMAL, LIGHT) < -0.125", "ATTENUATION", "is not called", "every graphics preset", facing, range })
        {
            AssertThat(section.Contains(said, System.StringComparison.Ordinal)).OverrideFailureMessage("docs/android.md: " + said).IsTrue();
        }

        // The section names the settings by the names a phone shows them under.
        bool offered = GraphicsPresets.Offered;
        try
        {
            GraphicsPresets.Offered = true;
            foreach (string key in new[] { facing, range })
            {
                string name = AndroidSettingNames.Name(key)!;
                AssertThat(section.Contains("**" + name + "**", System.StringComparison.Ordinal)).OverrideFailureMessage("docs/android.md: " + name).IsTrue();
            }
        }
        finally { GraphicsPresets.Offered = offered; }

        // acos(-0.125) is the angle the section states.
        AssertThat(System.Math.Round(System.Math.Acos(-0.125) * 180 / System.Math.PI)).IsEqual(97.0);
    }

    private static string? RepoRoot()
    {
        string? at = System.AppContext.BaseDirectory;
        for (int i = 0; i < 12 && at != null; i++)
        {
            if (System.IO.File.Exists(System.IO.Path.Combine(at, "docs", "android.md")))
            {
                return at;
            }

            at = System.IO.Path.GetDirectoryName(at);
        }

        return null;
    }

    /// <summary>The desktop's official engine has none of the settings: applying the keys must not create them.</summary>
    [TestCase, RequireGodotRuntime]
    public void LeavesAnUnpatchedEngineAlone()
    {
        var settings = AndroidEnginePatchSwitches.Bools.Select(b => b.Setting)
            .Append(AndroidEnginePatchSwitches.FrontToBackReachSetting).ToList();
        var known = settings.ToDictionary(s => s, s => Godot.ProjectSettings.HasSetting(s));

        var on = AndroidEnginePatchSwitches.Bools.ToDictionary(b => b.Key, _ => "true");
        on[AndroidEnginePatchSwitches.FrontToBackReachKey] = "32";
        AndroidEnginePatchSwitches.Apply(on);
        foreach (string setting in settings)
        {
            AssertThat(Godot.ProjectSettings.HasSetting(setting)).OverrideFailureMessage(setting).IsEqual(known[setting]);
        }

        AndroidEnginePatchSwitches.Apply(new Dictionary<string, string>());
    }
}
