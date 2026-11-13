using System;
using System.Collections.Generic;
using Refii.Core;

namespace Refii.AndroidRuntime;

/// <summary>
/// What the phone's lite surfaces hand from the vertex stage to the pixel stage (fable_lite_varyings.gdshaderinc
/// beside this file, included by fable_lighting.gdshaderinc in every lite build): seven settings, composed into
/// every lite build, the model surfaces and the ground alike (Engine_ShaderVariant); read once per process.
/// </summary>
/// <remarks>
/// <para>WHY. A tile GPU writes every vertex's outputs to memory for its binning pass and interpolates them per
/// pixel, and the S10e's scenes run about two triangles per pixel, so the components a vertex hands on cost more
/// than the instructions that use them: the fog moved to the vertex stage (six more components) measured 12%
/// slower on an Adreno 640 for ~100 fewer pixel instructions.</para>
/// <para>`lite_trim_varyings` (on): what no pixel reads, gone; the same picture bit for bit. A solid prop's
/// tree fade (1), the ground's page rectangle outside the vista pages (4, and the CUSTOM0 fetch), a vista page's
/// ground position and normal (6), the normal of a flat ground chunk with no cliff (3); with `lite_bake_variants`,
/// UV2 in a build for a bake mode other than amb3 (2) and the fable model's PRT transfer where it is the constant
/// zero (4); the tree fade, one value per copy, flat.</para>
/// <para>`lite_pack_varyings` (on, modern model): the props' scalars in COLOR's rgb, which the modern model never
/// reads: the height above the ground (1) and the tree fade (1), or the vertex ground AO (4) under
/// `lite_ground_ao = "fast"`. The same picture: COLOR crosses at Godot's half precision, and the height only gates
/// a fetch, with 0.45 m to spare.</para>
/// <para>`lite_fragment_world` (off): the world position (3) rebuilt per pixel from Godot's view-space position
/// (props) or the ground's own (terrain), about a dozen pixel instructions. The same picture to float rounding
/// (within 2/255 on a few pixels 3 km from the origin).</para>
/// <para>`lite_terrain_view_frame` (off): the ground's model-space position and normal (6) rebuilt per pixel from
/// Godot's; with `lite_fragment_world` the ground carries only Godot's own position and normal. For a ground whose
/// transform is a rotation, mirror and uniform scale, as every level's is; within 2/255 near the origin, 7/255 on
/// a few cliff pixels 2 km out.</para>
/// <para>`lite_mediump_varyings` (off): the small-range varyings that remain (fog terms, ground AO and heights,
/// the PRT transfer, the ground's normal) declared mediump: SPIR-V RelaxedPrecision on the interface, half-width
/// storage where the GPU packs it. Nearly the same picture.</para>
/// <para>`lite_prop_fragment_world` (off): `lite_fragment_world` for the models only; the ground keeps its world
/// position varying (rebuilding it there holds three more registers through the splat loop for no step below).</para>
/// <para>`lite_pack_lightmap_uv` (off, modern model with `lite_pack_varyings`): a solid prop's UV2 (the amb3
/// lightmap's) in COLOR.gb, free there (no fade under trim, the height in r, the vertex AO in a); not with the vertex
/// ground AO, which takes b. Within 1/60 of a texel of the 64x64 object lightmap (COLOR crosses at half precision).</para>
/// <para>WHY THE LAST TWO. On the S10e (combo17, lean modern, Low) the GPU time per frame follows the static ALU of the
/// prop and ground fragments linearly (about 0.0126 Mcycles per instruction removed) except for one step: about
/// 1.2 Mcycles (7%) more when a prop's pixel reads at most 13-16 interpolated components (normal maps and baked AO
/// off: 13; every other measured build 17-24). With `lite_normal_maps = "fast"` and `lite_prop_fragment_world` a
/// prop reads 14, and with `lite_pack_lightmap_uv` 12, keeping the normal maps and the ground AO.</para>
/// </remarks>
public static class AndroidLeanVaryings
{
    public const string TrimKey = "lite_trim_varyings";
    public const string PackKey = "lite_pack_varyings";
    public const string FragmentWorldKey = "lite_fragment_world";
    public const string TerrainViewFrameKey = "lite_terrain_view_frame";
    public const string MediumpKey = "lite_mediump_varyings";
    public const string PropFragmentWorldKey = "lite_prop_fragment_world";
    public const string PackLightmapUvKey = "lite_pack_lightmap_uv";

