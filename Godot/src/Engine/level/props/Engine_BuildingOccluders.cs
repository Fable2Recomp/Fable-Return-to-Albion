using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace Refii.Level;

/// <summary>
/// Occlusion culling from the buildings' own walls (`occlusion_culling`, experimental): every
/// streamed building prop gets an occluder made of its solid surfaces, and the window's
/// viewport culls what they hide.
/// </summary>
/// <remarks>
/// WHY. A town is mostly hidden behind its nearest facades, and nothing culled it: from the
/// Bowerstone Market street the phone drew the far streets, the trees behind the rooftops and
/// the furniture inside every house - about 60% of the opaque pass there is per-vertex and
/// per-draw cost, not pixels.
///
/// THE OCCLUDER IS THE BUILDING ITSELF, not a box: it can hide only what those walls hide, so
/// an archway, a doorway or a street between two houses stays open. Cutout and translucent
/// surfaces (`alpha_scissor_on`: windows, panes, railings, foliage on a wall) are left out,
/// so what shows through them stays drawn. One shape per mesh, shared by every placement.
///
/// Godot rasterises the occluders on its worker threads at a low resolution each frame
/// (Embree, present in the official Android templates) and tests each instance's box against
/// them. It does not cull shadow casters, and a batch is culled only when all of its box is
/// hidden. GameEntities' buildings are not covered.
///
/// MEASURED, and why it is off: at the Bowerstone Market save spot on the S25+, 513 occluders
/// (245k triangles) cut draw calls by 15% and shadow primitives by 7%, but GPU work by only 3%
/// - most of what they hide is cheap to draw, and the prop batches are rarely hidden whole.
/// The raycasting adds worker-thread CPU on a phone that heats as it works.
/// </remarks>
public static class Engine_BuildingOccluders
{
    private static readonly Dictionary<Mesh, ArrayOccluder3D[]> Shapes = new();
    private static readonly Queue<(GeometryInstance3D Instance, Mesh Mesh, IReadOnlyList<Transform3D>? Placements)> Pending = new();

    /// <summary>Main-thread time per frame for making occluders: a region holds hundreds.</summary>
    private const double BudgetMs = 1.5;
    private static bool? _enabled;

    /// <summary>Occluders made, and their triangles, for the debug channel.</summary>
    public static int Placed;
    public static long Triangles;

    /// <summary>The launch setting; read once.</summary>
    public static bool Enabled => _enabled ??= Refii.Core.Engine_RefiiConfig.Bool(
        Refii.Core.Engine_RefiiConfig.Read(out _), "occlusion_culling", false);

    /// <summary>Queues a streamed building prop for its occluders, one per placement.</summary>
    /// <param name="instance">The prop node, already holding <paramref name="mesh"/>.</param>
    /// <param name="mesh">Its model.</param>
    /// <param name="placements">A batch's instance transforms in its local space; null for a single node.</param>
    public static void Add(GeometryInstance3D instance, Mesh mesh, IReadOnlyList<Transform3D>? placements)
    {
        if (Enabled)
        {
            Pending.Enqueue((instance, mesh, placements));
        }
    }

