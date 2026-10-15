using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Godot;

namespace Refii.Models;

/// <summary>
/// A rigid model's cast mesh for the classic cascades: its plain opaque surfaces joined into one
/// surface per cull mode, drawn with a depth-only material; every other surface kept as it is.
/// </summary>
/// <remarks>
/// A cascade keeps only depth. A STANDARDMATERIAL surface with no alpha test and no composition
/// of its own writes nothing there but its triangles' depth, placed by the same transform Godot's
/// default gives (Engine_ClassicCasters), so several of them are one draw instead of several. A
/// surface whose depth depends on its shader stays itself: an alpha test (xr_spec_constants bit
/// 2), a composition's own defines (a quadrant gate, a vertex kill), any other family (wind,
/// stipple, fur). Grouped by the material's own cull mode, so each triangle culls as it did; a
/// two-sided caster (CastsTwoSided) culls nothing. LODs as the modern merge carries them
/// (Engine_ShadowProxy.MergedLods). Null when joining saves nothing. Cached per mesh.
/// One-sided casts may be built on the streamer's workers (they only read the source); the
/// two-sided one takes two-sided copies of the kept materials and is built on the main thread.
/// </remarks>
public static class Engine_ClassicCasterMerge
{
    private static readonly ConditionalWeakTable<ArrayMesh, Dictionary<(bool TwoSided, System.UInt128 Hidden), ArrayMesh?>> Cache = new();

    private static readonly System.Lazy<ShaderMaterial>[] Depth =
    {
        new(() => new ShaderMaterial { Shader = GD.Load<Shader>("res://src/Engine/material/classic_caster_depth_back.gdshader") }),
        new(() => new ShaderMaterial { Shader = GD.Load<Shader>("res://src/Engine/material/classic_caster_depth_front.gdshader") }),
        new(() => new ShaderMaterial { Shader = GD.Load<Shader>("res://src/Engine/material/classic_caster_depth_both.gdshader") }),
    };

    /// <summary>Surfaces joined so far, and merged casts built, for the log.</summary>
    public static int SurfacesJoined, MeshesBuilt;

    /// <summary>
    /// The cast mesh for <paramref name="source"/> without the surfaces set in
    /// <paramref name="hidden"/> (a body's collapsed clusters), or null to cast the source as it
    /// is. A skinned source keeps its bones and weights: the caster rides the body's skeleton.
    /// </summary>
    public static ArrayMesh? For(ArrayMesh source, bool twoSided, System.UInt128 hidden = default)
    {
        Dictionary<(bool, System.UInt128), ArrayMesh?> built = Cache.GetOrCreateValue(source);
        lock (built)
        {
            if (!built.TryGetValue((twoSided, hidden), out ArrayMesh? mesh))
            {
                mesh = Build(source, twoSided, hidden);
                built[(twoSided, hidden)] = mesh;
            }
            return mesh;
        }
    }

    /// <summary>0 back, 1 front, 2 none: the cull a composed render mode states.</summary>
    private static int CullOf(string renderMode) =>
        renderMode.Contains("cull_disabled", System.StringComparison.Ordinal) ? 2 :
        renderMode.Contains("cull_front", System.StringComparison.Ordinal) ? 1 : 0;

    /// <summary>The depth-only material a joined surface of cull mode <paramref name="cull"/> draws with.</summary>
    internal static ShaderMaterial DepthMaterial(int cull) => Depth[cull].Value;

    /// <summary>One cull mode's joined triangles of a model, in the model's space (Engine_ClassicCasterCells).</summary>
    internal sealed class Group
    {
        public required Vector3[] Positions;
        public required int[] Indices;
        /// <summary>Each joined surface's first vertex, full index list and LODs, for Engine_ShadowProxy.MergedLods.</summary>
        public required List<(int At, int[] Full, List<(float Edge, int[] Indices)> Lods)> Levels;
    }

