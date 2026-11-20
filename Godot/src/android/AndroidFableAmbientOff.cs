using System;
using System.Collections.Generic;
using Godot;
using Refii.Core;

namespace Refii.AndroidRuntime;

/// <summary>
/// `lite_fable_ambient_off`: under the fable lighting model the fable-model lite builds are compiled
/// with <c>ambient_light_disabled</c>, which drops Godot's ambient block (scene_forward_mobile.glsl:
/// the ambient flag test and load, the specular occlusion, the environment BRDF). Fable 2 only. Off by
/// default; read once per process.
/// </summary>
/// <remarks>
/// THE SAME PICTURE (checked by reading, 2026-11-16). Every world a lite surface is drawn in under the
/// fable model adds no Godot ambient:
/// - the level: Engine_LevelLighting.ConfigureEnvironment sets the source Disabled, energy 0;
/// - the far-tree card capture (AndroidTreeCards) copies the level's ambient, so Disabled too;
/// - the GUI figure stage (co-op figures, item previews, grey 0.3) dresses every surface with its own
///   StandardMaterial3D (Engine_UiStageMaterial), so no lite build is drawn there;
/// - the UI, effect and highlight worlds set their ambient Disabled and draw no lite builds.
/// Fable 3 is left out: its map table (Fable3MapTable) draws world models under a 0.55 grey ambient.
/// A world with no Environment gets Godot's clear-colour ambient; none draws lite builds once a level
/// is up. <see cref="Watch"/> logs once if the level's environment is ever found adding ambient.
/// </remarks>
public static class AndroidFableAmbientOff
{
    /// <summary>The refii.toml setting.</summary>
    public const string Key = "lite_fable_ambient_off";

    /// <summary>Whether the values ask for it; unset is off. Pure.</summary>
    public static bool Wanted(IReadOnlyDictionary<string, string> values) => Engine_RefiiConfig.Bool(values, Key, false);

    /// <summary>The process decision: a phone, the key on, the fable model, Fable 2. Pure.</summary>
    public static bool ChosenFor(bool android, bool wanted, bool modernModel, bool fable2) =>
        android && wanted && !modernModel && fable2;

    /// <summary>Whether a build takes the word: the key on, the process under the fable model, a fable (not lite/modern/) build. Pure.</summary>
    public static bool Applies(bool on, bool modernModel, bool modernBuild) => on && !modernModel && !modernBuild;

    /// <summary><paramref name="code"/> with the word added when <see cref="Applies"/>. Pure.</summary>
    public static string Compose(string code, bool on, bool modernModel, bool modernBuild) =>
        Applies(on, modernModel, modernBuild)
            ? Refii.Models.Engine_ShaderSpecialisation.WithRenderMode(code, AndroidModernAmbientFold.RenderModeWord)
            : code;

    /// <summary>
    /// Whether an environment adds ambient to a lit surface (render_scene_data_rd.cpp): Disabled adds
    /// nothing, a colour or sky adds its colour times energy, Background adds the background colour. Pure.
    /// </summary>
    public static bool AddsAmbient(Godot.Environment.AmbientSource source, Color colour, float energy, Color background) =>
        source switch
        {
            Godot.Environment.AmbientSource.Disabled => false,
            Godot.Environment.AmbientSource.Bg => background.R > 0f || background.G > 0f || background.B > 0f,
            Godot.Environment.AmbientSource.Sky => energy > 0f,
            _ => energy > 0f && (colour.R > 0f || colour.G > 0f || colour.B > 0f),
        };

    private static bool _warned;

    /// <summary>
    /// Main thread, once a frame with the level's environment: logs once when the setting is on and that
    /// environment adds ambient, which the lite surfaces then draw without. Changes nothing.
    /// </summary>
    public static void Watch(Godot.Environment environment)
    {
        if (_warned || !On || !AddsAmbient(environment.AmbientLightSource, environment.AmbientLightColor,
                environment.AmbientLightEnergy, environment.BackgroundColor))
        {
            return;
        }

        _warned = true;
        GD.Print($"[shader] {Key}: the level's environment adds ambient ({environment.AmbientLightSource}, "
            + $"energy {environment.AmbientLightEnergy:0.###}); lite surfaces draw without it here");
    }

    private static readonly Lazy<bool> Chosen = new(() =>
    {
        try
        {
            return ChosenFor(Engine_Platform.IsAndroid, Wanted(Engine_RefiiConfig.Read(out _)),
                Refii.Lighting.Engine_LightingFrame.ModernModel, Engine_Title.Active == Engine_Title.Fable2);
        }
        catch (Exception)
        {
            return false;
        }
    }, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Whether this process's fable lite builds take the word (read once).</summary>
    public static bool On => Chosen.Value;
}
