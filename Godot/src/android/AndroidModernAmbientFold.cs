using System;
using System.Collections.Generic;
using Godot;
using Refii.Core;

namespace Refii.AndroidRuntime;

/// <summary>
/// `android_modern_ambient_fold`: the phone's modern-model lite builds compiled with
/// <c>ambient_light_disabled</c>, the shader adding Godot's ambient back itself from the global
/// <see cref="GlobalName"/> (fable_lighting.gdshaderinc, FABLE_LITE_AMBIENT_FOLD). Off by default;
/// it only matters when the modern model runs on a phone (`android_modern_lighting = true`), as only
/// the lite/modern/ builds take it (Engine_ShaderVariant.Compose).
/// </summary>
/// <remarks>
/// WHY. Under the modern model Godot's own ambient is live (Engine_LevelLighting: a Color source,
/// the GI's half of the bake). In the Mobile renderer that path is the ambient block of
/// scene_forward_mobile.glsl, and the render mode that removes it is a compile-time word: the
/// lite build carries none of it, and its emission adds the one product it produced.
/// <para>THE SAME PICTURE. With a Color source, no reflection probes, no lightmap and no GI (the
/// Mobile renderer has none of SDFGI or VoxelGI), Godot's ambient is
/// <c>srgb_to_linear(colour) * energy</c> (render_scene_data_rd.cpp), then
/// <c>*= ao</c>, <c>*= albedo</c>, <c>*= 1 - metallic</c>, summed with the emission before fog,
/// with the emission's exposure normalisation 1 (CameraAttributesPractical, no physical units).
/// The body adds exactly that from its final ALBEDO, AO and METALLIC. The reflection the block
/// also carries is off (<c>ReflectedLightSource = Disabled</c>), so nothing else is lost. Where
/// the products differ: a Godot decal mixes its own albedo (and ORM) in after the material's
/// fragment, so on a decal's pixels the folded ambient shades the surface under it; and a lite
/// surface drawn in another world (the UI's figure stage) takes the level's ambient, not that
/// world's.</para>
/// <para>THE ENVIRONMENT KEEPS ITS AMBIENT. Every surface that is not a lite modern build (the full
/// shaders a material keeps, fur, StandardMaterial3D) still reads it, and the lite builds no longer
/// can, so it is neither doubled nor lost.</para>
/// Read once per process, as the shader variant is: the next start applies a change.
/// </remarks>
public static class AndroidModernAmbientFold
{
    /// <summary>The refii.toml setting.</summary>
    public const string Key = "android_modern_ambient_fold";

    /// <summary>The define that adds the folded ambient to the lite bodies' emission.</summary>
    public const string Define = "FABLE_LITE_AMBIENT_FOLD";

    /// <summary>The render-mode word that compiles Godot's ambient out of those builds.</summary>
    public const string RenderModeWord = "ambient_light_disabled";

    /// <summary>The global the shaders read: the environment's ambient as the renderer would.</summary>
    public const string GlobalName = "fable_godot_ambient";

    // Made at first use, not in the type's initialiser: the pure members run without the engine.
    private static StringName? _global;

    /// <summary>Whether the key asks for the fold, given the runtime configuration's values.</summary>
    public static bool Wanted(IReadOnlyDictionary<string, string> values) => Engine_RefiiConfig.Bool(values, Key, false);

    private static readonly Lazy<bool> Chosen = new(() =>
    {
        if (!Engine_Platform.IsAndroid)
        {
            return false;
        }

        try
        {
            return Wanted(Engine_RefiiConfig.Read(out _));
        }
        catch (Exception)
        {
            return false;
        }
    }, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Whether this process's modern lite builds fold the ambient (read once).</summary>
    public static bool On => Chosen.Value;

    /// <summary>
    /// The renderer's ambient for <paramref name="environment"/>'s state: the Color source's
    /// <c>srgb_to_linear(colour) * energy</c>, zero for any other source. Pure.
    /// </summary>
    public static Vector3 Ambient(Godot.Environment.AmbientSource source, Color colour, float energy)
    {
        if (source != Godot.Environment.AmbientSource.Color)
        {
            return Vector3.Zero;
        }

        Color linear = colour.SrgbToLinear();
        return new Vector3(linear.R * energy, linear.G * energy, linear.B * energy);
    }

    /// <summary>
    /// The lighting's hook, after it sets the environment's ambient: publishes it to the folded
    /// builds. Nothing when the fold is off.
    /// </summary>
    public static void Publish(Godot.Environment environment)
    {
        if (!On)
        {
            return;
        }

        RenderingServer.GlobalShaderParameterSet(_global ??= GlobalName,
            Ambient(environment.AmbientLightSource, environment.AmbientLightColor, environment.AmbientLightEnergy));
    }
}
