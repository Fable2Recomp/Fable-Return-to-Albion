using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using GdUnit4;
using Refii.AndroidRuntime;
using Refii.Launcher.Core;
using Refii.Lighting;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>`android_lean_modern` (<see cref="AndroidLeanModern"/>): the game-fed particle fade, and the lamps' reach and fade.</summary>
[TestSuite]
public sealed class AndroidLeanModernTests
{
    private static string FileText(string repoPath, [CallerFilePath] string source = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../../..", repoPath))).Replace("\r\n", "\n");

    /// <summary>Only the modern model's own shaders drop the fade, and not under the lean setting.</summary>
    [TestCase]
    public void LeanModernKeepsTheParticleFade()
    {
        AssertThat(AndroidLeanModern.ParticleFadeOff(classicActive: false, modernModel: true, leanFx: false)).IsTrue();
        AssertThat(AndroidLeanModern.ParticleFadeOff(classicActive: false, modernModel: true, leanFx: true)).IsFalse();
        AssertThat(AndroidLeanModern.ParticleFadeOff(classicActive: false, modernModel: false, leanFx: false)).IsFalse();
        AssertThat(AndroidLeanModern.ParticleFadeOff(classicActive: true, modernModel: true, leanFx: false)).IsFalse();
    }

    /// <summary>Off Android the setting is off, so the hook is the old expression.</summary>
    [TestCase]
    public void TheDesktopKeepsItsFade()
    {
        AssertThat(AndroidLeanModern.Fx).IsFalse();
        foreach (bool classic in new[] { false, true })
        {
            foreach (bool modern in new[] { false, true })
            {
                AssertThat(AndroidLeanModern.ParticleFadeOff(classic, modern)).IsEqual(!classic && modern);
            }
        }
    }

    /// <summary>Under lean modern the lamps' reach multiplier is the setting; anywhere else the modern model's own.</summary>
    [TestCase]
    public void TheLeanReachReplacesTheModernReach()
    {
        float modern = Engine_LevelLights.ModernBaseReachScale;
        AssertThat(AndroidLeanModern.LampReach(lean: true, setting: 1.0f, modernReach: modern)).IsEqual(1.0f);
        AssertThat(AndroidLeanModern.LampReach(lean: true, setting: 1.25f, modernReach: modern)).IsEqual(1.25f);
        AssertThat(AndroidLeanModern.LampReach(lean: false, setting: 1.0f, modernReach: modern)).IsEqual(modern);
        AssertThat(AndroidLeanModern.DefaultLampReach).IsEqual(1.0f);
        AssertThat(AndroidLeanModern.MaxLampReach).IsEqual(modern);
    }

    /// <summary>
    /// The default is what the phone tests got from <c>placed_light_reach_scale = 0.667</c> under the
    /// modern model, for the reach and for a lamp's stated fade, without touching the fable model.
    /// </summary>
    [TestCase]
    public void TheDefaultIsThePhoneTestsCounter()
    {
        const float testsGlobal = 0.667f;
        float modern = Engine_LevelLights.ModernBaseReachScale;
        float lean = AndroidLeanModern.DefaultLampReach;
        AssertThat(AndroidLeanModern.LampReach(true, lean, modern)).IsEqualApprox(modern * testsGlobal, 1e-3f);

        // ApplyDistanceFade's modern cutoff for a stated fade at the phone presets' 45 m.
        const float raw = 12f;
        const float rangePresetScale = 45f / 60f;
        float countered = raw * 2f * testsGlobal * rangePresetScale;
        float leanCutoff = AndroidLeanModern.LampFadeCutoff(true, lean, modern, raw * 2f * 1f * rangePresetScale, authored: true);
        AssertThat(leanCutoff).IsEqualApprox(countered, 0.01f);

        // A lamp with no stated fade fades at the view distance, which is not the lamp's own.
        AssertThat(AndroidLeanModern.LampFadeCutoff(true, lean, modern, 45f, authored: false)).IsEqual(45f);
        AssertThat(AndroidLeanModern.LampFadeCutoff(false, lean, modern, 18f, authored: true)).IsEqual(18f);
    }

    /// <summary>The fade share sets the ramp when above 0; 0 keeps the modern model's.</summary>
    [TestCase]
    public void TheFadeShareSetsTheRamp()
    {
        AssertThat(AndroidLeanModern.LampFadeRamp(lean: true, share: 0f, cutoff: 45f, ramp: 30f)).IsEqual(30f);
        AssertThat(AndroidLeanModern.LampFadeRamp(lean: true, share: 0.33f, cutoff: 45f, ramp: 30f)).IsEqualApprox(14.85f, 1e-4f);
        AssertThat(AndroidLeanModern.LampFadeRamp(lean: false, share: 0.33f, cutoff: 45f, ramp: 30f)).IsEqual(30f);
        AssertThat(AndroidLeanModern.LampFadeRamp(lean: true, share: 0.05f, cutoff: 0f, ramp: 30f)).IsGreater(0f);
    }

    /// <summary>A shadow bake always covers the modern model's own reach, so raising the setting later never clips a lamp at an old bake.</summary>
    [TestCase]
    public void TheBakeCoversTheModernReach()
    {
        float modern = Engine_LevelLights.ModernBaseReachScale;
        foreach (float setting in new[] { 0.5f, 1.0f, 1.25f, 1.5f })
        {
            float reach = AndroidLeanModern.LampReach(true, setting, modern);
            AssertThat(reach * AndroidLeanModern.BakeReachLift(true, setting, modern)).IsEqualApprox(modern, 1e-5f);
        }

        AssertThat(AndroidLeanModern.BakeReachLift(false, 1.0f, modern)).IsEqual(1f);
    }

    /// <summary>The settings clamp to what a phone can use.</summary>
    [TestCase]
    public void TheSettingsClamp()
    {
        AssertThat(AndroidLeanModern.ClampReach(0.1f)).IsEqual(AndroidLeanModern.MinLampReach);
        AssertThat(AndroidLeanModern.ClampReach(3f)).IsEqual(AndroidLeanModern.MaxLampReach);
        AssertThat(AndroidLeanModern.ClampReach(float.NaN)).IsEqual(AndroidLeanModern.DefaultLampReach);
        AssertThat(AndroidLeanModern.ClampReach(1.2f)).IsEqual(1.2f);
        AssertThat(AndroidLeanModern.ClampFade(-1f)).IsEqual(0f);
        AssertThat(AndroidLeanModern.ClampFade(0f)).IsEqual(0f);
        AssertThat(AndroidLeanModern.ClampFade(0.01f)).IsEqual(0.05f);
        AssertThat(AndroidLeanModern.ClampFade(2f)).IsEqual(1f);
    }

    /// <summary>Off a phone every lamp hook returns what it was given.</summary>
    [TestCase]
    public void TheDesktopLampsAreUntouched()
    {
        AssertThat(AndroidLeanModern.LampsLean).IsFalse();
        AssertThat(AndroidLeanModern.LampReach(Engine_LevelLights.ModernBaseReachScale)).IsEqual(Engine_LevelLights.ModernBaseReachScale);
        AssertThat(AndroidLeanModern.BakeReachLift(true, Engine_LevelLights.ModernBaseReachScale)).IsEqual(1f);
        AssertThat(AndroidLeanModern.LampFadeCutoff(36f, authored: true)).IsEqual(36f);
        AssertThat(AndroidLeanModern.LampFadeRamp(36f, 18f)).IsEqual(18f);
        // And none of them read the lamp settings: the config is never asked off a phone.
        AssertThat((int)(typeof(AndroidLeanModern).GetField("_settingsGeneration", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null) ?? 0)).IsEqual(int.MinValue);
    }

    /// <summary>The hooks stand where the modern model scales a lamp's and an effect light's reach and fade.</summary>
    [TestCase]
    public void TheHooksAreWired()
    {
        string lights = FileText("Godot/src/Engine/Lighting/Engine_LevelLights.cs");
        AssertThat(lights).Contains("Engine_LightingFrame.ModernModel ? Refii.AndroidRuntime.AndroidLeanModern.LampReach(ModernBaseReachScale) : 1.0f");
        AssertThat(lights).Contains("authoredReach *= Refii.AndroidRuntime.AndroidLeanModern.BakeReachLift(Engine_LightingFrame.ModernModel, ModernBaseReachScale);");
        AssertThat(lights).Contains("cutoff = Refii.AndroidRuntime.AndroidLeanModern.LampFadeCutoff(cutoff, authored);");
        AssertThat(lights).Contains("ramp = Refii.AndroidRuntime.AndroidLeanModern.LampFadeRamp(cutoff, ramp);");
        // The cutoff is shortened before the modern ramp is taken from it, and the share is applied after.
        int cut = lights.IndexOf("LampFadeCutoff(cutoff, authored)", StringComparison.Ordinal);
        int modernRamp = lights.IndexOf("ramp = MathF.Max(ramp * 2.0f, cutoff * 0.5f);", StringComparison.Ordinal);
        int share = lights.IndexOf("LampFadeRamp(cutoff, ramp)", StringComparison.Ordinal);
        AssertThat(cut < modernRamp && modernRamp < share).IsTrue();
        AssertThat(FileText("Godot/src/Engine/fx/FableFx.cs"))
            .Contains("? Refii.AndroidRuntime.AndroidLeanModern.LampReach(Refii.Lighting.Engine_LevelLights.ModernBaseReachScale)");
    }

    /// <summary>The keys are phone Tuning rows at the code's defaults, with a player's names.</summary>
    [TestCase]
    public void TheKeysAreRegistered()
    {
        foreach ((string key, string value) in new[]
                 {
                     (AndroidLeanModern.Key, "true"),
                     (AndroidLeanModern.LampReachKey, "1"),
                     (AndroidLeanModern.LampFadeKey, "0"),
                 })
        {
            RuntimeCVarDefinition? row = RuntimeCVarsAndroid.Tuning.SingleOrDefault(d => d.Name == key);
            AssertThat(row).OverrideFailureMessage(key + " is not a Tuning row").IsNotNull();
            AssertThat(row!.DefaultValue).IsEqual(value);
            AssertThat(AndroidSettingNames.Keys.Contains(key)).OverrideFailureMessage(key + " has no player's name").IsTrue();
        }

        AssertThat(float.Parse("1", System.Globalization.CultureInfo.InvariantCulture)).IsEqual(AndroidLeanModern.DefaultLampReach);
    }
}
