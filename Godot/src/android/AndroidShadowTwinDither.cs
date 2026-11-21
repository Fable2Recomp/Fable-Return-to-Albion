using System;
using System.Runtime.CompilerServices;
using Godot;
using Refii.Core;

namespace Refii.AndroidRuntime;

/// <summary>
/// `prop_shadow_twin_whole`: a prop batch's cast-only twin (Engine_PropShadow) draws with
/// shadow_caster_whole.gdshader, which has no fade dither, while the twin has no fade band and has
/// finished arriving. Then the dither in shadow_caster.gdshader never discards (its fade is 1), so the
/// shadow map is the same; only the discard leaves the shadow pass. Off by default; read once.
/// </summary>
/// <remarks>
/// Only twins whose merged mesh is the one caster surface (no alpha-tested surfaces kept beside it), so
/// a material override covers exactly that surface. The twin goes back to its mesh's material the moment
/// it is given a band or starts an arrival (Engine_PropFade's two writes report here).
/// </remarks>
public static class AndroidShadowTwinDither
{
    /// <summary>The refii.toml setting.</summary>
    public const string Key = "prop_shadow_twin_whole";

    /// <summary>The dither-free caster.</summary>
    public const string ShaderPath = "res://src/android/shadow_caster_whole.gdshader";

    private const string CasterPath = "res://src/Engine/entities/shadow_caster.gdshader";

    /// <summary>Whether the values ask for it; unset is off. Pure.</summary>
    public static bool Wanted(System.Collections.Generic.IReadOnlyDictionary<string, string> values) =>
        Engine_RefiiConfig.Bool(values, Key, false);

    /// <summary>Whether a twin may take the whole caster: one surface, drawn with the dithering caster. Pure.</summary>
    public static bool Eligible(int surfaces, string? surfaceShaderPath) =>
        surfaces == 1 && surfaceShaderPath == CasterPath;

    /// <summary>
    /// Whether the dither can discard nothing: no out band (the shader fades only when its end is above 0)
    /// and an arrival at 1 or none. Pure.
    /// </summary>
    public static bool Whole(float fadeOutEnd, float alpha) => !(fadeOutEnd > 0f) && alpha >= 1f;

    private sealed class State
    {
        public float FadeOutEnd;
        public float Alpha = 1f;
    }

    private static readonly ConditionalWeakTable<GeometryInstance3D, State> Twins = new();

    private static readonly Lazy<bool> Chosen = new(() =>
    {
        try
        {
            return Engine_Platform.IsAndroid && Wanted(Engine_RefiiConfig.Read(out _));
        }
        catch (Exception)
        {
            return false;
        }
    });

    private static readonly Lazy<ShaderMaterial> WholeMaterial = new(() => new ShaderMaterial
    {
        Shader = GD.Load<Shader>(ShaderPath),
    });

    /// <summary>Whether this process uses it (read once).</summary>
    public static bool On => Chosen.Value;

    /// <summary>A new twin, before its band is copied. Main thread.</summary>
    public static void Register(MultiMeshInstance3D twin)
    {
        if (!On || twin.Multimesh?.Mesh is not { } mesh)
        {
            return;
        }

        string? path = mesh.GetSurfaceCount() == 1 && mesh.SurfaceGetMaterial(0) is ShaderMaterial { Shader: { } shader }
            ? shader.ResourcePath
            : null;
        if (!Eligible(mesh.GetSurfaceCount(), path))
        {
            return;
        }

        var state = new State();
        Twins.AddOrUpdate(twin, state);
        Settle(twin, state);
    }

    /// <summary>Engine_PropFade gave <paramref name="node"/> a band.</summary>
    public static void Band(GeometryInstance3D node, float fadeOutEnd)
    {
        if (On && Twins.TryGetValue(node, out State? state))
        {
            state.FadeOutEnd = fadeOutEnd;
            Settle(node, state);
        }
    }

    /// <summary>Engine_PropFade set <paramref name="node"/>'s arrival.</summary>
    public static void Alpha(GeometryInstance3D node, float alpha)
    {
        if (On && Twins.TryGetValue(node, out State? state))
        {
            state.Alpha = alpha;
            Settle(node, state);
        }
    }

    private static void Settle(GeometryInstance3D twin, State state)
    {
        Material? want = Whole(state.FadeOutEnd, state.Alpha) ? WholeMaterial.Value : null;
        if (!ReferenceEquals(twin.MaterialOverride, want))
        {
            twin.MaterialOverride = want;
        }
    }
}
