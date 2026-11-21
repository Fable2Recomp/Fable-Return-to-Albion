namespace Refii.Models;

/// <summary>
/// Which of the guest's shader families draws a surface.
/// </summary>
/// <remarks>
/// THE FAMILY IS STRUCTURAL, AND IT IS NOT IN THE MATERIAL RECORD. The guest picks it from the
/// DRAW PATH — a vtable slot on the thing being drawn — which is why `.mdl` ShaderType is 0 on
/// every shipped record and reading a family out of a material would find nothing. The caller
/// knows what it is drawing; the material does not.
///
/// The families differ by LIGHTING MODEL, not by which textures they bind, and that is the whole
/// reason they cannot collapse into one shader with uniform branches:
///
///   - <see cref="Standard"/> and <see cref="Scatter"/> light per pixel with N·L and a Phong lobe.
///   - <see cref="Grass"/> has NO specular path and NO ambient-normal darkening at all; its
///     ambient is a vertex colour with the global ambient already folded in by the vertex stage.
///   - <see cref="TreeCanopy"/> has no per-pixel normal anywhere — three interpolated vertex
///     terms and a screen shadow — while <see cref="TreeTrunk"/> is ordinary per-pixel lighting.
///     They share only an output tail.
///
/// The permutation flags in the shipped blob names are NOT families and mostly do not survive the
/// port: `blt{1,2,4,7}` is a LIGHT COUNT (verified — `g_GlobalLightParamsPS` reads fit 6+3n
/// exactly across blt1/2/4, and all four variants read a character-identical constant set), and
/// Godot's renderer already calls `light()` once per light. `skn`, `wind1` and `coll1` are vertex
/// permutations Godot's skeleton and our own wind own. What is left is a handful of real branches
/// per family.
/// </remarks>
public enum ShaderFamily
{
    /// <summary>`standardmaterial` — props, buildings, everything static that is not vegetation.</summary>
    Standard,

    /// <summary>`treematerial` with no `trunk1` — leaf cards. Two blobs out of forty-four.</summary>
    TreeCanopy,

    /// <summary>`treematerial_trunk1` — the trunk, which IS lit per pixel.</summary>
    TreeTrunk,

    /// <summary>`repeatedmaterial` — instanced scatter.</summary>
    Scatter,

    /// <summary>`repeatedmaterial_grass1` — a genuinely reduced lighting model, not a variant.</summary>
    Grass,
}

public static class Engine_ShaderFamily
{
    public const string StandardPath =
        "res://src/Engine/material/standard_material.gdshader";

    public const string StandardCulledPath =
        "res://src/Engine/material/standard_material_culled.gdshader";

    /// <summary>Cutout/alpha-prepass shader with back faces culled, for meshes that explicitly model both sides.</summary>
    public const string StandardCutoutCulledPath =
        "res://src/Engine/material/standard_material_cutout_culled.gdshader";

    /// <summary>The blended shader for a translucent diffuse (texture header flag 0x80).</summary>
    public const string StandardTranslucentPath =
        "res://src/Engine/material/standard_material_translucent.gdshader";

    /// <summary>The blended translucent shader with back faces culled, for meshes that explicitly model both sides.</summary>
    public const string StandardTranslucentCulledPath =
        "res://src/Engine/material/standard_material_translucent_culled.gdshader";

    /// <summary>The two-sided shader with the title's tree wind, for every surface of a tree (.mdl byte 77 == 2).</summary>
    public const string StandardTreePath =
        "res://src/Engine/material/standard_material_tree.gdshader";

    /// <summary>The two-sided shader with the wind bend: the only one that reads TIME, for surfaces that sway.</summary>
    public const string StandardWindPath =
        "res://src/Engine/material/standard_material_wind.gdshader";

    /// <summary>
    /// `cull_back_faces`, ON by default: SOLID surfaces are drawn with back faces culled. Off draws
    /// every surface from both sides, as this did until 2026-09-22. Read once: a render mode
    /// belongs to the shader file a material was built with.
    ///
    /// NARROWER THAN THE GUEST, deliberately. The guest culls everything but the four leaf
    /// materials that carry a translucency texture (`MaterialSlotCensus`: 4 of 9,293 fill the +24
    /// slot `sub_82210DD0` tests), so on the 360 a fern seen from beneath is not there. Here an
    /// alpha-tested card or anything on a vegetation path keeps both sides — the look this had —
    /// and only opaque, still geometry is culled, which is where the pixels are.
    /// </summary>
    public static bool CullBackFaces
    {
        get
        {
            // REFII_CULL_BACK_FACES=0|1 wins over the file, for the tools' side-by-side renders.
            string forced = System.Environment.GetEnvironmentVariable("REFII_CULL_BACK_FACES") ?? string.Empty;
            if (_cullBackFaces is null && forced.Length != 0)
            {
                _cullBackFaces = forced != "0";
            }

            if (_cullBackFaces is null)
            {
                try
                {
                    _cullBackFaces = Refii.Core.Engine_RefiiConfig.Bool(
                        Refii.Core.Engine_RefiiConfig.Read(out _), "cull_back_faces", true);
                }
                catch (System.Exception)
                {
                    _cullBackFaces = true;
                }
            }

            return _cullBackFaces.Value;
        }
    }

