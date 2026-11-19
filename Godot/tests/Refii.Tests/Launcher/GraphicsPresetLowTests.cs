namespace Refii.Tests.Launcher;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.AndroidRuntime;
using Refii.Launcher.Core;

/// <summary>
/// Low (<see cref="GraphicsPresets.Low"/>) since revision 19: Low under the modern lighting with the
/// settings of the retired Low (lean), its normal maps off; every other preset on the fable model with
/// those settings at their defaults; and a phone that stored Low (lean) moved to Low.
/// </summary>
[TestSuite]
public class GraphicsPresetLowTests
{
    private const string RetiredLowLean = "android_low_lean";

    /// <summary>What Low took from Low (lean) at revision 19, Low (lean)'s values as revision 18 had them.</summary>
    private static readonly Dictionary<string, string> FromLowLean = new(StringComparer.Ordinal)
    {
        ["android_modern_lighting"] = "true",
        ["lite_fog"] = "table",
        ["lite_fold_modern_model"] = "true",
        ["lite_prop_fragment_world"] = "true",
        ["lite_pack_lightmap_uv"] = "true",
        ["lite_ground_ao"] = "off",
        ["lite_skip_black_lights"] = "true",
    };

    /// <summary>The settings Low (lean) decided and the presets before revision 19 did not.</summary>
    private static readonly string[] LeanOnly =
    {
        "android_modern_lighting", "lite_fog", "lite_fold_modern_model",
        "lite_prop_fragment_world", "lite_pack_lightmap_uv", "lite_ground_ao",
    };

    private static readonly string[] OtherPresets =
        { GraphicsPresets.Medium, GraphicsPresets.High, GraphicsPresets.Ultra };

    [BeforeTest]
    public void StandInForAPhone() => GraphicsPresets.Offered = true;

    [AfterTest]
    public void BackToThisPlatform() => GraphicsPresets.Offered = Refii.Core.Engine_Platform.IsAndroid;

    private static string DefaultOf(string key) => RuntimeCVarSet.All.First(d => d.Name == key).DefaultValue;

