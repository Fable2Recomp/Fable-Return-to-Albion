using System;
using System.Collections.Generic;
using Godot;
using Refii.Models;

namespace Refii.Physics;

/// <summary>
/// One cloth block joined to the mesh it replaces part of: the source surfaces the guest would
/// have drawn from the record's own buffer (hidden while the cloth draws), the material that
/// surface carried, and which skeleton bone each of the block's bone ids means.
/// </summary>
public sealed class ClothPiece
{
    public ClothAsset Asset { get; init; } = new();

    /// <summary>Source surfaces built from the cloth record's buffer, by index bit.</summary>
    public UInt128 HiddenSurfaces { get; init; }

    /// <summary>The material of the record's largest surface, which the cloth draws with.</summary>
    public Material? Material { get; init; }

    /// <summary>The surface of the piece's own model <see cref="Material"/> was taken from, or -1.</summary>
    public int MaterialSurface { get; init; } = -1;

    /// <summary>
    /// Per cloth bone id, the bone of the mesh's own table — identity for a single model; the
    /// composite's union index for a part, matched by name the way the composite remapped the
    /// part's blend indices. -1 where the id names no bone.
    /// </summary>
    public int[] BoneRemap { get; init; } = Array.Empty<int>();

    public string ModelPath { get; init; } = string.Empty;
}

