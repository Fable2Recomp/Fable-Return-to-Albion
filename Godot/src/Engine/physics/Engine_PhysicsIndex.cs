using System;
using System.Collections.Generic;
using Godot;
using Refii.Models;

namespace Refii.Physics;

public static class Engine_PhysicsIndex
{
    private static readonly object IndexLock = new();

    private static readonly Dictionary<string, List<ClothAsset>> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    private static Func<string, byte[]?>? _resolver;

    public static void SetResolver(Func<string, byte[]?>? resolver)
    {
        lock (IndexLock)
        {
            _resolver = resolver;
            Cache.Clear();
        }
    }

    public static void Invalidate()
    {
        lock (IndexLock)
        {
            Cache.Clear();
        }
    }

    public static bool HasCloth(string modelPath) => AssetsFor(modelPath).Count != 0;

    public static IReadOnlyList<ClothAsset> AssetsFor(string modelPath)
    {
        if (modelPath.Length == 0)
        {
            return Array.Empty<ClothAsset>();
        }

        lock (IndexLock)
        {
            if (Cache.TryGetValue(modelPath, out List<ClothAsset>? cached))
            {
                return cached;
            }
        }

        List<ClothAsset> built = BuildFor(modelPath);

        lock (IndexLock)
        {
            Cache[modelPath] = built;
        }

        return built;
    }

    public static List<ClothAsset> FromBytes(byte[] bytes, string sourcePath)
    {
        var assets = new List<ClothAsset>();

        if (bytes.Length == 0 || !Engine_MdlData.TryParseInfo(bytes, out MdlInfo info, out _))
        {
            return assets;
        }

        foreach (MdlClothBlock block in info.ClothBlocks)
        {
            ClothAsset? asset = Engine_ClothBuilder.Build(bytes, block, sourcePath);
            if (asset is not null && asset.Simulates)
            {
                assets.Add(asset);
            }
        }

        return assets;
    }

    private static List<ClothAsset> BuildFor(string modelPath)
    {
        Func<string, byte[]?>? resolver;
        lock (IndexLock)
        {
            resolver = _resolver;
        }

        byte[]? bytes = resolver?.Invoke(modelPath) ?? Engine_BnkMeshLoader.ReadBytes(modelPath);
        if (bytes is null)
        {
            return new List<ClothAsset>();
        }

        List<ClothAsset> assets = FromBytes(bytes, modelPath);
        if (assets.Count != 0)
        {
            GD.Print($"[physics] cloth on {modelPath}: {assets.Count} piece(s), " +
                $"{Vertices(assets)} vertices, {Edges(assets)} constraints");
        }

        return assets;
    }

    public static int Vertices(IReadOnlyList<ClothAsset> assets)
    {
        int total = 0;
        foreach (ClothAsset asset in assets)
        {
            total += asset.VertexCount;
        }

        return total;
    }

    public static int Edges(IReadOnlyList<ClothAsset> assets)
    {
        int total = 0;
        foreach (ClothAsset asset in assets)
        {
            total += asset.EdgeCount;
        }

        return total;
    }
}