    private static bool? _cullBackFaces;

    public const string TreeCanopyPath =
        "res://src/Engine/material/families/tree_canopy.gdshader";

    /// <summary>The shader file a family is drawn with.</summary>
    /// <remarks>
    /// Families with no file of their own yet fall back to <see cref="StandardPath"/> DELIBERATELY
    /// and visibly, rather than silently: drawing a trunk with the standard material is the
    /// long-standing behaviour and merely imperfect, whereas returning null would drop the surface
    /// entirely and read as missing geometry.
    /// </remarks>
    public static string PathFor(ShaderFamily family) => family switch
    {
        ShaderFamily.TreeCanopy => TreeCanopyPath,
        _ => StandardPath,
    };

    /// <summary>
    /// The family a mesh's surfaces are drawn with, from the mesh header.
    /// </summary>
    /// <remarks>
    /// `MeshKind` IS THE FAMILY BYTE, AND `MovementMode` IS NOT — they sit beside each other in the
    /// same 28-byte header block and it is easy to reach for the wrong one. MovementMode describes
    /// how a mesh moves in WIND (0 still, 1 a low plant, 2 a tree); MeshKind is the one the guest
    /// itself branches on to pick a draw path, and `sub_8222B600` tests it directly:
    ///
    ///     8222b6a0  lwz  r11, 0x64(r8)     ; sub-record +0x64 = KIND enum
    ///     8222b6a4  addi r5, r11, -4       ; kind == 4  ->  GRASS
    ///
    /// A census of all 5,446 shipped headers (<c>src/tools/MeshKindCensus.cs</c>) says it takes
    /// exactly three values, and they line up with the families rather than with the wind:
    ///
    ///     kind 0 : 5,299     kind 1 : 109     kind 4 : 38
    ///
    /// 107 meshes carry the data-stated `IsTree`/`NewTree` tag, and ALL 107 are kind 1 — so kind 1
    /// is the tree family, agreed on by two independent fields with no exceptions either way. Kind
    /// 4 is the guest's own grass constant.
    ///
    /// KIND 4 DOES NOT MEAN THIS PATH DRAWS GRASS. `repeatedmaterial_grass1` is the INSTANCED
    /// SCATTER path, which reaches Godot as a MultiMesh; a grass mesh arriving through the ordinary
    /// static path still wants the grass LIGHTING MODEL, which is what the family selects, but the
    /// instancing is a separate question this enum does not answer.
    ///
    /// RETURNS TreeTrunk, NEVER TreeCanopy, which matches the guest.
    ///
    /// The guest's own split is ONE BIT, and it is numeric and data-stated. `sub_82AA04A0` builds
    /// a texture-set record's flag word from the mere PRESENCE of each of its file strings, and
    /// bit 5 (0x20, at 0x82AA0584) means "the SEVENTH string names a texture". `sub_82218FC8`
    /// reads that word off material[0] (`*(obj+0x14)`, never indexed) at 0x82219070 and picks the
    /// shader descriptor from bit 5 alone: clear takes {40, 41, 29} and `sub_82B1E888` defines
    /// `SpecNormalSecDifParams_Trunk` at 0x82B1EA34, set takes {42, 43, 29} with no Trunk define.
    /// Those are Shaders.sbk table indices, so the bit terminates in a binding-id pair and no name
    /// is ever matched. The same bit drives the LOD fade that the shipped `EnableTreeCanopyFading`
    /// cvar names (`sub_8220AF90`, 0x8220B264), which corroborates the polarity independently.
    ///
    /// No shipped tree sets the bit (`src/tools/MeshKindCensus.cs`), so the guest draws every
    /// shipped tree with `treematerial_trunk1`; the canopy permutation is never reached. The
    /// canopy shader stays written for modded data that sets the bit; if it is ever wired up, the
    /// test is `material[0]`'s seventh file string being non-empty, which our parser holds as
    /// `ShaderParams[1]`.
    /// </remarks>
    public static ShaderFamily Resolve(MdlInfo? info) => info?.MeshKind switch
    {
        1 => ShaderFamily.TreeTrunk,
        4 => ShaderFamily.Grass,
        _ => ShaderFamily.Standard,
    };
}
