using System;
using System.Collections.Generic;

namespace Refii.Models;

/// <summary>
/// Fable 3's shader selection for the world families, as permutation tokens in the bank's own
/// vocabulary and the guest's rule from those tokens to one entry.
/// </summary>
/// <remarks>
/// THE GUEST SELECTS BY DENSE TABLE, NOT BY NAME. Each family's draw computes an index from its
/// inputs, reads a record number from an index table and the shader id from the record's first
/// word (the entry id the bank names); the record's other words flag which axes the shader fixes.
/// The rules below reproduce those tables exactly (checked over every index of every table
/// against the binary):
///
///   STANDARDMATERIAL, `sub_8220D998` (static), `sub_82210760` (skinned), `sub_822401A0` (SKN3):
///     PS dword_83292BD0[4608 ENV + 2304 SELF + 1152 FR + 576 SH + 288 STSP + 144 SP + 72 CMRPH
///        + 18 AMB + 9 MIST + LIGHTS] -> dword_8329BBD0[11 rec]  (0x8220DB7C..0x8220DC3C)
///     VS dword_83292478[64 SP + 32 FIX + 8 SKN + 4 COL + AMB] -> dword_83292678[6 rec]
///   TREEMATERIAL, `sub_8221BAF0`, with k = 4 (2 (canopy == 0) + SP) + AMB:
///     PS dword_8329C808[18 k + 9 MIST + LIGHTS] -> unk_8329CC88[6 rec]  (0x8221BC00..0x8221BC58)
///     VS dword_8329C4C0[9 k + LIGHTS] -> unk_8329C700[5 rec]
///   REPEATEDMATERIAL, `sub_82CE4C78`:
///     PS dword_83291078[36 NRM + 18 GRASS + 9 MIST + LIGHTS] -> dword_83291198[5 rec]
///     VS dword_83290EE0[8 WIND + 4 NRM + 2 GRASS + COLL] -> dword_83290F20[5 rec]
///
/// THE INPUTS, as the draw states them (offsets on the draw item `a1`, its owner `a1+4`, its
/// material): LIGHTS the light group's count `*(*(a1+0x2C)+0xC)+0x10`, 0..8; AMB the item's +0x24,
/// copied from the owner's +0x2F0 (<see cref="AmbientFor"/>); MIST the item's mist volume; SP the
/// specular texture (material+16) present and |SpecularColour.rgb|^2 &gt;= 2^-14 (material+176,
/// 0x8220DAB0); STSP SP and the owner's static specular count (owner+0x2E0) &gt; 0; SH the owner's
/// count of lights folded into its SH (owner+0x250) &gt; 0 (`sub_82C9FF28`); FR the material's
/// vtable slot 1; CMRPH (COL in the vertex stage) the owner's byte +0xD4; SELF material+24 and ENV
/// material+108 non-null; the tree's canopy byte material+340; GRASS model+132 == 4; NRM the
/// normal texture (material+20); WIND material+338; COLL the repeated item's byte +0x1F.
///
/// _ANY IS THE UBER SHADER. A record whose flag word for an axis is 0 leaves that axis to the
/// shader at run time: the draw always publishes the axes as pixel booleans (b9 STSP, b10 SP,
/// b12 CMRPH from `sub_82CAD9A0`; b7 SH `sub_8220F4B0`; b11 FR the material's slot 10; b4 SELF,
/// b5 ENV `sub_8220D668`; b0 mist `sub_82C626E8`) and the light count as PS integer i0 =
/// count ceil(n/4), start 0, step 1 (`sub_82C9D8A8`: 0x10000 | (n + 3) / 4). The tables pick a
/// specialised shader only for the common shapes — one to four lights, no mist, no colour morph,
/// no self-illumination, no environment map, and the SP/STSP/SH combinations the bank compiled —
/// and the uber shader for everything else.
/// </remarks>
public static class Engine_ClassicTokensFable3
{
    public const string Standard = "STANDARDMATERIAL";
    public const string Tree = "TREEMATERIAL";
    public const string Repeated = "REPEATEDMATERIAL";

