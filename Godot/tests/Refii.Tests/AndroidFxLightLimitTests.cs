using System;
using GdUnit4;
using Refii.AndroidRuntime;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>`android_fx_light_limit` (AndroidFxLightLimit) and `android_interior_no_sun` (AndroidInteriorSun): the pure choices.</summary>
[TestSuite]
public sealed class AndroidFxLightLimitTests
{
    private static bool[] Choose(float[] distance, bool[] lit, int limit, out int kept)
    {
        var keep = new bool[distance.Length];
        kept = AndroidFxLightLimit.Choose(distance, lit, limit, keep);
        return keep;
    }

    /// <summary>Off, or no more lights than the limit: every light keeps its slot.</summary>
    [TestCase]
    public void KeepsEveryLightUnderTheLimitOrOff()
    {
        float[] distance = { 30f, 5f, 12f };
        bool[] lit = { false, false, false };
        AssertThat(Choose(distance, lit, 0, out int off)).ContainsExactly(true, true, true);
        AssertThat(off).IsEqual(3);
        AssertThat(Choose(distance, lit, 3, out int under)).ContainsExactly(true, true, true);
        AssertThat(under).IsEqual(3);
        AssertThat(Choose(Array.Empty<float>(), Array.Empty<bool>(), 2, out int none)).IsEmpty();
        AssertThat(none).IsEqual(0);
    }

    /// <summary>With nothing lit yet the nearest N take the slots.</summary>
    [TestCase]
    public void TheNearestTakeTheSlots()
    {
        float[] distance = { 30f, 5f, 12f, 2f, 40f };
        bool[] lit = new bool[5];
        AssertThat(Choose(distance, lit, 2, out int kept)).ContainsExactly(false, true, false, true, false);
        AssertThat(kept).IsEqual(2);
    }

    /// <summary>A lit light keeps its slot against one only a little nearer: no trading at the boundary.</summary>
    [TestCase]
    public void ALitLightHoldsAgainstOneOnlyALittleNearer()
    {
        // Lit at 10 m ranks as 7 m; an unlit one at 9 m does not take it.
        float[] distance = { 2f, 10f, 9f };
        bool[] lit = { true, true, false };
        AssertThat(Choose(distance, lit, 2, out _)).ContainsExactly(true, true, false);

        // Swapped back the next ranking, the same two stay: the lit one is still held.
        distance = new[] { 2f, 9.5f, 9f };
        AssertThat(Choose(distance, lit, 2, out _)).ContainsExactly(true, true, false);
    }

    /// <summary>One clearly nearer takes the farthest lit light's slot, and a new light by the camera always gets one.</summary>
    [TestCase]
    public void OneClearlyNearerTakesTheSlot()
    {
        // Lit at 20 m ranks as 15 m; an unlit one at 6 m takes the slot.
        float[] distance = { 3f, 20f, 6f };
        bool[] lit = { true, true, false };
        AssertThat(Choose(distance, lit, 2, out _)).ContainsExactly(true, false, true);

        // A spell cast at the hero, every slot held by farther lit lights: it still lights.
        distance = new[] { 8f, 9f, 10f, 0.5f };
        lit = new[] { true, true, true, false };
        AssertThat(Choose(distance, lit, 3, out _)).ContainsExactly(true, true, false, true);
    }

    /// <summary>Ties go to the light already lit; a light at no known distance ranks last.</summary>
    [TestCase]
    public void TiesGoToTheLitLight()
    {
        AssertThat(AndroidFxLightLimit.RankKey(1f, lit: true)).IsEqual(0f);
        float[] distance = { 1f, 0f };
        bool[] lit = { true, false };
        AssertThat(Choose(distance, lit, 1, out _)).ContainsExactly(true, false);
        AssertThat(AndroidFxLightLimit.RankKey(float.NaN, lit: false)).IsEqual(float.MaxValue);
        AssertThat(Choose(new[] { float.NaN, 50f }, new[] { true, false }, 1, out _)).ContainsExactly(false, true);
    }

    /// <summary>The slot fade runs over FadeSeconds each way and stops at the ends.</summary>
    [TestCase]
    public void TheFadeRunsBothWaysAndClamps()
    {
        float half = AndroidFxLightLimit.FadeSeconds / 2f;
        AssertThat(AndroidFxLightLimit.Step(1f, allowed: false, half)).IsEqualApprox(0.5f, 1e-5f);
        AssertThat(AndroidFxLightLimit.Step(0.5f, allowed: false, AndroidFxLightLimit.FadeSeconds)).IsEqual(0f);
        AssertThat(AndroidFxLightLimit.Step(0f, allowed: true, half)).IsEqualApprox(0.5f, 1e-5f);
        AssertThat(AndroidFxLightLimit.Step(0.9f, allowed: true, AndroidFxLightLimit.FadeSeconds)).IsEqual(1f);
    }

    /// <summary>The sun goes only with the key on and a frame stating no main light.</summary>
    [TestCase]
    public void TheSunGoesOnlyWhereNoMainLightIsStated()
    {
        AssertThat(AndroidInteriorSun.Hides(enabled: true, hasSky: false)).IsTrue();
        AssertThat(AndroidInteriorSun.Hides(enabled: true, hasSky: true)).IsFalse();
        AssertThat(AndroidInteriorSun.Hides(enabled: false, hasSky: false)).IsFalse();
    }

    /// <summary>Both keys are listed with the phone's settings, off by default, with a player's name.</summary>
    [TestCase]
    public void BothKeysAreRegisteredOff()
    {
        foreach ((string key, string off) in new[] { (AndroidFxLightLimit.Key, "0"), (AndroidInteriorSun.Key, "false") })
        {
            Refii.Launcher.Core.RuntimeCVarDefinition? row = null;
            foreach (Refii.Launcher.Core.RuntimeCVarDefinition definition in Refii.Launcher.Core.RuntimeCVarsAndroid.Tuning)
            {
                if (definition.Name == key) row = definition;
            }

            AssertThat(row).OverrideFailureMessage(key + " is not a Tuning row").IsNotNull();
            AssertThat(row!.DefaultValue).IsEqual(off);
        }
    }

    /// <summary>Off a phone an effect light is not tracked: its writes go straight to the node.</summary>
    [TestCase]
    [RequireGodotRuntime]
    public void TheDesktopTracksNoEffectLight()
    {
        AssertThat(Refii.Core.Engine_Platform.IsAndroid).IsFalse();
        var light = new Godot.OmniLight3D { Visible = true, LightEnergy = 2f };
        try
        {
            AndroidFxLightLimit.Track(light);
            AndroidFxLightLimit.SetShown(light, false);
            AndroidFxLightLimit.SetEnergy(light, 0.5f);
            AssertThat(light.Visible).IsFalse();
            AssertThat(light.LightEnergy).IsEqual(0.5f);
        }
        finally
        {
            light.Free();
        }
    }
}
