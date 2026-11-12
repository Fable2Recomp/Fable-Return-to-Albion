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
        ArrayMesh mesh, Godot.Collections.Array arrays, Vector3[] vertices, Vector3[] normals, int[] indices)
    {
        Godot.Collections.Dictionary? lods = TerrainLodEnabled() ? CachedLods(arrays, vertices, normals, indices) : null;

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

    /// <summary>Raised when <see cref="GenerateLods"/> makes different levels from the same surface.</summary>
    private const int CacheFormat = 2;

    /// <summary>
    /// <see cref="GenerateLods"/>'s levels for a surface, from the disk cache when it made them
    /// for the same surface before.
    /// </summary>
    /// <remarks>GENERATING THEM WAS MOST OF A REGION'S GROUND BUILD: about 0.5 s on each of six
    /// workers for Bower Lake on a OnePlus 7 Pro, every load. Keyed on the surface, the levels'
    /// errors and Godot's build (meshoptimizer is Godot's), not on ours, so an update keeps them.</remarks>
    private static Godot.Collections.Dictionary? CachedLods(
        Godot.Collections.Array arrays, Vector3[] vertices, Vector3[] normals, int[] indices)
    {
        if (indices.Length < 600 || vertices.Length == 0)
        {
            return null;
        }

        byte[] key = LodKey(vertices, normals, indices);
        string path = System.IO.Path.Combine(Refii.Core.Engine_Title.Active.CacheDirectory, "terrain-lod",
            Convert.ToHexString(key, 0, 16) + ".bin");
        if (Refii.Core.Engine_KeyedFileCache.TryRead(path, key, out byte[] kept) && TryReadLods(kept, out var cached))
        {
            if (cached is not null)
            {
                System.Threading.Interlocked.Increment(ref _surfacesWithLods);
            }

            return cached;
        }

        Godot.Collections.Dictionary? lods = GenerateLods(arrays, vertices, indices.Length);
        Refii.Core.Engine_KeyedFileCache.Write(path, key, WriteLods(lods));
        return lods;
    }

    private static byte[] LodKey(Vector3[] vertices, Vector3[] normals, int[] indices)
    {
        using var hash = Refii.Core.Engine_Hash.CreateIncrementalSha256();
        void Add(ReadOnlySpan<byte> bytes)
        {
            Span<byte> number = stackalloc byte[8];
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(number, bytes.Length);
            hash.AppendData(number);
            hash.AppendData(bytes);
        }

        Add(BitConverter.GetBytes(CacheFormat));
        Add(GodotBuild.Value);
        Add(System.Runtime.InteropServices.MemoryMarshal.AsBytes(LodErrors.AsSpan()));
        Add(System.Runtime.InteropServices.MemoryMarshal.AsBytes(vertices.AsSpan()));
        Add(System.Runtime.InteropServices.MemoryMarshal.AsBytes(normals.AsSpan()));
        Add(System.Runtime.InteropServices.MemoryMarshal.AsBytes(indices.AsSpan()));
        return hash.GetHashAndReset();
    }

    private static readonly Lazy<byte[]> GodotBuild = new(() =>
        System.Text.Encoding.UTF8.GetBytes(Godot.Engine.GetVersionInfo()["hash"].AsString()));

    /// <summary>The levels as a count, then each one's key, index count and indices, as u16s when they
    /// fit (a ground chunk has about 4,200 vertices); no levels is a count of 0.</summary>
    private static byte[] WriteLods(Godot.Collections.Dictionary? lods)
    {
        using var bytes = new System.IO.MemoryStream();
        using var writer = new System.IO.BinaryWriter(bytes);
        writer.Write(lods?.Count ?? 0);
        if (lods is not null)
        {
            foreach (var (distance, level) in lods)
            {
                int[] indices = level.AsInt32Array();
                bool narrow = Array.TrueForAll(indices, index => (uint)index <= ushort.MaxValue);
                writer.Write(distance.AsSingle());
                writer.Write(narrow ? indices.Length : -indices.Length);
                if (narrow)
                {
                    foreach (int index in indices)
                    {
                        writer.Write((ushort)index);
                    }
                }
                else
                {
                    writer.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(indices.AsSpan()));
                }
            }
        }

        writer.Flush();
        return bytes.ToArray();
    }

    private static bool TryReadLods(byte[] bytes, out Godot.Collections.Dictionary? lods)
    {
        lods = null;
        var span = new ReadOnlySpan<byte>(bytes);
        if (span.Length < 4)
        {
            return false;
        }

        int count = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(span);
        int at = 4;
        if (count < 0 || count > LodErrors.Length)
        {
            return false;
        }

        for (int n = 0; n < count; n++)
        {
            if (span.Length < at + 8)
            {
                return false;
            }

            float distance = System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(span[at..]);
            int stored = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(span[(at + 4)..]);
            at += 8;
            bool narrow = stored > 0;
            int length = narrow ? stored : -stored;
            int width = narrow ? 2 : 4;
            if (length < 3 || (long)length * width > span.Length - at)
            {
                return false;
            }

            int[] level = new int[length];
            if (narrow)
            {
                ReadOnlySpan<ushort> narrowLevel =
                    System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(span.Slice(at, length * 2));
                for (int index = 0; index < length; index++)
                {
                    level[index] = narrowLevel[index];
                }
            }
            else
            {
                System.Runtime.InteropServices.MemoryMarshal.Cast<byte, int>(span.Slice(at, length * 4)).CopyTo(level);
            }

            at += length * width;
            lods ??= new Godot.Collections.Dictionary();
            lods[distance] = level;
        }

        return at == span.Length;
    }

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
            // SurfaceTool.GenerateLod is intentionally used here for raw index-buffer simplification
            // directly on normal-stripped terrain arrays without building an ImporterMesh asset.
#pragma warning disable CS0618
            int[] level = tool.GenerateLod(error, 3);
#pragma warning restore CS0618
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