    /// <summary>The pixel booleans (bit numbers within the pixel half of the word) the draw publishes.</summary>
    public const int EnableMist = 0;
    public const int NeedsScattering = 3;
    public const int NeedsSelfIllumination = 4;
    public const int NeedsEnvmapping = 5;
    public const int IsTransparent = 6;
    public const int DoSHLighting = 7;
    public const int DoStaticSpecularLighting = 9;
    public const int DoSpecularLighting = 10;
    public const int DoFresnelLighting = 11;
    public const int IsColourMorphed = 12;

    /// <summary>|SpecularColour.rgb|^2 below this keeps SP0 (`flt_82074084`, 2^-14, compared at 0x8220DACC).</summary>
    public const float SpecularThreshold = 0.000061035156f;

    /// <summary>The light counts whose selection differs (0 and 5..8 share the uber shader with 5).</summary>
    public static readonly int[] DistinctLightCounts = { 1, 2, 3, 4, 5 };

    /// <summary>Fable 3 is the running title.</summary>
    public static bool Active => Refii.Core.Engine_Title.Active == Refii.Core.Engine_Title.Fable3;

    /// <summary>Whether this file selects the family's shaders under Fable 3.</summary>
    public static bool Handles(string family) => family is Standard or Tree or Repeated;

    /// <summary>The pixel half's bit for boolean <paramref name="b"/> in the composed word (xenos_common.gdshaderinc).</summary>
    public static uint PixelBoolean(int b) => 1u << (16 + b);

    /// <summary>
    /// The draw's ambient source from the mesh's <see cref="MdlInfo.GlobalLightingMode"/>
    /// (`sub_82C88C88`): 0 and 1 store 2 at owner+0x2F0 (0x82C88CC8, 0x82C88CE8); 2 stores 3 when
    /// the level's lightmap table holds the placement's key (0x82C88E24) and 0 when it does not
    /// (0x82C88E00). Other modes leave the field as constructed, which this does not trace; 0.
    /// </summary>
    public static int AmbientFor(byte globalLightingMode, bool lightmapped) => globalLightingMode switch
    {
        0 or 1 => 2,
        2 => lightmapped ? 3 : 0,
        _ => 0,
    };

    /// <summary>
    /// THE LIGHTS AXIS IS THE GROUP'S BLOCK COUNT. Every selector reads `*(*(item+0x2C)+0xC)+0x10`
    /// (+0x50 on the repeated item; 0x8220DB54..0x8220DB58, 0x8221BBF0..0x8221BBF4, 0x82CE4CB0..0x82CE4CB4): the group's light set (built by
    /// `sub_82CEEA38`, stored at group+0xC at 0x821E3E28) at +16, which `sub_821FC990` writes as
    /// the lights it packed (0x821FCAE4), or 2 when the group is one light that is not
    /// directional (0x821FCAD4..0x821FCAD8). The key word at group+0x10 (points, spots, total,
    /// 0x821E3E2C..0x821E3E7C) enters no selection index. So one sun is LGHTS1, one local light
    /// LGHTS2, two to four lights LGHTS2..4, none or five to eight the _ANY shader. The same
    /// value feeds the loop (`sub_822163C0` passes it to `sub_82C9D8A8`).
    /// </summary>
    public static int GroupCount(int lights, bool singleLocal) =>
        lights == 1 && singleLocal ? 2 : Math.Clamp(lights, 0, 8);

    /// <summary>The PS loop constant i0 the draw writes for <paramref name="lights"/> lights: blocks of four.</summary>
    public static int LightBlocks(int lights) => (Math.Max(lights, 0) + 3) / 4;

    // ---- the inputs as tokens ----------------------------------------------------------------

    public static List<string> StandardPixelTokens(
        int ambient, int lights, bool mist, bool colourMorph, bool specular, bool staticSpecular, bool sh,
        bool fresnel, bool selfIllumination, bool envmap) => new()
    {
        "LGHTS" + lights, Bit("MIST", mist), "AMB" + ambient, Bit("CMRPH", colourMorph), Bit("SP", specular),
        Bit("STSP", specular && staticSpecular), Bit("SH", sh), Bit("FR", fresnel), Bit("SELF", selfIllumination),
        Bit("ENV", envmap),
    };

