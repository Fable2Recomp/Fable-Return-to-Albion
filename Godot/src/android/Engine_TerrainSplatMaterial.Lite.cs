using System;
using Godot;

namespace Refii.Level;

/// <summary>The mobile lite variant's terrain splat (Engine_ShaderVariant), and `terrain_normal_maps`.</summary>
public static partial class Engine_TerrainSplatMaterial
{
    /// <summary>
    /// On a mobile-lite splat: the level's cliff band (offset, scale) and whether any layer
    /// states a cliff at all (z = 1). What <see cref="ForChunk"/> proves a chunk flat against.
    /// </summary>
    private static readonly StringName CliffBandMeta = "lite_cliff_band";

    /// <summary>On a chunk mesh: the smallest |n.y| of its vertex normals (Engine_LevelMesh).</summary>
    public static readonly StringName MinUpMeta = "terrain_min_up";

    /// <summary>One no-cliff copy per lite splat: the same parameters and textures, the shader
    /// without its cliff block. Shallow, so no texture is duplicated.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ShaderMaterial, ShaderMaterial> NoCliff = new();

    /// <summary>Loads the lite splat shaders under the lite variant; nothing under "full".</summary>
    private static void WarmLiteShaders()
    {
        if (Refii.Models.Engine_ShaderVariant.Lite)
        {
            foreach (string path in Refii.Models.Engine_ShaderVariant.ActiveVariantPaths)
            {
                if (Refii.Models.Engine_ShaderVariant.IsTerrainSplat(path))
                {
                    _ = Refii.Models.Engine_ShaderSpecialisation.LoadFile(path);
                }
            }
        }
    }

    /// <summary>
    /// The material one terrain chunk draws with: the landscape's own, or — under the lite
    /// variant — its no-cliff copy when no pixel of the chunk can take a cliff.
    /// </summary>
    /// <remarks>
    /// EXACT, NOT A THRESHOLD GUESS. The shader's slope weight is
    /// `1 - saturate(|n.y| x scale + offset)`, and an interpolated, renormalised normal never has a
    /// smaller |n.y| than the smallest of its triangle's vertices (all on one side of the
    /// horizon). So a chunk whose every vertex has `|n.y| x scale + offset >= 1` has a slope
    /// weight of exactly zero at every pixel, the cliff mix is `mix(ground, cliff, 0)`, and
    /// dropping the block changes no bit. The margin covers Godot's normal compression. A level
    /// whose layers state no cliff (every material's z = 0) is flat everywhere.
    /// </remarks>
    internal static Material? ForChunk(Material? material, ArrayMesh mesh)
    {
        if (material is not ShaderMaterial splat || !splat.HasMeta(CliffBandMeta) ||
            Refii.Models.Engine_ShaderVariant.TerrainNoCliffPath(Refii.Models.Engine_ShaderSpecialisation.PathOf(splat.Shader)) is not { } noCliffPath)
        {
            return material;
        }

        Vector3 band = splat.GetMeta(CliffBandMeta).AsVector3();
        bool flat = band.Z < 0.5f;
        if (!flat && band.Y > 0f && mesh.HasMeta(MinUpMeta))
        {
            const float CompressionMargin = 0.002f;
            float minUp = mesh.GetMeta(MinUpMeta).AsSingle() - CompressionMargin;
            flat = (minUp * band.Y) + band.X >= 1f;
        }

        if (!flat)
        {
            return material;
        }

        return NoCliff.GetValue(splat, source =>
        {
            var copy = Refii.Models.Engine_MaterialCopy.Shallow(source);
            // The same specialisations as the splat it copies (Engine_ShaderSpecialisation).
            if (Refii.Models.Engine_ShaderSpecialisation.Load(
                    noCliffPath, Refii.Models.Engine_ShaderSpecialisation.DefinesOf(source.Shader)) is { } shader)
            {
                copy.Shader = shader;
            }

            return copy;
        });
    }

    /// <summary>
    /// Whether a terrain build uploads its layers' normal maps. `terrain_normal_maps = false` leaves
    /// them out on purpose, cavity and parallax with them, for their video memory: 275 MB in
    /// Bowerstone on the S10e, whose GPU samples no BC and holds them as RG8. It saved no measurable
    /// GPU time there. "lite_flat" draws the ground without them, so it does not upload them either.
    /// </summary>
    private static bool NormalMapsWanted() => NormalMapsEnabled() && !Refii.Models.Engine_ShaderVariant.FlatTerrain;

    /// <summary>`terrain_normal_maps`: whether the ground binds its layers' normal maps (and the
    /// cavity and parallax derived from them). Read at each terrain build; default on.</summary>
    internal static bool NormalMapsEnabled()
    {
        try
        {
            return Refii.Core.Engine_RefiiConfig.Bool(Refii.Core.Engine_RefiiConfig.Read(out _), "terrain_normal_maps", true);
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>
    /// THE MOBILE LITE BUILD (Engine_ShaderVariant): the same parameters on a shader with the
    /// modern model's multiplied-away work compiled out — and without the normal-map and cavity
    /// code wherever they were not bound, where dropping it is exact. A no-op under "full".
    /// </summary>
    /// <param name="material">The finished splat, its `cliff_blend` set when the level states one.</param>
    /// <param name="normalsBound">Whether the layers' normal maps were bound.</param>
    private static void ApplyLite(ShaderMaterial material, TerrainSplatData data, Vector4[] parameters, bool normalsBound)
    {
        if (!Refii.Models.Engine_ShaderVariant.Lite)
        {
            return;
        }

        // The level's own band when its header states one (TryBuild), else (-8, 10).
        Variant stated = material.GetShaderParameter("cliff_blend");
        Vector2 cliffBand = stated.VariantType == Variant.Type.Vector2 ? stated.AsVector2() : new Vector2(-8f, 10f);
        if (Refii.Models.Engine_ShaderSpecialisation.Load(
                Refii.Models.Engine_ShaderVariant.TerrainPath(normalsBound),
                Refii.Models.Engine_ShaderSpecialisation.TerrainDefines) is { } lite)
        {
            material.Shader = lite;
        }

        bool anyCliff = false;
        for (int i = 0; i < data.MaterialCount && i < parameters.Length; i++)
        {
            anyCliff |= parameters[i].Z > 0f;
        }

        material.SetMeta(CliffBandMeta, new Vector3(cliffBand.X, cliffBand.Y, anyCliff ? 1f : 0f));
    }

    /// <summary>The shader a terrain page's own material draws (Engine_EhfPages): its lite build under the lite variant.</summary>
    internal static Shader? PageShader() =>
        Refii.Models.Engine_ShaderVariant.Lite
            ? Refii.Models.Engine_ShaderSpecialisation.Load(Refii.Models.Engine_ShaderVariant.PageTerrainPath)
            : GD.Load<Shader>(Refii.Models.Engine_ShaderVariant.PageTerrainPath);
}