    private static string Flat(IReadOnlyDictionary<string, string> values) =>
        string.Join("\n", values.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + "=" + pair.Value));

    private static string Config(string text)
    {
        string path = Path.Combine(Refii.Tests.TestTemp.Root, "refii-low-" + Guid.NewGuid().ToString("N") + ".toml");
        File.WriteAllText(path, text);
        return path;
    }

    /// <summary>A phone written by revision 18 on Low (lean): Low's values then, with Low (lean)'s on top.</summary>
    private static Dictionary<string, string> LowLeanPhoneAt18()
    {
        var phone = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string key, string value) in GraphicsPresets.LowValues.Where(pair => !LeanOnly.Contains(pair.Key)))
        {
            phone[key] = value;
        }

        foreach ((string key, string value) in FromLowLean)
        {
            phone[key] = value;
        }

        phone[Refii.Models.Engine_ShaderVariant.NormalMapsKey] = "fast";
        phone[GraphicsPresets.Key] = RetiredLowLean;
        phone[GraphicsPresets.RevisionKey] = "18";
        return phone;
    }

    [TestCase]
    public void TestLowTakesLowLeansSettingsWithItsNormalMapsOff()
    {
        foreach ((string key, string value) in FromLowLean)
        {
            AssertThat(GraphicsPresets.LowValues[key]).OverrideFailureMessage(key).IsEqual(value);
        }

        // Low (lean) drew normal maps fast; Low keeps them off, the faster.
        AssertThat(GraphicsPresets.LowValues[Refii.Models.Engine_ShaderVariant.NormalMapsKey]).IsEqual("off");
        // Lean modern stays on by its own default, which no preset names.
        AssertThat(GraphicsPresets.LowValues.ContainsKey(AndroidLeanModern.Key)).IsFalse();
        AssertThat(DefaultOf(AndroidLeanModern.Key)).IsEqual("true");
        // Low's other values are its own, unchanged: High's with its compromises.
        AssertThat(GraphicsPresets.LowValues["render_scale_percent"]).IsEqual("40");
        AssertThat(GraphicsPresets.LowValues["sun_shadow_distance"]).IsEqual("0");
        AssertThat(GraphicsPresets.LowValues["placed_light_limit"]).IsEqual("4");
        AssertThat(GraphicsPresets.LowValues["lite_shadowless"]).IsEqual("true");
        AssertThat(GraphicsPresets.LowValues["android_low_ram_mode"]).IsEqual("on");
    }

    [TestCase]
    public void TestEveryPresetSetsTheSameSettingsItsLightingModelAmongThem()
    {
        string[] members = GraphicsPresets.Members.OrderBy(k => k, StringComparer.Ordinal).ToArray();
        AssertThat(GraphicsPresets.Members.ToArray()).IsEqual(GraphicsPresets.HighValues.Keys.ToArray());
        foreach (string preset in OtherPresets.Append(GraphicsPresets.Low))
        {
            AssertThat(GraphicsPresets.Values(preset)!.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray())
                .OverrideFailureMessage(preset).IsEqual(members);
        }

        AssertThat(GraphicsPresets.LowValues["android_modern_lighting"]).IsEqual("true");
        foreach (string preset in OtherPresets)
        {
            AssertThat(GraphicsPresets.Values(preset)!["android_modern_lighting"]).OverrideFailureMessage(preset).IsEqual("false");
            // Low's modern drawing at the settings' own defaults: what the preset drew before it named them.
            foreach (string key in LeanOnly)
            {
                AssertThat(GraphicsPresets.Values(preset)![key]).OverrideFailureMessage(preset + " " + key).IsEqual(DefaultOf(key));
            }
        }

        foreach (string key in GraphicsPresets.Members)
        {
            AssertThat(RuntimeCVarSet.All.Any(d => d.Name == key)).OverrideFailureMessage(key + " is not a setting").IsTrue();
            AssertThat(AndroidSettingNames.Name(key)).OverrideFailureMessage(key + " has no phone name").IsNotNull();
        }
    }

    /// <summary>Leaving Low puts every setting it decides back to the preset picked, exactly.</summary>
    [TestCase]
    public void TestSwitchingFromLowGivesTheOtherPresetExactly()
    {
        foreach (string preset in OtherPresets)
        {
            var phone = new Dictionary<string, string>();
            GraphicsPresets.Apply(GraphicsPresets.Low, phone);
            AssertThat(GraphicsPresets.Apply(preset, phone)).IsTrue();
            AssertThat(GraphicsPresets.Current(phone)).OverrideFailureMessage(preset).IsEqual(preset);
            foreach (string key in GraphicsPresets.LowValues.Keys)
            {
                AssertThat(phone[key]).OverrideFailureMessage(preset + " " + key).IsEqual(GraphicsPresets.Values(preset)![key]);
            }

            var fresh = new Dictionary<string, string>();
            GraphicsPresets.Apply(preset, fresh);
            AssertThat(Flat(phone)).OverrideFailureMessage(preset).IsEqual(Flat(fresh));
        }

        // Through Options, writing refii.toml and telling the game: Low to High is High, the fable model back.
        string config = Config("max_fps = 30\n");
        try
        {
            AndroidQuickSettings.ApplyPreset(config, GraphicsPresets.Low, (_, _) => { });
            AssertThat(AndroidQuickSettings.Value(config, "android_modern_lighting")).IsEqual("true");
            var live = new Dictionary<string, string>();
            AndroidQuickSettings.ApplyPreset(config, GraphicsPresets.High, (k, v) => live[k] = v);
            AssertThat(AndroidQuickSettings.Preset(config)).IsEqual(GraphicsPresets.High);
            foreach (string key in GraphicsPresets.LowValues.Keys)
            {
                AssertThat(AndroidQuickSettings.Value(config, key)).OverrideFailureMessage(key).IsEqual(GraphicsPresets.HighValues[key]);
                AssertThat(live[key]).OverrideFailureMessage(key).IsEqual(GraphicsPresets.HighValues[key]);
            }

            foreach (string key in LeanOnly)
            {
                AssertThat(AndroidQuickSettings.Value(config, key)).OverrideFailureMessage(key).IsEqual(DefaultOf(key));
            }

            AssertThat(AndroidQuickSettings.Value(config, "android_modern_lighting")).IsEqual("false");
            AssertThat(AndroidQuickSettings.FrameCap(config)).IsEqual(30);
        }
        finally
        {
            File.Delete(config);
        }
    }

    [TestCase]
    public void TestApplyingLowWritesItsValuesAndReadsBack()
    {
        var model = new RuntimeSettingsModel();
        model.ApplyGraphicsPreset(GraphicsPresets.Low);
        AssertThat(model.CurrentGraphicsPreset()).IsEqual(GraphicsPresets.Low);
        AssertThat(model.Values[GraphicsPresets.RevisionKey]).IsEqual(GraphicsPresets.Revision.ToString());
        foreach ((string key, string value) in GraphicsPresets.LowValues)
        {
            AssertThat(model.Values[key]).OverrideFailureMessage(key).IsEqual(value);
        }

        string path = Path.Combine(Refii.Tests.TestTemp.Root, "refii-low-" + Guid.NewGuid().ToString("N") + ".toml");
        try
        {
            var values = new Dictionary<string, string>();
            AssertThat(GraphicsPresets.Apply(GraphicsPresets.Low, values)).IsTrue();
            AssertThat(RuntimeConfig.WriteValues(path, values, out string _)).IsTrue();
            SortedDictionary<string, string> read = RuntimeConfig.ReadValues(path);
            AssertThat(GraphicsPresets.Current(read)).IsEqual(GraphicsPresets.Low);
            AssertThat(Refii.Core.Engine_RefiiConfig.Bool(read, "android_modern_lighting", false)).IsTrue();
            AssertThat(Refii.Core.Engine_RefiiConfig.String(read, "lite_fog", "")).IsEqual("table");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestCase]
    public void TestAChangeOnLowReadsAsCustom()
    {
        foreach (string key in new[] { "lite_fog", "android_modern_lighting", "render_scale_percent" })
        {
            var model = new RuntimeSettingsModel();
            model.ApplyGraphicsPreset(GraphicsPresets.Low);
            string original = model.Values[key];
            model.Values[key] = key switch { "lite_fog" => "full", "android_modern_lighting" => "false", _ => "55" };
            AssertThat(model.RefreshGraphicsPreset(key)).OverrideFailureMessage(key).IsTrue();
            AssertThat(model.Values[GraphicsPresets.Key]).OverrideFailureMessage(key).IsEqual(GraphicsPresets.Custom);

            model.Values[key] = original;
            model.RefreshGraphicsPreset(key);
            AssertThat(model.Values[GraphicsPresets.Key]).OverrideFailureMessage(key).IsEqual(GraphicsPresets.Low);
        }
    }

    /// <summary>A phone that stored Low (lean) is on Low from its next start, with Low's values.</summary>
    [TestCase]
    public void TestALowLeanPhoneMovesToLowOnItsNextStart()
    {
        Dictionary<string, string> phone = LowLeanPhoneAt18();
        AssertThat(GraphicsPresets.Upgrade(phone)).IsTrue();
        AssertThat(phone[GraphicsPresets.Key]).IsEqual(GraphicsPresets.Low);
        AssertThat(phone[GraphicsPresets.RevisionKey]).IsEqual(GraphicsPresets.Revision.ToString());
        AssertThat(GraphicsPresets.Current(phone)).IsEqual(GraphicsPresets.Low);
        foreach ((string key, string value) in GraphicsPresets.LowValues)
        {
            AssertThat(phone[key]).OverrideFailureMessage(key).IsEqual(value);
        }

        AssertThat(phone[Refii.Models.Engine_ShaderVariant.NormalMapsKey]).IsEqual("off");
        // Once: the next start changes nothing.
        AssertThat(GraphicsPresets.Upgrade(phone)).IsFalse();

        // From an earlier revision too, and through refii.toml.
        string path = Path.Combine(Refii.Tests.TestTemp.Root, "refii-low-" + Guid.NewGuid().ToString("N") + ".toml");
        try
        {
            var older = new Dictionary<string, string> { [GraphicsPresets.Key] = RetiredLowLean, [GraphicsPresets.RevisionKey] = "17" };
            AssertThat(RuntimeConfig.WriteValues(path, older, out string _)).IsTrue();
            SortedDictionary<string, string> read = RuntimeConfig.ReadValues(path);
            AssertThat(GraphicsPresets.Upgrade(read)).IsTrue();
            AssertThat(RuntimeConfig.WriteValues(path, read, out string _)).IsTrue();
            SortedDictionary<string, string> after = RuntimeConfig.ReadValues(path);
            AssertThat(after[GraphicsPresets.Key]).IsEqual(GraphicsPresets.Low);
            AssertThat(GraphicsPresets.Current(after)).IsEqual(GraphicsPresets.Low);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>The retired name, wherever it is still read, is Low; no picker offers it.</summary>
    [TestCase]
    public void TestTheRetiredNameReadsAsLow()
    {
        AssertThat(GraphicsPresets.Canonical(RetiredLowLean)).IsEqual(GraphicsPresets.Low);
        AssertThat(GraphicsPresets.Canonical("\"Android_Low_Lean\"")).IsEqual(GraphicsPresets.Low);
        AssertThat(GraphicsPresets.Label(RetiredLowLean)).IsEqual("Low");
        AssertThat(ReferenceEquals(GraphicsPresets.Values(RetiredLowLean), GraphicsPresets.LowValues)).IsTrue();
        AssertThat(GraphicsPresets.PickWarning(RetiredLowLean)).IsNull();
        AssertThat(AndroidQuickSettings.PresetLabel(RetiredLowLean)).IsEqual("Preset: Low");
        AssertThat(AndroidSettingsLayout.Summary(RetiredLowLean)).IsEqual(AndroidSettingsLayout.Summary(GraphicsPresets.Low));

        // Stored beside Low's values (a hand edit at this revision), it reads as Low.
        var values = new Dictionary<string, string>(GraphicsPresets.LowValues) { [GraphicsPresets.Key] = RetiredLowLean };
        AssertThat(GraphicsPresets.Current(values)).IsEqual(GraphicsPresets.Low);
        // Beside revision 18's Low (lean) values it reads as Custom until the upgrade moves it.
        Dictionary<string, string> at18 = LowLeanPhoneAt18();
        AssertThat(GraphicsPresets.Current(at18)).IsEqual(GraphicsPresets.Custom);
        // Applied by its old name, it writes Low.
        var applied = new Dictionary<string, string>();
        AssertThat(GraphicsPresets.Apply(RetiredLowLean, applied)).IsTrue();
        AssertThat(applied[GraphicsPresets.Key]).IsEqual(GraphicsPresets.Low);

        RuntimeCVarDefinition definition = RuntimeCVarSet.All.First(d => d.Name == GraphicsPresets.Key);
        AssertThat(RuntimeSettingsModel.OptionsFor(definition, new Dictionary<string, string>())
            .Any(option => option.Value == RetiredLowLean)).IsFalse();
        AssertThat(AndroidQuickSettings.Presets.Contains(RetiredLowLean)).IsFalse();
        AssertThat(AndroidSettingNames.About(GraphicsPresets.Key)).NotContains("Low (lean)");
        foreach (string gpu in new[] { "Adreno (TM) 640", "Adreno (TM) 830", "Mali-G715", "" })
        {
            AssertThat(AndroidLauncherSettings.FirstPreset(gpu).Preset).IsNotEqual(RetiredLowLean);
        }
    }

    /// <summary>At revision 19 a phone on another preset takes its lighting model and Low's settings at their defaults.</summary>
    [TestCase]
    public void TestAPhoneOnAnotherPresetTakesTheFableModelAtRevision19()
    {
        foreach (string preset in OtherPresets)
        {
            // Turned on by hand under revision 18, when no preset named it: the preset's model back.
            var phone = new Dictionary<string, string>();
            GraphicsPresets.Apply(preset, phone);
            phone["android_modern_lighting"] = "true";
            phone["lite_fog"] = "table";
            phone[GraphicsPresets.RevisionKey] = "18";
            AssertThat(GraphicsPresets.Upgrade(phone)).OverrideFailureMessage(preset).IsTrue();
            AssertThat(phone[GraphicsPresets.Key]).OverrideFailureMessage(preset).IsEqual(preset);
            AssertThat(phone["android_modern_lighting"]).OverrideFailureMessage(preset).IsEqual("false");
            AssertThat(phone["lite_fog"]).OverrideFailureMessage(preset).IsEqual(DefaultOf("lite_fog"));
            AssertThat(GraphicsPresets.Current(phone)).OverrideFailureMessage(preset).IsEqual(preset);

            // Already on this revision: nothing to upgrade.
            var before = new Dictionary<string, string>(phone);
            AssertThat(GraphicsPresets.Upgrade(phone)).OverrideFailureMessage(preset).IsFalse();
            AssertThat(Flat(phone)).OverrideFailureMessage(preset).IsEqual(Flat(before));
        }

        // A Low phone of revision 18 takes the modern model and the rest of Low (lean)'s settings.
        var low = new Dictionary<string, string> { [GraphicsPresets.Key] = GraphicsPresets.Low, [GraphicsPresets.RevisionKey] = "18" };
        AssertThat(GraphicsPresets.Upgrade(low)).IsTrue();
        foreach ((string key, string value) in FromLowLean)
        {
            AssertThat(low[key]).OverrideFailureMessage(key).IsEqual(value);
        }
        AssertThat(GraphicsPresets.Current(low)).IsEqual(GraphicsPresets.Low);
    }
}
