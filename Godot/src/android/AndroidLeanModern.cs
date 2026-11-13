using System;
using Refii.Core;

namespace Refii.AndroidRuntime;

/// <summary>
/// `android_lean_modern` (on by default): under the modern lighting model a phone keeps the classic
/// model's particle reach (simulation frozen past 50 m, not 200 m), its distance fade (the FX
/// system's and the game-fed groups' and trails'), its particle light cutoff and no default halo
/// per particle light (the phone's post chain never draws halos), and draws entities without
/// batches (AndroidGameHost). Measured on a Galaxy S10e, modern with the 200 m reach cost 5 ms more
/// main thread in particles than fable, and batches 6-7 ms of GPU.
/// </summary>
/// <remarks>
/// <para>THE LAMPS. The modern model lights every placed lamp and effect light at twice its energy
/// and half again its reach (Engine_LevelLights.ModernBaseEnergyScale, ModernBaseReachScale), and
/// fades a lamp that states a fade at twice its stated distance (Engine_LevelLights.ApplyDistanceFade:
/// <c>rawFade x 2 x placed_light_reach_scale x placed_light_range / 60</c>, over at least half of it).
/// A lamp's pixel cost goes with the ground it reaches, so on a phone the reach is
/// <see cref="LampReachKey"/> (<see cref="DefaultLampReach"/>: the shipped reach, the fable model's)
/// and a stated fade is shortened by the same share. The energy stays doubled, so the night keeps the
/// modern model's bright pools around each lamp. These are what the phone tests had been getting
/// from the global <c>placed_light_reach_scale = 0.667</c> (modern 1.5 x 0.667 = 1.0), which also
/// shortened the fable model's lamps; this leaves the fable model alone.</para>
/// <para><see cref="LampFadeKey"/>: the modern model fades a lamp over at least half its fade
/// distance (a lamp with no stated fade from 15 m to placed_light_range's 45 m on the phone presets),
/// so a lamp across a square is already dim; above 0 the fade takes that share of the distance
/// instead. 0 (default) keeps the modern model's.</para>
/// Read live: a change re-applies at the lamps' next scale refresh (any lighting setting's change).
/// </remarks>
public static class AndroidLeanModern
{
    public const string Key = "android_lean_modern";

    /// <summary>refii.toml's lamp reach under lean modern, a multiple of the shipped reach.</summary>
    public const string LampReachKey = "android_lean_modern_lamp_reach";

    /// <summary>refii.toml's share of a lamp's fade distance it fades over under lean modern; 0 is the modern model's.</summary>
    public const string LampFadeKey = "android_lean_modern_lamp_fade";

    /// <summary>The shipped reach: what the phone tests ran (1.5 x 0.667).</summary>
    public const float DefaultLampReach = 1.0f;

    public const float MinLampReach = 0.5f;

    /// <summary>The modern model's own reach: a phone gains nothing past it.</summary>
    public const float MaxLampReach = 1.5f;

    private static bool? _fx;

    /// <summary>Whether particles keep the classic reach and fade under modern lighting; read once.</summary>
    public static bool Fx => _fx ??= Engine_Platform.IsAndroid && Read();

    /// <summary>
    /// Whether game-fed particle groups and trails drop the GPU distance fade (Engine_ClassicParticles):
    /// the modern model's own shaders only, and not under <see cref="Fx"/>, which keeps fable's fade.
    /// </summary>
    public static bool ParticleFadeOff(bool classicActive, bool modernModel) => ParticleFadeOff(classicActive, modernModel, Fx);

    /// <summary><see cref="ParticleFadeOff(bool, bool)"/> with the setting given. Pure.</summary>
    public static bool ParticleFadeOff(bool classicActive, bool modernModel, bool leanFx) => !classicActive && modernModel && !leanFx;

    // ---- The lamps -------------------------------------------------------------------------

    /// <summary>Whether this process's lamps take the lean reach and fade: a phone, the modern model, the key.</summary>
    public static bool LampsLean => Fx && Refii.Lighting.Engine_LightingFrame.ModernModel;

    /// <summary>
    /// The modern model's reach multiplier for placed lamps and effect lights: <paramref name="modernReach"/>
    /// (Engine_LevelLights.ModernBaseReachScale) off a phone, the setting under lean modern.
    /// </summary>
    public static float LampReach(float modernReach) =>
        Engine_Platform.IsAndroid && LampsLean ? LampReach(true, LampReachSetting, modernReach) : modernReach;