    /// <summary>Godot skins before vertex() runs, so the standard vertex stage is always SKN0 FIX0.</summary>
    public static List<string> StandardVertexTokens(int ambient, bool colourMorph, bool specular) => new()
    {
        "AMB" + ambient, Bit("COL", colourMorph), "SKN0", "FIX0", Bit("SP", specular),
    };

    /// <summary>A canopy has no specular axis: v7 = (material+340 == 0) and the texture (0x8221BB98..0x8221BBAC).</summary>
    public static List<string> TreePixelTokens(int ambient, int lights, bool mist, bool specular, bool canopy) => new()
    {
        "LGHTS" + lights, Bit("MIST", mist), "AMB" + ambient, Bit("SP", specular && !canopy), Bit("TRUNK", !canopy),
    };

    public static List<string> TreeVertexTokens(int ambient, int lights, bool specular, bool canopy) => new()
    {
        "LGHTS" + lights, "AMB" + ambient, Bit("SP", specular && !canopy), Bit("TRUNK", !canopy),
    };

    public static List<string> RepeatedPixelTokens(int lights, bool mist, bool grass, bool normal) => new()
    {
        "LGHTS" + lights, Bit("MIST", mist), Bit("GRASS", grass), Bit("NRM", normal),
    };

    public static List<string> RepeatedVertexTokens(bool collision, bool grass, bool normal, bool wind) => new()
    {
        Bit("COLL", collision), Bit("GRASS", grass), Bit("NRM", normal), Bit("WIND", wind),
    };

    /// <summary>The same tokens with the light count (and, for a pixel stage, the mist) of a light group.</summary>
    public static List<string> WithLights(IEnumerable<string> tokens, int lights, bool? mist)
    {
        var list = new List<string>();
        foreach (string token in tokens)
        {
            if (token.StartsWith("LGHTS", StringComparison.Ordinal))
            {
                list.Add("LGHTS" + Math.Clamp(lights, 0, 8));
            }
            else if (mist is { } m && token.StartsWith("MIST", StringComparison.Ordinal))
            {
                list.Add(Bit("MIST", m));
            }
            else
            {
                list.Add(token);
            }
        }
        return list;
    }

    /// <summary>The light count these tokens state (LGHTS), 0 when they state none.</summary>
    public static int Lights(IEnumerable<string> tokens)
    {
        foreach (string token in tokens)
        {
            (string axis, int value) = Split(token);
            if (axis == "LGHTS")
            {
                return value;
            }
        }
        return 0;
    }

    /// <summary>
    /// The pixel booleans the draw publishes for these pixel tokens (the axes an uber shader
    /// reads at run time), plus b3 g_NeedsScattering, which `sub_82221B48` sets unless the draw
    /// state's byte +430 says otherwise or a later pass is out of the atmosphere's reach.
    /// </summary>
    public static uint Booleans(IEnumerable<string> pixelTokens)
    {
        uint word = PixelBoolean(NeedsScattering);
        foreach (string token in pixelTokens)
        {
            (string axis, int value) = Split(token);
            if (value <= 0)
            {
                continue;
            }
            word |= axis switch
            {
                "MIST" => PixelBoolean(EnableMist),
                "SP" => PixelBoolean(DoSpecularLighting),
                "STSP" => PixelBoolean(DoStaticSpecularLighting),
                "SH" => PixelBoolean(DoSHLighting),
                "FR" => PixelBoolean(DoFresnelLighting),
                "CMRPH" => PixelBoolean(IsColourMorphed),
                "SELF" => PixelBoolean(NeedsSelfIllumination),
                "ENV" => PixelBoolean(NeedsEnvmapping),
                _ => 0u,
            };
        }
        return word;
    }

    // ---- tokens to the entry ------------------------------------------------------------------