    /// <summary>A model's share of a caster cell: its joinable triangles, and whether surfaces stay out.</summary>
    internal sealed class CellShape
    {
        public required Group?[] Groups;
        /// <summary>Surfaces <see cref="Build"/> keeps with their own material (an alpha test, a family's own depth).</summary>
        public required bool HasKept;
        /// <summary>Those surfaces alone, built on first use (<see cref="KeptOnly"/>).</summary>
        public ArrayMesh? KeptMesh;
    }

    private static readonly ConditionalWeakTable<ArrayMesh, Dictionary<bool, CellShape?>> CellCache = new();

    /// <summary>
    /// <paramref name="source"/>'s joinable triangles by cull mode (null where it has none), or null
    /// when none join or the model is skinned. Cached per mesh.
    /// </summary>
    internal static CellShape? CellGeometry(ArrayMesh source, bool twoSided)
    {
        Dictionary<bool, CellShape?> built = CellCache.GetOrCreateValue(source);
        lock (built)
        {
            if (!built.TryGetValue(twoSided, out CellShape? shape))
            {
                shape = BuildCellGeometry(source, twoSided);
                built[twoSided] = shape;
            }
            return shape;
        }
    }

    /// <summary>
    /// The surfaces of <paramref name="source"/> a cell leaves out, as their own cast mesh: what a
    /// model in a cell still draws itself. Same surfaces and materials as <see cref="Build"/> keeps.
    /// Main thread (two-sided copies).
    /// </summary>
    internal static ArrayMesh KeptOnly(ArrayMesh source, bool twoSided, CellShape shape)
    {
        if (shape.KeptMesh is { } held)
        {
            return held;
        }
        var result = new ArrayMesh();
        for (int s = 0; s < source.GetSurfaceCount(); s++)
        {
            Material? material = source.SurfaceGetMaterial(s);
            Mesh.PrimitiveType primitive = source.SurfaceGetPrimitiveType(s);
            if (primitive == Mesh.PrimitiveType.Triangles && Joinable(material))
            {
                continue;
            }
            Engine_MdlSurfaceArrays.Add(result, primitive, Engine_MdlSurfaceArrays.Of(source, s), Engine_MdlMesh.SurfaceLods(source, s));
            Material? cast = material is not null && twoSided ? Engine_ClassicMaterial.TwoSidedCopy(material) : material;
            result.SurfaceSetMaterial(result.GetSurfaceCount() - 1, cast);
        }
        result.CustomAabb = source.GetAabb();
        shape.KeptMesh = result;
        return result;
    }

    private static CellShape? BuildCellGeometry(ArrayMesh source, bool twoSided)
    {
        int surfaces = source.GetSurfaceCount();
        if (surfaces < 1 || source.GetBlendShapeCount() != 0)
        {
            return null;
        }
        var positions = new List<Vector3>[3];
        var indices = new List<int>[3];
        var levels = new List<(int, int[], List<(float, int[])>)>[3];
        bool kept = false;
        for (int s = 0; s < surfaces; s++)
        {
            Material? material = source.SurfaceGetMaterial(s);
            if ((source.SurfaceGetFormat(s) & Mesh.ArrayFormat.FormatBones) != 0)
            {
                return null;
            }
            if (source.SurfaceGetPrimitiveType(s) != Mesh.PrimitiveType.Triangles || !Joinable(material))
            {
                kept = true;
                continue;
            }
            Godot.Collections.Array arrays = Engine_MdlSurfaceArrays.Of(source, s);
            if (arrays[(int)Mesh.ArrayType.Vertex].VariantType != Variant.Type.PackedVector3Array ||
                arrays[(int)Mesh.ArrayType.Index].VariantType != Variant.Type.PackedInt32Array)
            {
                return null;
            }
            Vector3[] vertex = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            int[] index = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
            if (vertex.Length == 0 || index.Length == 0)
            {
                continue;
            }
            int cull = twoSided ? 2 : CullOf(Engine_ClassicMaterial.RenderModeOf((ShaderMaterial)material!));
            positions[cull] ??= new List<Vector3>();
            indices[cull] ??= new List<int>();
            levels[cull] ??= new List<(int, int[], List<(float, int[])>)>();
            int at = positions[cull].Count;
            positions[cull].AddRange(vertex);
            foreach (int i in index)
            {
                indices[cull].Add(at + i);
            }
            var sorted = new List<(float Edge, int[] Indices)>();
            if (Engine_MdlMesh.SurfaceLods(source, s) is { } lods)
            {
                foreach (Variant key in lods.Keys)
                {
                    sorted.Add((key.AsSingle(), lods[key].AsInt32Array()));
                }
                sorted.Sort((a, b) => a.Edge.CompareTo(b.Edge));
            }
            levels[cull].Add((at, index, sorted));
        }
        var groups = new Group?[3];
        bool any = false;
        for (int cull = 0; cull < 3; cull++)
        {
            if (positions[cull] is null)
            {
                continue;
            }
            groups[cull] = new Group
            {
                Positions = positions[cull].ToArray(),
                Indices = indices[cull].ToArray(),
                Levels = levels[cull],
            };
            any = true;
        }
        return any ? new CellShape { Groups = groups, HasKept = kept } : null;
    }

