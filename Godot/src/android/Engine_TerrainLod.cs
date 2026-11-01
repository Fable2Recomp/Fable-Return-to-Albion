using System;
using Godot;

namespace Refii.Level;

/// <summary>
/// What a ground surface gets beside its arrays (Engine_LevelMesh): generated levels under
/// `terrain_lod`, and under the lite variant its steepest normal for the no-cliff chunks.
/// </summary>
public static class Engine_TerrainLod
{
    /// <summary>
    /// The levels for a surface about to be added to <paramref name="mesh"/>, or null; under the
    /// lite variant also folds the surface's steepest normal into the mesh's
    /// <see cref="Engine_TerrainSplatMaterial.MinUpMeta"/>.
    /// </summary>
    internal static Godot.Collections.Dictionary? For(
        ArrayMesh mesh, Godot.Collections.Array arrays, Vector3[] vertices, Vector3[] normals, int indexCount)
    {
        Godot.Collections.Dictionary? lods = TerrainLodEnabled() ? GenerateLods(arrays, vertices, indexCount) : null;

        // The steepest normal, for the lite splat's no-cliff chunks (Engine_TerrainSplatMaterial.ForChunk).
        if (Refii.Models.Engine_ShaderVariant.Lite)
        {
            float minUp = mesh.HasMeta(Engine_TerrainSplatMaterial.MinUpMeta)
                ? mesh.GetMeta(Engine_TerrainSplatMaterial.MinUpMeta).AsSingle()
                : 1f;
            foreach (Vector3 normal in normals)
            {
                float length = normal.Length();
                minUp = MathF.Min(minUp, length > 1e-6f ? MathF.Abs(normal.Y) / length : 0f);
            }

            mesh.SetMeta(Engine_TerrainSplatMaterial.MinUpMeta, minUp);
        }

        return lods;
    }

    /// <summary>The coarser levels' error, relative to a surface's largest extent: on a 32 m chunk
    /// 6 cm, 26 cm and 1 m.</summary>
    private static readonly float[] LodErrors = { 0.002f, 0.008f, 0.03f };

    /// <summary>
    /// Coarser index lists over the same vertices, meshoptimizer's (Godot's SurfaceTool) with the
    /// border locked so neighbouring chunks at different levels still meet exactly. Keyed as
    /// Godot's importer keys them, by absolute error, so a level draws once that error projects
    /// under `mesh_lod_pixels`.
    /// </summary>
    /// <remarks>THE GROUND HAD NO LOD AT ALL: a Bowerstone frame drew 600k terrain triangles,
    /// distant chunks at the full 8,192, and on an Adreno 640 triangles cost ~12 GPU cycles each.
    /// A level that saves under 30% of its predecessor is not kept.</remarks>
    private static Godot.Collections.Dictionary? GenerateLods(Godot.Collections.Array arrays, Vector3[] vertices, int indexCount)
    {
        if (indexCount < 600 || vertices.Length == 0)
        {
            return null;
        }

        Vector3 min = vertices[0], max = vertices[0];
        foreach (Vector3 v in vertices)
        {
            min = min.Min(v);
            max = max.Max(v);
        }

        Vector3 size = max - min;
        float extent = Math.Max(size.X, Math.Max(size.Y, size.Z));
        if (!(extent > 0f))
        {
            return null;
        }

        using var tool = new SurfaceTool();
        tool.CreateFromArrays(arrays);
        GC.KeepAlive(arrays);
        Godot.Collections.Dictionary? lods = null;
        int previous = indexCount;
        foreach (float error in LodErrors)
        {
            int[] level = tool.GenerateLod(error, 3);
            if (level.Length < 3 || level.Length > previous * 7 / 10)
            {
                continue;
            }

            lods ??= new Godot.Collections.Dictionary();
            lods[error * extent] = level;
            previous = level.Length;
        }

        if (lods is not null)
        {
            System.Threading.Interlocked.Increment(ref _surfacesWithLods);
        }

        return lods;
    }

    private static int _surfacesWithLods;

    /// <summary>Terrain surfaces given generated levels since start-up.</summary>
    public static int SurfacesWithLods => System.Threading.Volatile.Read(ref _surfacesWithLods);

    /// <summary>`terrain_lod`: generated levels for the ground's chunks and vistas. Default on for
    /// Android, off elsewhere (the desktop draws the heightfield at full detail, as the 360 did).</summary>
    internal static bool TerrainLodEnabled()
    {
        try
        {
            return Refii.Core.Engine_RefiiConfig.Bool(Refii.Core.Engine_RefiiConfig.Read(out _), "terrain_lod",
                Refii.Core.Engine_Platform.IsAndroid);
        }
        catch (Exception)
        {
            return Refii.Core.Engine_Platform.IsAndroid;
        }
    }
}
