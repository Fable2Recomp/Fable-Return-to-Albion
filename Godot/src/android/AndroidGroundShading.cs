using System;
using System.Collections.Generic;
using Refii.Core;

namespace Refii.AndroidRuntime;

/// <summary>
/// `lite_ground_ao`: how the modern lighting's baked ground-contact shadow is fetched on a phone, and
/// `lite_ground_ao_gather`: how its taps are read; composed into the lite builds as the defines
/// fable_lighting.gdshaderinc and fable_lite_ground_ao.gdshaderinc name (Engine_ShaderVariant); read once,
/// as the variant is, so a change applies at the next start.
/// </summary>
/// <remarks>
/// <para>WHAT EACH PIECE COSTS PER PIXEL (fable_lighting's helpers; the map is the RGBA8 ground-contact
/// map, R the terrain's AO, G the walkable floor's, B/A a 16-bit floor height, never mipmapped; SPIR-V
/// path counts of the lean Low builds, 2026-11-12):</para>
/// <list type="bullet">
/// <item>the terrain's contact AO: one level-0 fetch at the texel centres from a computed coordinate,
/// about 27 ALU with the coordinate, the crop tests and the AO's combination;</item>
/// <item>a prop's: four texel fetches, each tap's height decoded and matched to the pixel's
/// (a 0.06-0.42 m smoothstep each), bilinear weights, the weighted floor AO and its fade, about 110 ALU
/// with the coordinate and its tests (near's vertex height, the same taps, about 104 ALU per vertex);</item>
/// <item>the strength: a uniform test and, off the default strength of 1, a log2, a multiply and an
/// exp2 on every pixel of both, which `lite_fold_modern_model` folds to its base (FABLE_LITE_FOLD_AO_DEFAULTS);</item>
/// <item>the direct AO: 15 ALU in the sun's call and 23 in a lamp's (10 and 12 with the fold) per lit light (fable_direct_ao_for_light, and fable_specular_ao where a
/// highlight is drawn), on a varying the light loop does not change.</item>
/// </list>
/// <para>THE LEVELS of <see cref="Key"/>:</para>
/// <list type="bullet">
/// <item><c>full</c>: every prop and ground pixel fetches.</item>
/// <item><c>near</c> (the default): a prop takes its four taps only where its vertices put the pixel
/// within a metre of the walkable floor (their own four taps, in vertex()); a tap more than 0.42 m
/// from the pixel's height weighs exactly nothing, so walls, roofs and the upper parts of everything
/// skip the taps for the same picture. Exact wherever the floor under a triangle stays within 0.58 m
/// of what its vertices interpolate; a floor that steps further under one large face (a stair, a
/// ledge) can lose its contact shadow there (FABLE_LITE_NEAR_GROUND_AO).</item>
/// <item><c>fast</c>: props take the taps per vertex and interpolate the floor's AO and their height
/// above it (FABLE_LITE_VERTEX_GROUND_AO); the height fade stays per pixel. Small props are as they
/// were; along a long wall the contact shadow is a blend of its ends.</item>
/// <item><c>fastest</c>: that, and the ground per vertex too (FABLE_LITE_VERTEX_TERRAIN_AO). The
/// ground's grid is a metre at its finest and metres wide where its coarser levels flatten it, against
/// a map texel of a few centimetres to a few tens of them, so the shadow at a wall's foot softens.</item>
/// <item><c>off</c>: no contact shadow on either (FABLE_LITE_NO_PROP_GROUND_AO, FABLE_LITE_NO_TERRAIN_GROUND_AO);
/// the models' own baked AO stays.</item>
/// </list>
/// <para><see cref="GatherKey"/> (FABLE_LITE_GROUND_AO_GATHER): wherever a prop reads the four taps (near's
/// pixel and vertex, fast's vertex), three textureGather of the map's G, B and A for four
/// texel fetches (two in near's vertex, which reads no G) and one coordinate for four clamped
/// ones: the same texels, weights and arithmetic, so the same picture (0/255 measured, maps of 64 to 1024
/// texels). About 8 ALU and a texture read fewer per near pixel; per vertex 8 ALU fewer and two reads for
/// four (near) or three for four (fast, fastest).</para>
/// <para>ONLY WHERE THE AO IS COMPILED: under the fable model, or with baked AO off, the helpers are
/// constants (Engine_ShaderVariant.NoBakedAoFor) and nothing is added, so those builds keep their text.</para>
/// </remarks>
public static class AndroidGroundShading
{
    /// <summary>refii.toml's key.</summary>
    public const string Key = "lite_ground_ao";

