using System.Collections.Generic;
using Refii.Gdb;

namespace Refii.Entities;

public sealed class ReadableInfo
{
    public uint TagsRecordHash { get; set; }

    public List<uint> TagHashes { get; } = new();

    public List<uint> ResolvedTags { get; } = new();

    public List<string> Texts { get; } = new();
}

public static class Engine_EntityReadables
{
    public const uint TextTags = 0x709D872B;
    public const uint ReadableComponent = 0x89ABB47E;
    public const uint TextTag = 0xB8F45248;
    public const uint NameTag = 0x9555A6FC;
    public const uint DescriptionTag = 0xD823B12B;
    public const uint InventoryItemComponent = 0xC3318103;
    public const uint CreatureComponent = 0xC3B90D4F;

    public static readonly uint[] TagFields =
    {
        0x1D280BA4, 0x1D280BA7, 0x1D280BA6, 0x1D280BA1, 0x1D280BA0,
    };

    public static Dictionary<uint, ReadableInfo> Extract(
        GdbMulti multi, IReadOnlyList<KeyValuePair<uint, string>> saveMap)
    {
        var result = new Dictionary<uint, ReadableInfo>();

        foreach (KeyValuePair<uint, string> saved in saveMap)
        {
            if (!multi.TryLookup(saved.Key, out GdbCursor entity))
            {
                continue;
            }

            ReadableInfo? info = ExtractFor(multi, entity);
            if (info is not null)
            {
                result[saved.Key] = info;
            }
        }

        if (multi.Views.Count != 0)
        {
            GdbView levelView = multi.Views[0];
            for (int i = 0; i < levelView.RecordCount; i++)
            {
                uint recordHash = levelView.RecordHash(i);
                if (recordHash == 0 || recordHash == GdbHash.Null ||
                    result.ContainsKey(recordHash))
                {
                    continue;
                }

                ReadableInfo? info = ExtractFor(multi, new GdbCursor(levelView, i));
                if (info is not null)
                {
                    result[recordHash] = info;
                }
            }
        }

        return result;
    }

    private static ReadableInfo? ExtractFor(GdbMulti multi, GdbCursor entity)
    {
        var info = new ReadableInfo();

        if (multi.TryFindInherited(
                entity, ReadableComponent, 6, out _, out uint readableHash, out _) &&
            IsRef(readableHash) &&
            multi.TryLookup(readableHash, out GdbCursor readable) &&
            multi.TryFindInherited(
                readable, TextTag, -1, out _, out uint tagHash, out GdbFieldType tagType) &&
            IsTagType(tagType) && IsRef(tagHash))
        {
            info.TagsRecordHash = readableHash;
            info.TagHashes.Add(tagHash);
        }

        if (info.TagHashes.Count == 0 &&
            multi.TryFindInherited(
                entity, TextTags, 6, out _, out uint tagsHash, out _) &&
            IsRef(tagsHash) && multi.TryLookup(tagsHash, out GdbCursor tags))
        {
            info.TagsRecordHash = tagsHash;
            foreach (uint field in TagFields)
            {
                if (multi.TryFindInherited(
                        tags, field, -1, out _, out uint hash, out GdbFieldType type) &&
                    IsTagType(type) && IsRef(hash))
                {
                    info.TagHashes.Add(hash);
                }
            }
        }

        if (info.TagHashes.Count == 0)
        {
            var scopes = new List<GdbCursor> { entity };
            foreach (uint component in new[] { InventoryItemComponent, CreatureComponent })
            {
                if (multi.TryFindInherited(
                        entity, component, 6, out _, out uint compHash, out _) &&
                    IsRef(compHash) && multi.TryLookup(compHash, out GdbCursor comp))
                {
                    scopes.Add(comp);
                }
            }

            foreach (GdbCursor scope in scopes)
            {
                if (info.TagHashes.Count != 0)
                {
                    break;
                }

                foreach (uint field in new[] { NameTag, DescriptionTag })
                {
                    if (multi.TryFindInherited(
                            scope, field, -1, out _, out uint hash,
                            out GdbFieldType type) &&
                        IsTagType(type) && IsRef(hash))
                    {
                        info.TagHashes.Add(hash);
                    }
                }
            }
        }

        return info.TagHashes.Count != 0 ? info : null;
    }

    private static bool IsRef(uint hash) => hash != 0 && hash != GdbHash.Null;

    private static bool IsTagType(GdbFieldType type) =>
        type is GdbFieldType.String or GdbFieldType.ResourceReference;
}
