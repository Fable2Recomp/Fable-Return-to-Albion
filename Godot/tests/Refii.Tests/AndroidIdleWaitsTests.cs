using GdUnit4;
using Refii.AndroidRuntime;
using Refii.Launcher.Core;
using System.Collections.Generic;
using System.Linq;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>The native threads' waits (AndroidIdleWaits) and the cloth land at FramePreDraw (AndroidClothLand).</summary>
[TestSuite]
public sealed class AndroidIdleWaitsTests
{
    private static Dictionary<string, string> Values(params (string Key, string Value)[] pairs) =>
        pairs.ToDictionary(pair => pair.Key, pair => pair.Value);

    /// <summary>Unset, or set to the defaults, sets no variable: the native defaults stand.</summary>
    [TestCase]
    public void DefaultsSetNothing()
    {
        AssertThat(AndroidIdleWaits.Environment(Values()).Count).IsEqual(0);
        AssertThat(AndroidIdleWaits.Environment(Values(
            (AndroidIdleWaits.ShortSleepsKey, "false"), (AndroidIdleWaits.TickWaitKey, "500"),
            (AndroidIdleWaits.CompletionKey, "1"), (AndroidIdleWaits.RexNetIdleKey, "16"),
            (AndroidIdleWaits.WaitCheckKey, "1"), (AndroidIdleWaits.YieldBackoffKey, "true"))).Count).IsEqual(0);
    }

    /// <summary>Each setting becomes the variable its native reader takes.</summary>
    [TestCase]
    public void EachSettingNamesItsVariable()
    {
        var variables = AndroidIdleWaits.Environment(Values(
            (AndroidIdleWaits.ShortSleepsKey, "true"), (AndroidIdleWaits.TickWaitKey, "2000"),
            (AndroidIdleWaits.CompletionKey, "\"4\""), (AndroidIdleWaits.RexNetIdleKey, "250"),
            (AndroidIdleWaits.WaitCheckKey, "20"), (AndroidIdleWaits.YieldBackoffKey, "false")))
            .ToDictionary(pair => pair.Name, pair => pair.Value);
        AssertThat(variables["REFII_GUEST_SHORT_SLEEPS"]).IsEqual("1");
        AssertThat(variables["REFII_TICK_WAIT_SLEEP_US"]).IsEqual("2000");
        AssertThat(variables["REFII_COMPLETION_PERIOD_MS"]).IsEqual("4");
        AssertThat(variables["REFII_REXNET_IDLE_WAIT_MS"]).IsEqual("250");
        AssertThat(variables["REFII_GUEST_WAIT_CHECK_MS"]).IsEqual("20");
        AssertThat(variables["REFII_YIELD_BACKOFF"]).IsEqual("0");
    }

    /// <summary>Out of range is clamped into the range the native side takes; a bad number is the default.</summary>
    [TestCase]
    public void ValuesAreClampedToTheNativeRanges()
    {
        var high = AndroidIdleWaits.Environment(Values(
            (AndroidIdleWaits.TickWaitKey, "100000"), (AndroidIdleWaits.CompletionKey, "50"),
            (AndroidIdleWaits.RexNetIdleKey, "5000"), (AndroidIdleWaits.WaitCheckKey, "1000")))
            .ToDictionary(pair => pair.Name, pair => pair.Value);
        AssertThat(high["REFII_TICK_WAIT_SLEEP_US"]).IsEqual("8000");
        AssertThat(high["REFII_COMPLETION_PERIOD_MS"]).IsEqual("8");
        AssertThat(high["REFII_REXNET_IDLE_WAIT_MS"]).IsEqual("1000");
        AssertThat(high["REFII_GUEST_WAIT_CHECK_MS"]).IsEqual("100");

        AssertThat(AndroidIdleWaits.Environment(Values(
            (AndroidIdleWaits.TickWaitKey, "10"), (AndroidIdleWaits.CompletionKey, "0"),
            (AndroidIdleWaits.RexNetIdleKey, "fast"), (AndroidIdleWaits.WaitCheckKey, "0"))).Count).IsEqual(0);
        AssertThat(AndroidIdleWaits.Environment(Values((AndroidIdleWaits.WaitCheckKey, "-5"))).Count).IsEqual(0);
    }

