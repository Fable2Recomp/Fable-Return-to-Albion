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
    private static readonly ConditionalWeakTable<ArrayMesh, Built> Cache = new();

    private sealed class Built
    {
        public bool OneDone, BothDone;
        public ArrayMesh? One, Both;
    }

    private static readonly System.Lazy<ShaderMaterial>[] Depth =
    {
        new(() => new ShaderMaterial { Shader = GD.Load<Shader>("res://src/Engine/material/classic_caster_depth_back.gdshader") }),
        new(() => new ShaderMaterial { Shader = GD.Load<Shader>("res://src/Engine/material/classic_caster_depth_front.gdshader") }),
        new(() => new ShaderMaterial { Shader = GD.Load<Shader>("res://src/Engine/material/classic_caster_depth_both.gdshader") }),
    };

    /// <summary>Surfaces joined so far, and merged casts built, for the log.</summary>
    public static int SurfacesJoined, MeshesBuilt;

    /// <summary>The cast mesh for <paramref name="source"/>, or null to cast the source as it is.</summary>
    public static ArrayMesh? For(ArrayMesh source, bool twoSided)
    {
        Built built = Cache.GetOrCreateValue(source);
        lock (built)
        {
            if (twoSided ? built.BothDone : built.OneDone)
            {
                return twoSided ? built.Both : built.One;
            }
            ArrayMesh? mesh = Build(source, twoSided);
            if (twoSided)
            {
                built.Both = mesh;
                built.BothDone = true;
            }
            else
            {
                built.One = mesh;
                built.OneDone = true;
            }
            return mesh;
        }
    }

    /// <summary>0 back, 1 front, 2 none: the cull a composed render mode states.</summary>
    private static int CullOf(string renderMode) =>
        renderMode.Contains("cull_disabled", System.StringComparison.Ordinal) ? 2 :
        renderMode.Contains("cull_front", System.StringComparison.Ordinal) ? 1 : 0;

    private static bool Joinable(Material? material)
    {
        return material is ShaderMaterial classic &&
            classic.HasMeta(Engine_ClassicMaterial.ClassicMeta) &&
            classic.GetMeta(Engine_ClassicMaterial.FamilyMetaName, string.Empty).AsString() == "STANDARDMATERIAL" &&
            classic.GetMeta(Engine_ClassicMaterial.DefinesMeta, string.Empty).AsString().Length == 0 &&
            (classic.GetShaderParameter("xr_spec_constants").AsUInt32() & 2u) == 0u &&
            !Engine_ClassicMaterial.RenderModeOf(classic).Contains("blend_", System.StringComparison.Ordinal);
    }

    private static ArrayMesh? Build(ArrayMesh source, bool twoSided)
    {
        int surfaces = source.GetSurfaceCount();
        if (surfaces < 2 || surfaces > 64 || (source.SurfaceGetFormat(0) & Mesh.ArrayFormat.FormatBones) != 0)
        {
            return null;
        }

        var groups = new (List<Vector3> Positions, List<int> Indices, List<(int At, int[] Full, List<(float Edge, int[] Indices)> Lods)> Levels, int Count)[3];
        var kept = new List<(Godot.Collections.Array Arrays, Material? Material, Godot.Collections.Dictionary? Lods, Mesh.PrimitiveType Primitive)>();
        for (int s = 0; s < surfaces; s++)
        {
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
            if (primitive != Mesh.PrimitiveType.Triangles || !Joinable(material) || vertex.Length == 0 || index.Length == 0)
            {
                kept.Add((arrays, material, lods, primitive));
                continue;
            }
            int cull = twoSided ? 2 : CullOf(Engine_ClassicMaterial.RenderModeOf((ShaderMaterial)material!));
            groups[cull].Positions ??= new List<Vector3>();
            groups[cull].Indices ??= new List<int>();
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
        if (joined - nonEmpty < 1)
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
            result.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, one, null,
                Refii.Entities.Engine_ShadowProxy.MergedLods(groups[cull].Levels));
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