    /// <summary>
    /// The entry the guest's table selects for <paramref name="tokens"/> (an absent axis is 0), or
    /// empty where its record is 0 (no shader: AMB1 on the standard family, AMB3 on trees).
    /// </summary>
    public static string Entry(string stage, string family, IEnumerable<string> tokens)
    {
        var axes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string token in tokens)
        {
            (string axis, int value) = Split(token);
            axes[axis] = value;
        }
        int V(string axis) => axes.TryGetValue(axis, out int value) ? value : 0;
        bool pixel = stage == "PSHADER";
        return family switch
        {
            Standard when pixel => StandardPixel(V("AMB"), V("LGHTS"), V("MIST") != 0, V("CMRPH") != 0, V("SP") != 0,
                V("STSP") != 0, V("SH") != 0, V("FR") != 0, V("SELF") != 0, V("ENV") != 0),
            Standard => StandardVertex(V("AMB"), V("COL") != 0, V("SKN"), V("FIX") != 0, V("SP") != 0),
            Tree when pixel => TreePixel(V("AMB"), V("LGHTS"), V("MIST") != 0, V("SP") != 0, V("TRUNK") == 0),
            Tree => TreeVertex(V("AMB"), V("LGHTS"), V("SP") != 0, V("TRUNK") == 0),
            Repeated when pixel => RepeatedPixel(V("LGHTS"), V("MIST") != 0, V("GRASS") != 0, V("NRM") != 0),
            Repeated => RepeatedVertex(V("COLL") != 0, V("GRASS") != 0, V("NRM") != 0, V("WIND") != 0),
            _ => string.Empty,
        };
    }

    /// <summary>
    /// Records 1..51 of dword_8329BBD0: AMB1 has none; AMB0 has only the uber shader; one to four
    /// lights with no mist, colour morph, self-illumination or environment map take the
    /// specialised shader where the bank compiled the SP/STSP/SH shape (AMB2: 000, 100, 111; AMB3:
    /// 000, 110), else the shader fixing only the light count and the ambient.
    /// </summary>
    public static string StandardPixel(
        int ambient, int lights, bool mist, bool colourMorph, bool specular, bool staticSpecular, bool sh,
        bool fresnel, bool selfIllumination, bool envmap)
    {
        if (ambient is not (0 or 2 or 3))
        {
            return string.Empty;
        }
        string Uber(string count) =>
            $"PSHADER_STANDARDMATERIAL_{count}_MIST_ANY_AMB{ambient}_CMRPH_ANY_SP_ANY_STSP_ANY_SH_ANY_FR_ANY_SELF_ANY_ENV_ANY";
        if (ambient == 0 || lights is < 1 or > 4)
        {
            return Uber("LGHTS_ANY");
        }
        bool st = specular && staticSpecular;
        bool compiled = ambient == 2
            ? (!specular && !st && !sh) || (specular && !st && !sh) || (specular && st && sh)
            : (!specular && !st && !sh) || (specular && st && !sh);
        if (mist || colourMorph || selfIllumination || envmap || !compiled)
        {
            return Uber("LGHTS" + lights);
        }
        return $"PSHADER_STANDARDMATERIAL_LGHTS{lights}_MIST0_AMB{ambient}_CMRPH0_SP{B(specular)}_STSP{B(st)}" +
               $"_SH{B(sh)}_FR{B(fresnel)}_SELF0_ENV0";
    }

    /// <summary>
    /// Records 1..56 of dword_83292678: AMB 0, 2 and 3; FIX only with SKN 1 or 2; AMB3 never with
    /// SKN 1 or 2. Every other shape names its entry outright.
    /// </summary>
    public static string StandardVertex(int ambient, bool colourMorph, int skin, bool fix, bool specular)
    {
        bool exists = ambient is 0 or 2 or 3 && skin is >= 0 and <= 3 && (!fix || skin is 1 or 2) &&
                      !(ambient == 3 && skin is 1 or 2);
        return exists
            ? $"VSHADER_STANDARDMATERIAL_AMB{ambient}_COL{B(colourMorph)}_SKN{skin}_FIX{B(fix)}_SP{B(specular)}"
            : string.Empty;
    }

    /// <summary>
    /// The trunk lights per pixel (LGHTS1 specialised, else the uber shader); the canopy per
    /// vertex, its pixel stage always LGHTS_ANY with a real mist axis. No tree has an AMB3 pair.
    /// </summary>
    public static string TreePixel(int ambient, int lights, bool mist, bool specular, bool canopy)
    {
        if (ambient is not (0 or 1 or 2))
        {
            return string.Empty;
        }
        if (canopy)
        {
            return $"PSHADER_TREEMATERIAL_LGHTS_ANY_MIST{B(mist)}_AMB{ambient}_SP0_TRUNK0";
        }
        return lights == 1
            ? $"PSHADER_TREEMATERIAL_LGHTS1_MIST{B(mist)}_AMB{ambient}_SP{B(specular)}_TRUNK1"
            : $"PSHADER_TREEMATERIAL_LGHTS_ANY_MIST_ANY_AMB{ambient}_SP_ANY_TRUNK1";
    }

    public static string TreeVertex(int ambient, int lights, bool specular, bool canopy)
    {
        if (ambient is not (0 or 1 or 2))
        {
            return string.Empty;
        }
        if (!canopy)
        {
            return $"VSHADER_TREEMATERIAL_LGHTS_ANY_AMB{ambient}_SP{B(specular)}_TRUNK1";
        }
        return lights == 1
            ? $"VSHADER_TREEMATERIAL_LGHTS1_AMB{ambient}_SP0_TRUNK0"
            : $"VSHADER_TREEMATERIAL_LGHTS_ANY_AMB{ambient}_SP_ANY_TRUNK0";
    }

    public static string RepeatedPixel(int lights, bool mist, bool grass, bool normal) =>
        $"PSHADER_REPEATEDMATERIAL_{(lights == 1 ? "LGHTS1" : "LGHTS_ANY")}_MIST{B(mist)}_GRASS{B(grass)}_NRM{B(normal)}";

    public static string RepeatedVertex(bool collision, bool grass, bool normal, bool wind) =>
        $"VSHADER_REPEATEDMATERIAL_COLL{B(collision)}_GRASS{B(grass)}_NRM{B(normal)}_WIND{B(wind)}";

    // ---- the material's own inputs ------------------------------------------------------------

    /// <summary>
    /// The family a Fable 3 material draws with, by the parts that name it: a tree part's on
    /// TREEMATERIAL (`sub_8221BAF0`), an instanced part's on REPEATEDMATERIAL (`sub_82CE4C78`),
    /// rigid and skinned parts' on STANDARDMATERIAL.
    /// </summary>
    public static string FamilyFor(MdlMaterial material, MdlInfo? info)
    {
        if (info is null)
        {
            return Standard;
        }
        bool tree = false, instanced = false;
        // Each LOD payload has its own material table, which its own parts index.
        void Look(List<MdlMesh> meshes, IEnumerable<MdlMeshBuffer> buffers)
        {
            int index = meshes.FindIndex(mesh => mesh.Materials.Contains(material));
            if (index < 0)
            {
                return;
            }
            foreach (MdlMeshBuffer buffer in buffers)
            {
                if (buffer.MeshIndex == (uint)index)
                {
                    tree |= buffer.IsEngineTree;
                    instanced |= buffer.IsEngineInstanced;
                }
            }
        }
        Look(info.Meshes, info.MeshBuffers);
        foreach (MdlLod lod in info.Lods)
        {
            Look(lod.Meshes, lod.MeshBuffers);
        }
        return tree ? Tree : instanced ? Repeated : Standard;
    }

    /// <summary>
    /// SP's material half: SpecularColour (material+176, zero from the base constructor
    /// `sub_82CEE200`) has |rgb|^2 &gt;= 2^-14. The texture half always holds: an empty specular
    /// name takes the white default (`sub_82CBA610` through `sub_82182B70`).
    /// </summary>
    public static bool SpecularStated(MdlMaterial material)
    {
        if (material.StatedProperties is null ||
            !material.StatedProperties.TryGetValue("SpecularColour", out float[]? colour) || colour.Length < 3)
        {
            return false;
        }
        return colour[0] * colour[0] + colour[1] * colour[1] + colour[2] * colour[2] >= SpecularThreshold;
    }

    private static string Bit(string axis, bool on) => on ? axis + "1" : axis + "0";

    private static int B(bool on) => on ? 1 : 0;

    private static (string Axis, int Value) Split(string token)
    {
        int at = token.Length;
        while (at > 0 && char.IsDigit(token[at - 1]))
        {
            --at;
        }
        return at == token.Length || at == 0
            ? (token, 0)
            : (token.Substring(0, at), int.Parse(token.AsSpan(at), System.Globalization.CultureInfo.InvariantCulture));
    }
}