    /// <summary>Every setting here is in the Android list with its default, named for a player, and waits for the next start.</summary>
    [TestCase]
    public void SettingsAreListedNamedAndWaitForTheNextStart()
    {
        var defaults = new Dictionary<string, string>
        {
            [AndroidIdleWaits.ShortSleepsKey] = "false",
            [AndroidIdleWaits.TickWaitKey] = AndroidIdleWaits.TickWaitDefault.ToString(),
            [AndroidIdleWaits.CompletionKey] = AndroidIdleWaits.CompletionDefault.ToString(),
            [AndroidIdleWaits.RexNetIdleKey] = AndroidIdleWaits.RexNetIdleDefault.ToString(),
            [AndroidIdleWaits.WaitCheckKey] = AndroidIdleWaits.WaitCheckDefault.ToString(),
            [AndroidIdleWaits.YieldBackoffKey] = "true",
            [AndroidClothLand.Key] = "false",
        };
        foreach ((string key, string value) in defaults)
        {
            RuntimeCVarDefinition definition = RuntimeCVarSet.All.First(candidate => candidate.Name == key);
            AssertThat(definition.DefaultValue).OverrideFailureMessage(key).IsEqual(value);
            AssertThat(RuntimeCVarsAndroid.AllKeys.Contains(key)).OverrideFailureMessage(key).IsTrue();
            AssertThat(AndroidSettingNames.Keys.Contains(key)).OverrideFailureMessage(key).IsTrue();
            AssertThat(AndroidRestartNotes.NeedsRestart(key)).OverrideFailureMessage(key).IsTrue();
        }
    }

    /// <summary>No preset sets any of them: each stays at today's behaviour unless chosen.</summary>
    [TestCase]
    public void NoPresetSetsThem()
    {
        foreach (string preset in new[] { GraphicsPresets.Low, GraphicsPresets.Medium, GraphicsPresets.High, GraphicsPresets.Ultra })
        {
            var values = GraphicsPresets.Values(preset)!;
            foreach (string key in new[] { AndroidIdleWaits.ShortSleepsKey, AndroidIdleWaits.TickWaitKey,
                AndroidIdleWaits.CompletionKey, AndroidIdleWaits.RexNetIdleKey, AndroidIdleWaits.WaitCheckKey,
                AndroidClothLand.Key })
                AssertThat(values.ContainsKey(key)).OverrideFailureMessage(preset + " " + key).IsFalse();
        }
    }

    /// <summary>`android_cloth_land_predraw`: off unless refii.toml says true.</summary>
    [TestCase]
    public void TheClothLandMovesOnlyWhenConfigured()
    {
        AssertThat(AndroidClothLand.Configured(Values())).IsFalse();
        AssertThat(AndroidClothLand.Configured(Values((AndroidClothLand.Key, "false")))).IsFalse();
        AssertThat(AndroidClothLand.Configured(Values((AndroidClothLand.Key, "true")))).IsTrue();
    }

    /// <summary>`android_guest_wait_check_ms`: 1 (the old 1 ms polling) sets nothing; 2 is the least blocking value the native reader takes.</summary>
    [TestCase]
    public void TheWaitCheckBlocksOnlyFromTwo()
    {
        AssertThat(AndroidIdleWaits.Environment(Values((AndroidIdleWaits.WaitCheckKey, "1"))).Count).IsEqual(0);
        var two = AndroidIdleWaits.Environment(Values((AndroidIdleWaits.WaitCheckKey, "2")));
        AssertThat(two.Count).IsEqual(1);
        AssertThat(two[0].Name).IsEqual("REFII_GUEST_WAIT_CHECK_MS");
        AssertThat(two[0].Value).IsEqual("2");
    }
}