    internal static bool Joinable(Material? material)
    {
        return material is ShaderMaterial classic &&
            classic.HasMeta(Engine_ClassicMaterial.ClassicMeta) &&
            classic.GetMeta(Engine_ClassicMaterial.FamilyMetaName, string.Empty).AsString() == "STANDARDMATERIAL" &&
            classic.GetMeta(Engine_ClassicMaterial.DefinesMeta, string.Empty).AsString().Length == 0 &&
            (classic.GetShaderParameter("xr_spec_constants").AsUInt32() & 2u) == 0u &&
            !Engine_ClassicMaterial.RenderModeOf(classic).Contains("blend_", System.StringComparison.Ordinal);
    }

    private static ArrayMesh? Build(ArrayMesh source, bool twoSided, System.UInt128 hidden)
    {
        int surfaces = source.GetSurfaceCount();
        // Blend shapes would not survive the join.
        if (surfaces < 2 || surfaces > 128 || source.GetBlendShapeCount() != 0)
        {
            return null;
        }
        // Skinned: every surface carries the same weight layout, or nothing is joined.
        Mesh.ArrayFormat first = source.SurfaceGetFormat(0);
        bool skinned = (first & Mesh.ArrayFormat.FormatBones) != 0;
        Mesh.ArrayFormat weightLayout = first & Mesh.ArrayFormat.FlagUse8BoneWeights;
        for (int s = 1; s < surfaces; s++)
        {
            Mesh.ArrayFormat format = source.SurfaceGetFormat(s);
            if (((format & Mesh.ArrayFormat.FormatBones) != 0) != skinned ||
                (format & Mesh.ArrayFormat.FlagUse8BoneWeights) != weightLayout)
            {
                return null;
            }
        }

        var groups = new (List<Vector3> Positions, List<int> Indices, List<int> Bones, List<float> Weights,
            List<(int At, int[] Full, List<(float Edge, int[] Indices)> Lods)> Levels, int Count)[3];
        var kept = new List<(Godot.Collections.Array Arrays, Material? Material, Godot.Collections.Dictionary? Lods, Mesh.PrimitiveType Primitive)>();
        for (int s = 0; s < surfaces; s++)
        {
            if ((hidden & (System.UInt128.One << s)) != System.UInt128.Zero)
            {
                continue;
            }
            Material? material = source.SurfaceGetMaterial(s);
            Godot.Collections.Array arrays = Engine_MdlSurfaceArrays.Of(source, s);
            Godot.Collections.Dictionary? lods = Engine_MdlMesh.SurfaceLods(source, s);
            Mesh.PrimitiveType primitive = source.SurfaceGetPrimitiveType(s);
            Vector3[] vertex = arrays[(int)Mesh.ArrayType.Vertex].VariantType == Variant.Type.PackedVector3Array
                ? arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array()
                : System.Array.Empty<Vector3>();
            int[] index = arrays[(int)Mesh.ArrayType.Index].VariantType == Variant.Type.PackedInt32Array
                ? arrays[(int)Mesh.ArrayType.Index].AsInt32Array()
                : System.Array.Empty<int>();
            int[] bone = skinned && arrays[(int)Mesh.ArrayType.Bones].VariantType == Variant.Type.PackedInt32Array
                ? arrays[(int)Mesh.ArrayType.Bones].AsInt32Array()
                : System.Array.Empty<int>();
            float[] weight = skinned && arrays[(int)Mesh.ArrayType.Weights].VariantType == Variant.Type.PackedFloat32Array
                ? arrays[(int)Mesh.ArrayType.Weights].AsFloat32Array()
                : System.Array.Empty<float>();
            int perVertex = weightLayout != 0 ? 8 : 4;
            if (primitive != Mesh.PrimitiveType.Triangles || !Joinable(material) || vertex.Length == 0 || index.Length == 0 ||
                (skinned && (bone.Length != vertex.Length * perVertex || weight.Length != vertex.Length * perVertex)))
            {
                kept.Add((arrays, material, lods, primitive));
                continue;
            }
            int cull = twoSided ? 2 : CullOf(Engine_ClassicMaterial.RenderModeOf((ShaderMaterial)material!));
            groups[cull].Positions ??= new List<Vector3>();
            groups[cull].Indices ??= new List<int>();
            groups[cull].Bones ??= new List<int>();
            groups[cull].Weights ??= new List<float>();
            groups[cull].Bones.AddRange(bone);
            groups[cull].Weights.AddRange(weight);
            groups[cull].Levels ??= new List<(int, int[], List<(float, int[])>)>();
            int at = groups[cull].Positions.Count;
            groups[cull].Positions.AddRange(vertex);
            foreach (int i in index)
            {
                groups[cull].Indices.Add(at + i);
            }
            var sorted = new List<(float Edge, int[] Indices)>();
            if (lods is not null)
            {
                foreach (Variant key in lods.Keys)
                {
                    sorted.Add((key.AsSingle(), lods[key].AsInt32Array()));
                }
                sorted.Sort((a, b) => a.Edge.CompareTo(b.Edge));
            }
            groups[cull].Levels.Add((at, index, sorted));
            groups[cull].Count++;
        }

        int joined = 0;
        foreach (var group in groups)
        {
            joined += group.Count;
        }
        int nonEmpty = 0;
        foreach (var group in groups)
        {
            nonEmpty += group.Count > 0 ? 1 : 0;
        }
        // Nothing joined and nothing dropped: the source casts as well as this would.
        if (joined - nonEmpty < 1 && hidden == System.UInt128.Zero)
        {
            return null;
        }

        var result = new ArrayMesh();
        for (int cull = 0; cull < 3; cull++)
        {
            if (groups[cull].Count == 0)
            {
                continue;
            }
            var one = new Godot.Collections.Array();
            one.Resize((int)Mesh.ArrayType.Max);
            one[(int)Mesh.ArrayType.Vertex] = groups[cull].Positions.ToArray();
            one[(int)Mesh.ArrayType.Index] = groups[cull].Indices.ToArray();
            if (skinned)
            {
                one[(int)Mesh.ArrayType.Bones] = groups[cull].Bones.ToArray();
                one[(int)Mesh.ArrayType.Weights] = groups[cull].Weights.ToArray();
            }
            result.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, one, null,
                Refii.Entities.Engine_ShadowProxy.MergedLods(groups[cull].Levels), weightLayout);
            result.SurfaceSetMaterial(result.GetSurfaceCount() - 1, Depth[cull].Value);
        }
        foreach ((Godot.Collections.Array arrays, Material? material, Godot.Collections.Dictionary? lods, Mesh.PrimitiveType primitive) in kept)
        {
            Engine_MdlSurfaceArrays.Add(result, primitive, arrays, lods);
            Material? cast = material is not null && twoSided ? Engine_ClassicMaterial.TwoSidedCopy(material) : material;
            result.SurfaceSetMaterial(result.GetSurfaceCount() - 1, cast);
        }
        // The source's box: the cut and the cascades' culling see the same extent.
        result.CustomAabb = source.GetAabb();
        System.Threading.Interlocked.Add(ref SurfacesJoined, joined);
        System.Threading.Interlocked.Increment(ref MeshesBuilt);
        return result;
    }
}