    /// <summary>Makes queued occluders within the frame's budget; the prop streamer calls it each frame.</summary>
    public static void Pump()
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        while (Pending.Count > 0 &&
            System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds < BudgetMs)
        {
            (GeometryInstance3D instance, Mesh mesh, IReadOnlyList<Transform3D>? placements) = Pending.Dequeue();
            if (GodotObject.IsInstanceValid(instance) && instance.IsInsideTree())
            {
                Place(instance, mesh, placements);
            }
        }
    }

    private static void Place(GeometryInstance3D instance, Mesh mesh, IReadOnlyList<Transform3D>? placements)
    {
        ArrayOccluder3D[] shapes = Shape(mesh);
        if (shapes.Length == 0)
        {
            return;
        }

        int count = placements?.Count ?? 1;
        for (int i = 0; i < count; i++)
        {
            foreach (ArrayOccluder3D shape in shapes)
            {
                instance.AddChild(new OccluderInstance3D
                {
                    Occluder = shape,
                    Transform = placements?[i] ?? Transform3D.Identity,
                });
            }
        }
        Placed += count;

        // The window draws the world; the occluders mean nothing until its viewport tests them.
        if (instance.GetViewport() is { UseOcclusionCulling: false } viewport)
        {
            viewport.UseOcclusionCulling = true;
        }
    }

    /// <summary>The mesh's solid triangles as occluders of at most <see cref="ChunkVertices"/> each.</summary>
    /// <remarks>
    /// IN CHUNKS BECAUSE EMBREE READS PAST THE LAST VERTEX. Its SIMD loads take 16 bytes for a
    /// 12-byte position, and Godot gives it each occluder's transformed vertices unpadded. A
    /// large buffer gets pages of its own from Android's allocator with a guard page after it,
    /// and the phone faulted there every launch (libgodot +0x2051788, the address exactly
    /// page-aligned). Small buffers come from the allocator's size-class blocks, where the
    /// four bytes past the end are still mapped.
    /// </remarks>
    private static ArrayOccluder3D[] Shape(Mesh mesh)
    {
        if (Shapes.TryGetValue(mesh, out ArrayOccluder3D[]? cached))
        {
            return cached;
        }

        var shapes = new List<ArrayOccluder3D>();
        var vertices = new List<Vector3>();
        var indices = new List<int>();
        var remap = new Dictionary<int, int>();
        void Flush()
        {
            if (indices.Count >= 3)
            {
                var shape = new ArrayOccluder3D();
                shape.SetArrays(vertices.ToArray(), indices.ToArray());
                shapes.Add(shape);
                Triangles += indices.Count / 3;
            }
            vertices.Clear();
            indices.Clear();
            remap.Clear();
        }

        for (int s = 0; s < mesh.GetSurfaceCount(); s++)
        {
            if ((mesh is ArrayMesh arrayMesh && arrayMesh.SurfaceGetPrimitiveType(s) != Mesh.PrimitiveType.Triangles) ||
                SeeThrough(mesh.SurfaceGetMaterial(s)))
            {
                continue;
            }

            // The builder's own CPU copy: SurfaceGetArrays is a GPU readback (Engine_MdlSurfaceArrays).
            Godot.Collections.Array arrays = Refii.Models.Engine_MdlSurfaceArrays.Of(mesh, s);
            if (arrays.Count <= (int)Mesh.ArrayType.Index ||
                arrays[(int)Mesh.ArrayType.Vertex].Obj is not Vector3[] source)
            {
                continue;
            }

            int[] triangles = arrays[(int)Mesh.ArrayType.Index].Obj is int[] given && given.Length >= 3
                ? given
                : Enumerable.Range(0, source.Length - source.Length % 3).ToArray();
            for (int t = 0; t + 2 < triangles.Length; t += 3)
            {
                if (vertices.Count + 3 > ChunkVertices)
                {
                    Flush();
                }

                for (int k = 0; k < 3; k++)
                {
                    int key = (s << 24) ^ triangles[t + k];
                    if (!remap.TryGetValue(key, out int at))
                    {
                        at = vertices.Count;
                        vertices.Add(source[triangles[t + k]]);
                        remap[key] = at;
                    }
                    indices.Add(at);
                }
            }
        }
        Flush();

        ArrayOccluder3D[] result = shapes.ToArray();
        Shapes[mesh] = result;
        return result;
    }

    /// <summary>Vertices per occluder: 48 KB of positions, inside the allocator's small blocks.</summary>
    private const int ChunkVertices = 4096;

    /// <summary>A cutout or translucent surface, which something behind it shows through.</summary>
    private static bool SeeThrough(Material? material) =>
        material is not ShaderMaterial shader || shader.GetShaderParameter("alpha_scissor_on").AsBool();

    /// <summary>Forgets the shapes with the region that owned them.</summary>
    public static void Clear()
    {
        Shapes.Clear();
        Pending.Clear();
        Placed = 0;
        Triangles = 0;
    }
}