    /// <summary><see cref="LampReach(float)"/> with the state given. Pure.</summary>
    public static float LampReach(bool lean, float setting, float modernReach) => lean ? setting : modernReach;

    /// <summary>
    /// What a lamp's shadow bake range is lifted by so a bake covers the modern model's own reach
    /// whatever the setting (Engine_LevelLights.ComputeMaxBakeRange): a bake made at a short reach would
    /// otherwise darken a lamp past it once the reach is raised. 1 off a phone or under fable.
    /// </summary>
    public static float BakeReachLift(bool modernModel, float modernReach) =>
        Engine_Platform.IsAndroid && Fx && modernModel ? BakeReachLift(true, LampReachSetting, modernReach) : 1f;

    /// <summary><see cref="BakeReachLift(bool, float)"/> with the state given. Pure.</summary>
    public static float BakeReachLift(bool lean, float setting, float modernReach) =>
        lean && setting > 0f ? MathF.Max(1f, modernReach / setting) : 1f;

    /// <summary>
    /// A modern lamp's fade cutoff (Engine_LevelLights.ApplyDistanceFade): one it states is shortened by
    /// the reach's share of the modern reach (its x2 is there for the same reason as the reach's x1.5);
    /// the view distance a lamp with none fades at is left.
    /// </summary>
    public static float LampFadeCutoff(float cutoff, bool authored) =>
        Engine_Platform.IsAndroid && LampsLean
            ? LampFadeCutoff(true, LampReachSetting, Refii.Lighting.Engine_LevelLights.ModernBaseReachScale, cutoff, authored)
            : cutoff;

    /// <summary><see cref="LampFadeCutoff(float, bool)"/> with the state given. Pure.</summary>
    public static float LampFadeCutoff(bool lean, float setting, float modernReach, float cutoff, bool authored) =>
        lean && authored && modernReach > 0f ? cutoff * (setting / modernReach) : cutoff;

    /// <summary>A modern lamp's fade length: <see cref="LampFadeKey"/>'s share of the cutoff when set, else as given.</summary>
    public static float LampFadeRamp(float cutoff, float ramp) =>
        Engine_Platform.IsAndroid && LampsLean ? LampFadeRamp(true, LampFadeSetting, cutoff, ramp) : ramp;

    /// <summary><see cref="LampFadeRamp(float, float)"/> with the state given. Pure.</summary>
    public static float LampFadeRamp(bool lean, float share, float cutoff, float ramp) =>
        lean && share > 0f ? MathF.Max(cutoff * share, 0.01f) : ramp;

    /// <summary>The reach setting, clamped to <see cref="MinLampReach"/>..<see cref="MaxLampReach"/>. Pure.</summary>
    public static float ClampReach(float value) =>
        float.IsFinite(value) ? Math.Clamp(value, MinLampReach, MaxLampReach) : DefaultLampReach;

    /// <summary>The fade share setting: 0 (the modern model's), or 0.05..1. Pure.</summary>
    public static float ClampFade(float value) =>
        !float.IsFinite(value) || value <= 0f ? 0f : Math.Clamp(value, 0.05f, 1f);

    private static int _settingsGeneration = int.MinValue;
    private static float _lampReach = DefaultLampReach;
    private static float _lampFade;

    private static float LampReachSetting
    {
        get
        {
            RefreshSettings();
            return _lampReach;
        }
    }

    private static float LampFadeSetting
    {
        get
        {
            RefreshSettings();
            return _lampFade;
        }
    }

    /// <summary>Re-reads both lamp settings when the config moved (a live set or the file): asked per effect light per frame.</summary>
    private static void RefreshSettings()
    {
        int generation = Engine_Switches.Generation;
        if (generation == _settingsGeneration) return;
        try
        {
            var values = Engine_RefiiConfig.Read(out _);
            _lampReach = ClampReach(Engine_RefiiConfig.Float(values, LampReachKey, DefaultLampReach));
            _lampFade = ClampFade(Engine_RefiiConfig.Float(values, LampFadeKey, 0f));
        }
        catch (Exception)
        {
            _lampReach = DefaultLampReach;
            _lampFade = 0f;
        }

        // Read can bump the generation itself when it re-parses the file: stamp after it.
        _settingsGeneration = Engine_Switches.Generation;
    }

    private static bool Read()
    {
        try
        {
            return Engine_RefiiConfig.Bool(Engine_RefiiConfig.Read(out _), Key, true);
        }
        catch (Exception)
        {
            return true;
        }
    }
}