/// <summary>
/// Finds the cloth blocks behind a drawn mesh. The guest draws a cloth record from the block's
/// OWN vertex set, index list and UVs, never from the record's buffer — that buffer is only
/// what draws when the cloth is out of range — so the join is not vertex to vertex. It is
/// surface to record: a model's mesh maps each render vertex to the buffer vertex it came
/// from, and the record's buffer range names the surfaces the cloth stands in for. A
/// composite records where each part's surfaces landed, so a part's surfaces are lifted by
/// that table.
/// </summary>
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
            !mesh.HasMeta(Engine_MdlComposite.SurfacePartsMeta))
        {
            return pieces;
        }

        string[] partPaths = mesh.GetMeta(Engine_MdlComposite.PartPathsMeta).AsStringArray();
        int[] surfaceParts = mesh.GetMeta(Engine_MdlComposite.SurfacePartsMeta).AsInt32Array();
        Dictionary<string, int>? unionBones = UnionBoneIndex(mesh);

        for (int i = 0; i < partPaths.Length; i++)
        {
            string path = partPaths[i];

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

            int[]? remap = RemapForPart(part, unionBones);

            foreach (ClothPiece piece in For(path, part))
            {
                UInt128 lifted = LiftSurfaces(piece.HiddenSurfaces, i, surfaceParts);
                if (lifted == UInt128.Zero)
                {
                    continue;
                }

                pieces.Add(new ClothPiece
                {
                    Asset = piece.Asset,
                    HiddenSurfaces = lifted,
                    Material = CompositeMaterial(piece, mesh, i, surfaceParts),
                    MaterialSurface = piece.MaterialSurface,
                    BoneRemap = remap is null ? piece.BoneRemap : Compose(piece.BoneRemap, remap),
                    ModelPath = path,
                });
            }
        }

        return pieces;
    }

    /// <summary>
    /// The material the COMPOSITE wears on the surface the piece took its own from. A
    /// character's composite carries private copies where the guest stated texture layers — a
    /// dye, a morph — and the piece's own is the part's shared, unlayered one.
    /// </summary>
    private static Material? CompositeMaterial(
        ClothPiece piece, ArrayMesh composite, int partIndex, int[] surfaceParts)
    {
        int partSurface = piece.MaterialSurface;
        if (partSurface < 0)
        {
            return piece.Material;
        }

        int seen = 0;
        for (int j = 0; j < surfaceParts.Length && j < composite.GetSurfaceCount(); j++)
        {
            if (surfaceParts[j] != partIndex)
            {
                continue;
            }

            if (seen == partSurface)
            {
                return composite.SurfaceGetMaterial(j) ?? piece.Material;
            }

            seen++;
        }

        return piece.Material;
    }

    /// <summary>
    /// The part's k-th surface is the composite's j-th surface with <c>surfaceParts[j] == part</c>,
    /// counting in order — the composite adds a part's surfaces in the part's order.
    /// </summary>
    private static UInt128 LiftSurfaces(UInt128 partMask, int part, int[] surfaceParts)
    {
        UInt128 lifted = UInt128.Zero;
        int k = 0;
        for (int j = 0; j < surfaceParts.Length && j < 128; j++)
        {
            if (surfaceParts[j] != part)
            {
                continue;
            }

            if (k < 128 && (partMask & (UInt128.One << k)) != UInt128.Zero)
            {
                lifted |= UInt128.One << j;
            }

            k++;
        }

        return lifted;
    }

    /// <summary>The composite's bone name → union index, or null when it carries no rig.</summary>
    private static Dictionary<string, int>? UnionBoneIndex(ArrayMesh composite)
    {
        if (!composite.HasMeta(Engine_MdlMesh.BoneNamesMeta))
        {
            return null;
        }

        string[] names = composite.GetMeta(Engine_MdlMesh.BoneNamesMeta).AsStringArray();
        var index = new Dictionary<string, int>(names.Length);
        for (int b = 0; b < names.Length; b++)
        {
            index.TryAdd(names[b], b);
        }

        return index;
    }

    /// <summary>
    /// The part's bone table → the union, by name, as <c>Engine_MdlComposite.Absorb</c> did for
    /// its blend indices. A part with no rig had its indices collapsed to bone 0 there; the same
    /// here, so the cloth at least rides the root with the geometry.
    /// </summary>
    private static int[]? RemapForPart(ArrayMesh part, Dictionary<string, int>? union)
    {
        if (union is null)
        {
            return null;
        }

        if (Engine_MdlRig.TryFrom(part) is not { } table)
        {
            return new[] { 0 };
        }

        var remap = new int[table.BoneCount];
        for (int b = 0; b < table.BoneCount; b++)
        {
            remap[b] = union.TryGetValue(table.Names[b], out int at) ? at : 0;
        }

        return remap;
    }

    private static int[] Compose(int[] inner, int[] outer)
    {
        var composed = new int[inner.Length];
        for (int b = 0; b < inner.Length; b++)
        {
            int local = inner[b];
            composed[b] = local < 0 ? -1 : local < outer.Length ? outer[local] : 0;
        }

        return composed;
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
            info.ClothBlocks.Count == 0 ||
            !mesh.HasMeta(Engine_MdlMesh.SourceVertexMapMeta))
        {
            return pieces;
        }

        int[] map = mesh.GetMeta(Engine_MdlMesh.SourceVertexMapMeta).AsInt32Array();
        int surfaceCount = mesh.GetSurfaceCount();
        var surfaceBase = new int[surfaceCount + 1];
        for (int s = 0; s < surfaceCount; s++)
        {
            surfaceBase[s + 1] = surfaceBase[s] + Engine_MdlSurfaceArrays.VertexCount(mesh, s);
        }

        if (map.Length != surfaceBase[surfaceCount])
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

        int[] identity = IdentityRemap(mesh);
        int skipped = 0;

        foreach (MdlClothBlock block in info.ClothBlocks)
        {
            int record = (int)block.RecordIndex;
            if (record < 0 || record >= bases.Count || bases[record].Count == 0)
            {
                skipped++;
                continue;
            }

            (uint count, int start) = bases[record];

            // The surfaces the record's buffer feeds, and the material of the largest.
            UInt128 hidden = UInt128.Zero;
            Material? material = null;
            int materialSurface = -1;
            int best = 0;
            for (int s = 0; s < surfaceCount && s < 128; s++)
            {
                int hits = 0;
                for (int v = surfaceBase[s]; v < surfaceBase[s + 1]; v++)
                {
                    int local = map[v] - start;
                    if (local >= 0 && local < (int)count)
                    {
                        hits++;
                    }
                }

                if (hits == 0)
                {
                    continue;
                }

                hidden |= UInt128.One << s;
                if (hits > best)
                {
                    best = hits;
                    material = mesh.SurfaceGetMaterial(s);
                    materialSurface = s;
                }
            }

            if (hidden == UInt128.Zero)
            {
                skipped++;
                continue;
            }

            ClothAsset? asset = Engine_ClothBuilder.Build(info.Body ?? bytes, block, modelPath);
            if (asset is null || !asset.Simulates || asset.Indices.Length < 3)
            {
                skipped++;
                continue;
            }

            pieces.Add(new ClothPiece
            {
                Asset = asset,
                HiddenSurfaces = hidden,
                Material = material,
                MaterialSurface = materialSurface,
                BoneRemap = identity,
                ModelPath = modelPath,
            });
        }

        if (pieces.Count != 0 || skipped != 0)
        {
            GD.Print($"[cloth] {Leaf(modelPath)}: {pieces.Count} piece(s) standing in for the mesh" +
                (skipped != 0
                    ? $", {skipped} block(s) skipped — no surface of the mesh comes from the record's buffer"
                    : string.Empty));
        }

        return pieces;
    }

    private static int[] IdentityRemap(ArrayMesh mesh)
    {
        int bones = Engine_MdlRig.TryFrom(mesh) is { } rig ? rig.BoneCount : 0;
        var remap = new int[bones];
        for (int b = 0; b < bones; b++)
        {
            remap[b] = b;
        }

        return remap;
    }

    private static string Leaf(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash < 0 ? path : path[(slash + 1)..];
    }
}