    public const string Full = "full";
    public const string Near = "near";
    public const string Fast = "fast";
    public const string Fastest = "fastest";
    public const string Off = "off";

    /// <summary>The key's default: the same picture.</summary>
    public const string Default = Near;

    public const string NearDefine = "FABLE_LITE_NEAR_GROUND_AO";
    public const string VertexDefine = "FABLE_LITE_VERTEX_GROUND_AO";
    public const string VertexTerrainDefine = "FABLE_LITE_VERTEX_TERRAIN_AO";
    public const string NoPropDefine = "FABLE_LITE_NO_PROP_GROUND_AO";
    public const string NoTerrainDefine = "FABLE_LITE_NO_TERRAIN_GROUND_AO";

    /// <summary>refii.toml's key for the gathered taps (default off).</summary>
    public const string GatherKey = "lite_ground_ao_gather";
    public const string GatherDefine = "FABLE_LITE_GROUND_AO_GATHER";

    /// <summary>The key's value as a level; anything unknown is the default (a stored <c>one_tap</c>, a level since removed, too). Pure.</summary>
    public static string Parse(string value) => value.Trim().Trim('"').ToLowerInvariant() switch
    {
        Full => Full,
        Fast => Fast,
        Fastest => Fastest,
        Off => Off,
        _ => Default,
    };

    /// <summary>The level the values ask for. Pure.</summary>
    public static string Level(IReadOnlyDictionary<string, string> values) =>
        Parse(Engine_RefiiConfig.String(values, Key, Default));

    /// <summary>Whether the values ask for the gathered taps; unset is off. Pure.</summary>
    public static bool GatherWanted(IReadOnlyDictionary<string, string> values) =>
        Engine_RefiiConfig.Bool(values, GatherKey, false);

    /// <summary>
    /// The defines for <paramref name="level"/>, given whether the baked AO is compiled at all, and the
    /// gathered taps' where the level reads taps in a prop (not full or off). Pure.
    /// </summary>
    public static string[] Defines(string level, bool aoCompiled, bool gather = false)
    {
        if (!aoCompiled)
        {
            return Array.Empty<string>();
        }

        var defines = new List<string>();
        switch (Parse(level))
        {
            case Near:
                defines.Add(NearDefine);
                break;
            case Fast:
                defines.Add(VertexDefine);
                break;
            case Fastest:
                defines.Add(VertexDefine);
                defines.Add(VertexTerrainDefine);
                break;
            case Off:
                defines.Add(NoPropDefine);
                defines.Add(NoTerrainDefine);
                break;
        }

        if (gather && defines.Count > 0 && !defines.Contains(NoPropDefine))
        {
            defines.Add(GatherDefine);
        }

        return defines.ToArray();
    }

    private static readonly Lazy<string[]> Chosen = new(() =>
    {
        if (!Engine_Platform.IsAndroid)
        {
            return Array.Empty<string>();
        }

        try
        {
            IReadOnlyDictionary<string, string> values = Engine_RefiiConfig.Read(out _);
            bool compiled = !Refii.Models.Engine_ShaderVariant.NoBakedAoFor(
                Refii.Lighting.Engine_LevelAmbientOcclusion.BakedAoEnabled, Refii.Lighting.Engine_LightingFrame.ModernModel,
                Refii.Models.Engine_ShaderVariant.FableNoBakedAoOn);
            return Defines(Level(values), compiled, GatherWanted(values));
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>This process's AO defines for the lite builds; read once.</summary>
    public static IEnumerable<string> ProcessDefines() => Chosen.Value;
}
