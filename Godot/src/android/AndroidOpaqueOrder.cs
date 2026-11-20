using System;
using System.Collections.Generic;
using Godot;

namespace Refii.AndroidRuntime;

/// <summary>
/// `android_discard_draw_priority` and `android_terrain_draw_priority`: where two classes of opaque
/// surface draw in Godot Mobile's opaque pass, by material render priority. 0, the default, leaves
/// the order as Godot sorts it. Android only; read once at the first material build.
/// </summary>
/// <remarks>
/// GODOT'S OPAQUE ORDER (4.7 forward_mobile, render_forward_mobile.h `sort` union, sorted by
/// `sort_by_key`): render priority first, then shader, material, mesh, surface, and only then a
/// 16-step depth band. Shader order is the shaders' creation order, so which surfaces draw first is
/// close to arbitrary and nothing is front to back. Priority is the one lever a game has without an
/// engine change: a higher one draws later.
/// <para>THE CLASSES. DISCARD: the model surfaces whose shader compiles a `discard` — the two-sided,
/// tree and wind standard shaders (the prop fade dither and the alpha scissor,
/// standard_material_body.gdshaderinc). On Adreno such a draw runs its depth test after the shader
/// and writes no LRZ, so it cannot be rejected per pixel, and what it covers is only rejected early
/// when it drew before. The culled shader (FABLE_SOLID) compiles neither and stays at 0, and the
/// blended translucent pass is left alone: priority also orders the transparent list.
/// TERRAIN: the splat chunks and the far pages, which have no discard and cover most of the screen
/// behind everything else, so drawn late their hidden pixels fail the early depth test.</para>
/// <para>THE SAME PICTURE except where two surfaces of different classes have exactly equal depth:
/// the opaque compare is GREATER_OR_EQUAL (scene_shader_forward_mobile.cpp), so the later draw wins
/// such a tie. Each shader stays in one class, so no extra pipeline or material switches.</para>
/// Which direction pays is the driver's (whether LRZ is built in the binning pass), so both signs
/// are offered; clamped to <see cref="Limit"/> so the fur and overlay depth passes at Godot's
/// minimum and the highlight at its maximum keep their places.
/// </remarks>
public static class AndroidOpaqueOrder
{
    public const string DiscardKey = "android_discard_draw_priority";
    public const string TerrainKey = "android_terrain_draw_priority";

    /// <summary>The largest offset either key takes, either sign.</summary>
    public const int Limit = 16;

    /// <summary>The two offsets the values ask for, clamped. Pure.</summary>
    public static (int Discard, int Terrain) Wanted(IReadOnlyDictionary<string, string> values) =>
        (Math.Clamp(Refii.Core.Engine_RefiiConfig.Int(values, DiscardKey, 0), -Limit, Limit),
         Math.Clamp(Refii.Core.Engine_RefiiConfig.Int(values, TerrainKey, 0), -Limit, Limit));

    /// <summary>
    /// Whether the standard shader at <paramref name="fullPath"/> (a full path, Engine_ShaderVariant.FullPathOf)
    /// compiles a discard and draws in the opaque list. Pure.
    /// </summary>
    public static bool Discards(string fullPath) => fullPath is
        Refii.Models.Engine_ShaderFamily.StandardPath or
        Refii.Models.Engine_ShaderFamily.StandardCutoutCulledPath or
        Refii.Models.Engine_ShaderFamily.StandardTreePath or
        Refii.Models.Engine_ShaderFamily.StandardWindPath;

    /// <summary>The priority a model surface drawn by <paramref name="fullPath"/> takes, or null to leave it. Pure.</summary>
    public static int? ModelPriority(string fullPath, int discardOffset) =>
        discardOffset != 0 && Discards(fullPath) ? discardOffset : null;

    private static readonly Lazy<(int Discard, int Terrain)> Read = new(ReadKeys, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

    private static (int, int) ReadKeys()
    {
        if (!Refii.Core.Engine_Platform.IsAndroid)
        {
            return (0, 0);
        }

        (int Discard, int Terrain) offsets;
        try
        {
            offsets = Wanted(Refii.Core.Engine_RefiiConfig.Read(out _));
        }
        catch (Exception)
        {
            offsets = (0, 0);
        }

        GD.Print($"[material] opaque order: {DiscardKey}={offsets.Discard}, {TerrainKey}={offsets.Terrain} (relaunch to change)");
        return offsets;
    }

    /// <summary>
    /// Gives a finished model material's opaque pass its class's priority (Engine_MdlMaterial, after
    /// the lite and specialised shaders are chosen). Its next passes are left as they are.
    /// </summary>
    public static void Model(Material built)
    {
        if (Read.Value.Discard == 0 || built is not ShaderMaterial { Shader: { } shader } shaded)
        {
            return;
        }

        string path = Refii.Models.Engine_ShaderVariant.FullPathOf(Refii.Models.Engine_ShaderSpecialisation.PathOf(shader));
        if (ModelPriority(path, Read.Value.Discard) is { } priority)
        {
            shaded.RenderPriority = priority;
        }
    }

    /// <summary>Gives a terrain splat or page material the terrain priority.</summary>
    public static void Terrain(Material? material)
    {
        if (Read.Value.Terrain != 0 && material is not null)
        {
            material.RenderPriority = Read.Value.Terrain;
        }
    }
}
