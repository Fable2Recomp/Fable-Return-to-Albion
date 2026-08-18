using System.Collections.Generic;
using Refii.Gdb;

namespace Refii.Hero;

public readonly struct TextureMorphPair
{
    public TextureMorphPair(
        uint originalTextureHash, uint morphTextureHash, uint morphType, uint layer)
    {
        OriginalTextureHash = originalTextureHash;
        MorphTextureHash = morphTextureHash;
        MorphType = morphType;
        Layer = layer;
    }

    public uint OriginalTextureHash { get; }

    public uint MorphTextureHash { get; }

    public uint MorphType { get; }

    public uint Layer { get; }
}

public static class Engine_GdbTextureMorphs
{
    public const uint TextureMorphs = 0xD29AC07B;
    public const uint OriginalTexture = 0xEC376C75;
    public const uint MorphTexture = 0x2F96241E;
    public const uint MorphType = 0x3BF01BE1;
    public const uint Layer = 0x717476D0;

    public const int WalkDepth = 64;

    public static List<TextureMorphPair> Build(GdbMulti multi)
    {
        var pairs = new List<TextureMorphPair>();
        var seenLists = new HashSet<uint>();
        var seenPairs = new HashSet<(uint, uint, uint)>();

        foreach (GdbView view in multi.Views)
        {
            for (int i = 0; i < view.RecordCount; i++)
            {
                var record = new GdbCursor(view, i);

                if (!multi.TryFindInherited(
                        record, TextureMorphs, 6, out _, out uint listHash, out _) ||
                    !IsRef(listHash) || !seenLists.Add(listHash))
                {
                    continue;
                }

                CollectList(multi, listHash, pairs, seenPairs);
            }
        }

        return pairs;
    }

    private static void CollectList(
        GdbMulti multi,
        uint listHash,
        List<TextureMorphPair> pairs,
        HashSet<(uint, uint, uint)> seenPairs)
    {
        if (!multi.TryLookup(listHash, out GdbCursor walk))
        {
            return;
        }

        var inherited = new HashSet<uint>();
        var visited = new HashSet<uint>();

        for (int depth = 0; depth < WalkDepth; depth++)
        {
            var local = new List<uint>();

            foreach ((uint hash, GdbFieldType type, uint raw) in
                     walk.View.RawFields(walk.Record))
            {
                if (hash == GdbView.HashParent || inherited.Contains(hash))
                {
                    continue;
                }

                local.Add(hash);

                if (type != GdbFieldType.Reference || !IsRef(raw) ||
                    !multi.TryLookup(raw, out GdbCursor entry))
                {
                    continue;
                }

                if (!TryReadPair(multi, entry, out TextureMorphPair pair))
                {
                    continue;
                }

                if (seenPairs.Add(
                        (pair.OriginalTextureHash, pair.MorphTextureHash, pair.MorphType)))
                {
                    pairs.Add(pair);
                }
            }

            foreach (uint hash in local)
            {
                inherited.Add(hash);
            }

            if (!walk.View.TryFindLocalRaw(
                    walk.Record, GdbView.HashParent, 6, out uint parent, out _) ||
                !IsRef(parent) || !visited.Add(parent) ||
                !multi.TryLookup(parent, out walk))
            {
                return;
            }
        }
    }

    private static bool TryReadPair(GdbMulti multi, GdbCursor entry, out TextureMorphPair pair)
    {
        pair = default;

        if (!multi.TryFindInherited(
                entry, OriginalTexture, -1, out _, out uint original,
                out GdbFieldType originalType) ||
            !multi.TryFindInherited(
                entry, MorphTexture, -1, out _, out uint morph,
                out GdbFieldType morphType) ||
            !multi.TryFindInherited(
                entry, MorphType, -1, out _, out uint type,
                out GdbFieldType typeFieldType))
        {
            return false;
        }

        if (!IsTagType(originalType) || !IsTagType(morphType) ||
            !IsIntType(typeFieldType) || !IsRef(original) || !IsRef(morph))
        {
            return false;
        }

        uint layer = 0;
        if (multi.TryFindInherited(
                entry, Layer, -1, out _, out uint layerValue,
                out GdbFieldType layerType) && IsIntType(layerType))
        {
            layer = layerValue;
        }

        pair = new TextureMorphPair(original, morph, type, layer);
        return true;
    }

    private static bool IsRef(uint hash) => hash != 0 && hash != GdbHash.Null;

    private static bool IsIntType(GdbFieldType type) =>
        type is GdbFieldType.Int or GdbFieldType.Enum;

    private static bool IsTagType(GdbFieldType type) =>
        type is GdbFieldType.String or GdbFieldType.ResourceReference;
}