    public const string TrimDefine = "FABLE_LITE_TRIM_VARYINGS";
    public const string PackDefine = "FABLE_LITE_PACK_VARYINGS";
    public const string FragmentWorldDefine = "FABLE_LITE_FRAGMENT_WORLD";
    public const string TerrainViewFrameDefine = "FABLE_LITE_TERRAIN_VIEW_FRAME";
    public const string MediumpDefine = "FABLE_LITE_MEDIUMP_VARYINGS";
    public const string PropFragmentWorldDefine = "FABLE_LITE_PROP_FRAGMENT_WORLD";
    public const string PackLightmapUvDefine = "FABLE_LITE_PACK_LIGHTMAP_UV";

    /// <summary>The seven settings as they stand.</summary>
    public readonly record struct Levers(bool Trim, bool Pack, bool FragmentWorld, bool TerrainViewFrame, bool Mediump,
        bool PropFragmentWorld = false, bool PackLightmapUv = false);

    /// <summary>Unset: the two that keep the picture bit for bit on, the three that trade pixel work or precision off.</summary>
    public static readonly Levers Defaults = new(true, true, false, false, false, false, false);

    /// <summary>The settings the values ask for; an unset key is its default. Pure.</summary>
    public static Levers Wanted(IReadOnlyDictionary<string, string> values) => new(
        Engine_RefiiConfig.Bool(values, TrimKey, Defaults.Trim),
        Engine_RefiiConfig.Bool(values, PackKey, Defaults.Pack),
        Engine_RefiiConfig.Bool(values, FragmentWorldKey, Defaults.FragmentWorld),
        Engine_RefiiConfig.Bool(values, TerrainViewFrameKey, Defaults.TerrainViewFrame),
        Engine_RefiiConfig.Bool(values, MediumpKey, Defaults.Mediump),
        Engine_RefiiConfig.Bool(values, PropFragmentWorldKey, Defaults.PropFragmentWorld),
        Engine_RefiiConfig.Bool(values, PackLightmapUvKey, Defaults.PackLightmapUv));

    /// <summary>The defines a lite build takes for <paramref name="levers"/>, in order. Pure.</summary>
    public static string[] DefinesFor(Levers levers)
    {
        var defines = new List<string>(7);
        if (levers.Trim) defines.Add(TrimDefine);
        if (levers.Pack) defines.Add(PackDefine);
        if (levers.FragmentWorld) defines.Add(FragmentWorldDefine);
        if (levers.TerrainViewFrame) defines.Add(TerrainViewFrameDefine);
        if (levers.Mediump) defines.Add(MediumpDefine);
        if (levers.PropFragmentWorld && !levers.FragmentWorld) defines.Add(PropFragmentWorldDefine);
        if (levers.PackLightmapUv && levers.Pack) defines.Add(PackLightmapUvDefine);
        return defines.ToArray();
    }

    private static readonly Lazy<Levers> Chosen = new(() =>
    {
        if (!Engine_Platform.IsAndroid)
        {
            return default;
        }

        try
        {
            return Wanted(Engine_RefiiConfig.Read(out _));
        }
        catch (Exception)
        {
            return default;
        }
    }, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>This process's settings (read once, as the variant is).</summary>
    public static Levers On => Chosen.Value;

    /// <summary>This process's defines, for Engine_ShaderVariant's process-wide list.</summary>
    public static string[] Defines() => DefinesFor(On);
}
