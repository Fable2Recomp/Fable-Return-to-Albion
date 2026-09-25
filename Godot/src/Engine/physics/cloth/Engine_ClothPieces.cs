using System;
using System.Collections.Generic;
using Godot;
using Refii.Models;

namespace Refii.Physics;

public sealed class ClothPiece
{
    public ClothAsset Asset { get; init; } = new();

    public int[] ClothVertexOf { get; init; } = Array.Empty<int>();

    public string ModelPath { get; init; } = string.Empty;

    public int DrivenVertices { get; init; }
}

public static class Engine_ClothPieces
{
    private static readonly object CacheLock = new();

    private static readonly Dictionary<string, List<ClothPiece>> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    public static void Invalidate()
    {
        lock (CacheLock)
        {
            Cache.Clear();
        }
    }

    public static List<ClothPiece> ForMesh(
        IReadOnlyList<string> modelPaths, ArrayMesh mesh, Func<string, ArrayMesh?> resolvePart)
    {
        var pieces = new List<ClothPiece>();

        if (mesh.HasMeta(Engine_MdlMesh.SourceVertexMapMeta))
        {
            foreach (string path in modelPaths)
            {
                pieces.AddRange(For(path, mesh));
            }

            return pieces;
        }

        if (!mesh.HasMeta(Engine_MdlComposite.PartPathsMeta) ||
            !mesh.HasMeta(Engine_MdlComposite.PartSpansMeta))
        {
            return pieces;
        }

        string[] partPaths = mesh.GetMeta(Engine_MdlComposite.PartPathsMeta).AsStringArray();
        int[] spans = mesh.GetMeta(Engine_MdlComposite.PartSpansMeta).AsInt32Array();
        int total = TotalVertices(mesh);

        for (int i = 0; i < partPaths.Length && (i * 2) + 1 < spans.Length; i++)
        {
            string path = partPaths[i];
            int start = spans[i * 2];
            int count = spans[(i * 2) + 1];

            if (count <= 0 || start < 0 || start + count > total)
            {
                continue;
            }

            bool wanted = false;
            foreach (string requested in modelPaths)
            {
                if (string.Equals(requested, path, StringComparison.OrdinalIgnoreCase))
                {
                    wanted = true;
                    break;
                }
            }

            if (!wanted || resolvePart(path) is not ArrayMesh part)
            {
                continue;
            }

            foreach (ClothPiece piece in For(path, part))
            {
                if (piece.ClothVertexOf.Length != count)
                {
                    continue;
                }

                var lifted = new int[total];
                for (int v = 0; v < total; v++)
                {
                    lifted[v] = -1;
                }

                for (int v = 0; v < count; v++)
                {
                    lifted[start + v] = piece.ClothVertexOf[v];
                }

                pieces.Add(new ClothPiece
                {
                    Asset = piece.Asset,
                    ClothVertexOf = lifted,
                    ModelPath = path,
                    DrivenVertices = piece.DrivenVertices,
                });
            }
        }

        return pieces;
    }

    public static int TotalVertices(ArrayMesh mesh)
    {
        int total = 0;
        for (int surface = 0; surface < mesh.GetSurfaceCount(); surface++)
        {
            total += Refii.Models.Engine_MdlSurfaceArrays.VertexCount(mesh, surface);
        }

        return total;
    }

    public static IReadOnlyList<ClothPiece> For(string modelPath, ArrayMesh mesh)
    {
        if (modelPath.Length == 0)
        {
            return Array.Empty<ClothPiece>();
        }

        lock (CacheLock)
        {
            if (Cache.TryGetValue(modelPath, out List<ClothPiece>? cached))
            {
                return cached;
            }
        }

        List<ClothPiece> built = Build(modelPath, mesh);

        lock (CacheLock)
        {
            Cache[modelPath] = built;
        }

        return built;
    }

    private static List<ClothPiece> Build(string modelPath, ArrayMesh mesh)
    {
        var pieces = new List<ClothPiece>();

        byte[]? bytes = Engine_BnkMeshLoader.ReadBytes(modelPath);
        if (bytes is null ||
            !Engine_MdlData.TryParseInfo(bytes, modelPath, out MdlInfo info, out _) ||
            info.ClothBlocks.Count == 0)
        {
            return pieces;
        }

        if (!mesh.HasMeta(Engine_MdlMesh.SourceVertexMapMeta))
        {
            return pieces;
        }

        int[] map = mesh.GetMeta(Engine_MdlMesh.SourceVertexMapMeta).AsInt32Array();
        if (map.Length == 0)
        {
            return pieces;
        }

        Vector3[] positions = VertexPositions(mesh);
        if (positions.Length != map.Length)
        {
            return pieces;
        }

        var bases = new List<(uint Count, int Base)>();
        int running = 0;
        foreach (MdlMeshBuffer buffer in info.MeshBuffers)
        {
            bases.Add((buffer.VertexCount, running));
            running += (int)buffer.VertexCount;
        }

        int skipped = 0;

        foreach (MdlClothBlock block in info.ClothBlocks)
        {
            int record = (int)block.RecordIndex;
            if (record < 0 || record >= bases.Count)
            {
                skipped++;
                continue;
            }

            (uint count, int start) = bases[record];
            if (count != block.SimVertexCount || count == 0)
            {
                skipped++;
                continue;
            }

            var clothVertexOf = new int[map.Length];
            var bind = new PhysVec4[count];
            var seeded = new bool[count];
            int driven = 0;

            for (int v = 0; v < map.Length; v++)
            {
                int local = map[v] - start;
                if (local < 0 || local >= (int)count)
                {
                    clothVertexOf[v] = -1;
                    continue;
                }

                clothVertexOf[v] = local;
                driven++;

                if (!seeded[local])
                {
                    seeded[local] = true;
                    Vector3 p = positions[v];
                    bind[local] = new PhysVec4(p.X, p.Y, p.Z, 1.0f);
                }
            }

            if (driven == 0)
            {
                skipped++;
                continue;
            }

            ClothAsset? asset = Engine_ClothBuilder.Build(bytes, block, modelPath, bind);
            if (asset is null || !asset.Simulates)
            {
                skipped++;
                continue;
            }

            pieces.Add(new ClothPiece
            {
                Asset = asset,
                ClothVertexOf = clothVertexOf,
                ModelPath = modelPath,
                DrivenVertices = driven,
            });
        }

        if (pieces.Count != 0 || skipped != 0)
        {
            GD.Print($"[cloth] {Leaf(modelPath)}: {pieces.Count} piece(s) driving the mesh" +
                (skipped != 0
                    ? $", {skipped} block(s) skipped — the buffer holds a different vertex " +
                      "count than the block simulates"
                    : string.Empty));
        }

        return pieces;
    }

    public static Vector3[] VertexPositions(ArrayMesh mesh)
    {
        var all = new List<Vector3>();
        for (int surface = 0; surface < mesh.GetSurfaceCount(); surface++)
        {
            Godot.Collections.Array arrays = Refii.Models.Engine_MdlSurfaceArrays.Of(mesh, surface);
            all.AddRange(arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array());
        }

        return all.ToArray();
    }

    private static string Leaf(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash < 0 ? path : path[(slash + 1)..];
    }
}
