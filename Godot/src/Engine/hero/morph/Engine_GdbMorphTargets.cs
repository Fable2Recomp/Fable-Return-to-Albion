using System.Collections.Generic;
using Refii.Gdb;

namespace Refii.Hero;

public readonly struct MorphTargetPair
{
    public MorphTargetPair(uint originalModelHash, uint targetModelHash, uint morphType)
    {
        OriginalModelHash = originalModelHash;
        TargetModelHash = targetModelHash;
        MorphType = morphType;
    }

    public uint OriginalModelHash { get; }

    public uint TargetModelHash { get; }

    public uint MorphType { get; }
}

public static class Engine_GdbMorphTargets
{
    public const uint MorphTargets = 0xA1F45CDD;
    public const uint OriginalModel = 0xD97C293B;
    public const uint TargetModel = 0x1A77CE85;
    public const uint MorphType = 0x3BF01BE1;

    public const int WalkDepth = 64;

    public static List<MorphTargetPair> Build(GdbMulti multi)
    {
        var pairs = new List<MorphTargetPair>();
        var seenLists = new HashSet<uint>();
        var seenPairs = new HashSet<(uint, uint, uint)>();

        foreach (GdbView view in multi.Views)
        {
            for (int i = 0; i < view.RecordCount; i++)
            {
                var record = new GdbCursor(view, i);

                if (!multi.TryFindInherited(
                        record, MorphTargets, 6, out _, out uint listHash, out _) ||
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
        List<MorphTargetPair> pairs,
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

                if (!TryReadPair(multi, entry, out MorphTargetPair pair))
                {
                    continue;
                }

                if (seenPairs.Add((pair.OriginalModelHash, pair.TargetModelHash, pair.MorphType)))
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

    private static bool TryReadPair(GdbMulti multi, GdbCursor entry, out MorphTargetPair pair)
    {
        pair = default;

        if (!multi.TryFindInherited(
                entry, OriginalModel, -1, out _, out uint original,
                out GdbFieldType originalType) ||
            !multi.TryFindInherited(
                entry, TargetModel, -1, out _, out uint target,
                out GdbFieldType targetType) ||
            !multi.TryFindInherited(
                entry, MorphType, -1, out _, out uint morphType,
                out GdbFieldType morphFieldType))
        {
            return false;
        }

        if (!IsTagType(originalType) || !IsTagType(targetType) ||
            !IsIntType(morphFieldType) || !IsRef(original) || !IsRef(target))
        {
            return false;
        }

        pair = new MorphTargetPair(original, target, morphType);
        return true;
    }

    private static bool IsRef(uint hash) => hash != 0 && hash != GdbHash.Null;

    private static bool IsIntType(GdbFieldType type) =>
        type is GdbFieldType.Int or GdbFieldType.Enum;

    private static bool IsTagType(GdbFieldType type) =>
        type is GdbFieldType.String or GdbFieldType.ResourceReference;
}
