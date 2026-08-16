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
    /// RETURNS TreeTrunk, NEVER TreeCanopy. The census settled that the canopy/trunk split is real
    /// and structural — a tagged tree carries 2 or 3 materials on average, each on its own named
    /// sub-mesh, one binding bark and the others binding leaves — but it did NOT turn up a numeric
    /// field that separates them. The names do it plainly and §3.1 forbids classifying by name, so
    /// a tree resolves to the trunk family: the half that IS ordinary per-pixel lighting, and
    /// therefore the half the standard-material fallback already approximates. The canopy shader is
    /// written and compiling, waiting on a discriminator that is allowed to be used.
    /// </remarks>
    public static ShaderFamily Resolve(MdlInfo? info) => info?.MeshKind switch
    {
        1 => ShaderFamily.TreeTrunk,
        4 => ShaderFamily.Grass,
        _ => ShaderFamily.Standard,
    };
}
