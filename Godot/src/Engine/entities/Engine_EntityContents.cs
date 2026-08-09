using System.Collections.Generic;
using Refii.Gdb;

namespace Refii.Entities;

public sealed class ContentsItem
{
    public uint RecordHash { get; set; }

    public uint EntryRecordHash { get; set; }

    public string EntryLabel { get; set; } = string.Empty;

    public string NameTag { get; set; } = string.Empty;

    public uint NameTagHash { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    public int Money { get; set; } = -1;

    public float Weight { get; set; } = -1f;

    public float MinFurnitureRating { get; set; } = -1f;

    public float MaxFurnitureRating { get; set; } = -1f;

    public float MinChapter { get; set; } = -1f;

    public float MaxChapter { get; set; } = -1f;

    public List<ContentsItem> RandomChoices { get; } = new();
}

public sealed class EntityContentsInfo
{
    public string EntityName { get; set; } = string.Empty;

    public bool HasInventoryComponent { get; set; }

    public bool HasChestComponent { get; set; }

    public bool IsDigSpot { get; set; }

    public bool IsDiveSpot { get; set; }

    public bool DogCanLeadTo { get; set; }

    public int SilverKeysNeeded { get; set; } = -1;

    public int CanBeStolenFrom { get; set; } = -1;

    public int CanRespawnItemsOnce { get; set; } = -1;

    public int CanRespawnItemsRepeatedly { get; set; } = -1;

    public float ChanceOfRespawning { get; set; } = -1f;

    public List<ContentsItem> InitialItems { get; } = new();

    public List<ContentsItem> PotentialItems { get; } = new();

    public string TypeLabel =>
        HasChestComponent ? "Chest"
        : IsDigSpot ? "Dig spot"
        : IsDiveSpot ? "Dive spot"
        : "Container";
}

public static class Engine_EntityContents
{
    public const uint ChestComponent = 0x379C25A9;
    public const uint SilverKeysNeeded = 0xB208E419;
    public const uint DiggingSpotComponent = 0x4D89E63B;
    public const uint DiveSpotComponent = 0x76E2C0A0;
    public const uint DogLeadToComponent = 0x09D8D1BD;
    public const uint InventoryComponentA = 0x1C7D7B74;
    public const uint InventoryComponentB = 0x73AB8B6A;
    public const uint InitialItems = 0x9C24A50D;
    public const uint ItemRepopulationData = 0xFDF2E63A;
    public const uint PotentialItems = 0x4FB47937;
    public const uint ChanceOfRespawning = 0x993B9AA2;
    public const uint CanBeStolenFrom = 0xFDC33317;
    public const uint CanRespawnItemsOnce = 0xC19C6EE2;
    public const uint CanRespawnRepeatedly = 0x6C5206D2;
    public const uint ItemReference = 0x32A71597;
    public const uint LootWeight = 0x04C07DF9;
    public const uint MinFurnitureRating = 0xCCF9B9CC;
    public const uint MaxFurnitureRating = 0x46976DB6;
    public const uint MinChapterProgress = 0xABD2D73B;
    public const uint MaxChapterProgress = 0x7CC73E81;
    public const uint InventoryItemComponent = 0xC3318103;
    public const uint NameTag = 0x9555A6FC;
    public const uint MoneyComponent = 0xE21AB7A0;
    public const uint Money = 0x941C7FA7;
    public const uint RandomlyGeneratedItem = 0x9121DCA1;
    public const uint RandomTableEntry = 0x951C5AA5;

    public static Dictionary<uint, EntityContentsInfo> Extract(
        GdbMulti multi, IReadOnlyList<KeyValuePair<uint, string>> saveMap)
    {
        var result = new Dictionary<uint, EntityContentsInfo>();

        foreach (KeyValuePair<uint, string> saved in saveMap)
        {
            if (!multi.TryLookup(saved.Key, out GdbCursor record))
            {
                continue;
            }

            var info = new EntityContentsInfo { EntityName = saved.Value };

            if (multi.TryFindInherited(
                    record, ChestComponent, 6, out _, out uint chestHash, out _) &&
                IsRef(chestHash))
            {
                info.HasChestComponent = true;
                if (multi.TryLookup(chestHash, out GdbCursor chest) &&
                    multi.TryFindInherited(
                        chest, SilverKeysNeeded, -1, out _, out uint keys,
                        out GdbFieldType keysType) &&
                    IsIntType(keysType))
                {
                    info.SilverKeysNeeded = (int)keys;
                }
            }

            if (multi.TryFindInherited(
                    record, DiggingSpotComponent, 6, out _, out uint dig, out _) &&
                IsRef(dig))
            {
                info.IsDigSpot = true;
            }

            if (multi.TryFindInherited(
                    record, DiveSpotComponent, 6, out _, out uint dive, out _) &&
                IsRef(dive))
            {
                info.IsDiveSpot = true;
            }

            if (multi.TryFindInherited(
                    record, DogLeadToComponent, 6, out _, out uint dog, out _) &&
                IsRef(dog))
            {
                info.DogCanLeadTo = true;
            }

            uint inventoryHash = 0;
            if (!multi.TryFindInherited(
                    record, InventoryComponentA, 6, out _, out inventoryHash, out _))
            {
                multi.TryFindInherited(
                    record, InventoryComponentB, 6, out _, out inventoryHash, out _);
            }

            if (IsRef(inventoryHash) && multi.TryLookup(inventoryHash, out GdbCursor inventory))
            {
                info.HasInventoryComponent = true;

                if (multi.TryFindInherited(
                        inventory, InitialItems, -1, out _, out uint itemsHash,
                        out GdbFieldType itemsType) &&
                    itemsType is GdbFieldType.Reference or GdbFieldType.ResourceReference &&
                    IsRef(itemsHash))
                {
                    ReadItemsList(multi, itemsHash, info.InitialItems);
                }

                if (multi.TryFindInherited(
                        inventory, ItemRepopulationData, 6, out _, out uint repopHash, out _) &&
                    IsRef(repopHash) && multi.TryLookup(repopHash, out GdbCursor repop))
                {
                    ReadBool(multi, repop, CanBeStolenFrom, v => info.CanBeStolenFrom = v);
                    ReadBool(multi, repop, CanRespawnItemsOnce, v => info.CanRespawnItemsOnce = v);
                    ReadBool(multi, repop, CanRespawnRepeatedly,
                        v => info.CanRespawnItemsRepeatedly = v);
                    if (TryReadFloat(multi, repop, ChanceOfRespawning, out float chance))
                    {
                        info.ChanceOfRespawning = chance;
                    }

                    if (multi.TryFindInherited(
                            repop, PotentialItems, -1, out _, out uint potHash,
                            out GdbFieldType potType) &&
                        potType is GdbFieldType.Reference or GdbFieldType.ResourceReference &&
                        IsRef(potHash))
                    {
                        ReadLootTable(multi, potHash, info.PotentialItems);
                    }
                }
            }

            if (info.HasInventoryComponent || info.HasChestComponent ||
                info.IsDigSpot || info.IsDiveSpot ||
                info.InitialItems.Count != 0 || info.PotentialItems.Count != 0)
            {
                result[saved.Key] = info;
            }
        }

        return result;
    }

    public static void ReadItemInfo(GdbMulti multi, uint itemHash, ContentsItem item) =>
        ReadItemInfo(multi, itemHash, item, true);

    public static void ReadItemInfo(
        GdbMulti multi, uint itemHash, ContentsItem item, bool expandRandom)
    {
        item.RecordHash = itemHash;
        if (!multi.TryLookup(itemHash, out GdbCursor record))
        {
            return;
        }

        if (multi.TryFindInherited(
                record, InventoryItemComponent, 6, out _, out uint compHash, out _) &&
            IsRef(compHash) && multi.TryLookup(compHash, out GdbCursor component) &&
            multi.TryFindInherited(
                component, NameTag, -1, out _, out uint tagHash, out GdbFieldType tagType) &&
            IsTagType(tagType) && IsRef(tagHash))
        {
            item.NameTag = multi.NameOf(tagHash);
            item.NameTagHash = tagHash;
        }

        if (multi.TryFindInherited(
                record, MoneyComponent, 6, out _, out uint moneyCompHash, out _) &&
            IsRef(moneyCompHash) && multi.TryLookup(moneyCompHash, out GdbCursor moneyComp) &&
            multi.TryFindInherited(
                moneyComp, Money, -1, out _, out uint money, out GdbFieldType moneyType) &&
            IsIntType(moneyType))
        {
            item.Money = (int)money;
        }

        if (item.NameTag.Length == 0 &&
            multi.TryFindInherited(
                record, RandomlyGeneratedItem, 6, out _, out uint randomHash, out _) &&
            IsRef(randomHash) && multi.TryLookup(randomHash, out GdbCursor random) &&
            multi.TryFindInherited(
                random, RandomTableEntry, -1, out _, out uint tableHash,
                out GdbFieldType tableType) &&
            IsTagType(tableType) && IsRef(tableHash))
        {
            string tableName = multi.NameOf(tableHash);
            if (tableName.Length != 0)
            {
                item.NameTag = $"Random ({tableName})";
            }

            if (expandRandom)
            {
                ExpandRandomTable(multi, tableHash, item.RandomChoices);
            }
        }

        if (item.NameTag.Length == 0)
        {
            item.NameTag = multi.NameOf(itemHash);
        }
    }

    public const string RandomItemLookupTables = "RandomItemLookupTables";

    public static void ExpandRandomTable(
        GdbMulti multi, uint tableHash, List<ContentsItem> choices)
    {
        if (!multi.TryLookup(tableHash, out GdbCursor table) &&
            !TryResolveLookupTable(multi, tableHash, out table))
        {
            return;
        }

        foreach (GdbField field in table.View.Fields(table.Record))
        {
            if (choices.Count >= 64)
            {
                break;
            }

            if (field.Hash == GdbView.HashParent || !IsListRefType(field.Type) ||
                !IsRef(field.Raw) ||
                !multi.TryLookup(field.Raw, out GdbCursor entry))
            {
                continue;
            }

            uint itemRef = field.Raw;
            if (multi.TryFindInherited(
                    entry, ItemReference, -1, out _, out uint wrapped,
                    out GdbFieldType wrappedType) &&
                IsTagType(wrappedType) && IsRef(wrapped))
            {
                itemRef = wrapped;
            }

            var choice = new ContentsItem
            {
                EntryRecordHash = field.Raw,
                EntryLabel = multi.NameOf(field.Hash),
            };
            ReadItemInfo(multi, itemRef, choice, false);
            if (TryReadFloat(multi, entry, LootWeight, out float weight))
            {
                choice.Weight = weight;
            }

            choices.Add(choice);
        }
    }

    private static bool TryResolveLookupTable(
        GdbMulti multi, uint tableNameHash, out GdbCursor table)
    {
        table = default;

        foreach (GdbView view in multi.Views)
        {
            for (int i = 0; i < view.RecordCount; i++)
            {
                if (!view.RecordName(i).Equals(
                        RandomItemLookupTables, System.StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (GdbField field in view.Fields(i))
                {
                    if (field.Hash == tableNameHash && IsRef(field.Raw) &&
                        multi.TryLookup(field.Raw, out table))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static void ReadItemsList(GdbMulti multi, uint listHash, List<ContentsItem> items)
    {
        if (!multi.TryLookup(listHash, out GdbCursor list))
        {
            return;
        }

        foreach (GdbField field in list.View.Fields(list.Record))
        {
            if (items.Count >= 64)
            {
                break;
            }

            if (field.Hash == GdbView.HashParent || !IsListRefType(field.Type) ||
                !IsRef(field.Raw))
            {
                continue;
            }

            var item = new ContentsItem
            {
                EntryLabel = multi.NameOf(field.Hash),
            };
            ReadItemInfo(multi, field.Raw, item);
            items.Add(item);
        }
    }

    private static void ReadLootTable(GdbMulti multi, uint tableHash, List<ContentsItem> items)
    {
        if (!multi.TryLookup(tableHash, out GdbCursor table))
        {
            return;
        }

        foreach (GdbField field in table.View.Fields(table.Record))
        {
            if (items.Count >= 96)
            {
                break;
            }

            if (field.Hash == GdbView.HashParent ||
                field.Type != GdbFieldType.Reference ||
                !IsRef(field.Raw) ||
                !multi.TryLookup(field.Raw, out GdbCursor entry))
            {
                continue;
            }

            if (!multi.TryFindInherited(
                    entry, ItemReference, -1, out _, out uint itemRef,
                    out GdbFieldType refType) ||
                !IsTagType(refType) || !IsRef(itemRef))
            {
                continue;
            }

            var item = new ContentsItem
            {
                EntryRecordHash = field.Raw,
                EntryLabel = multi.NameOf(field.Hash),
            };
            ReadItemInfo(multi, itemRef, item);
            if (TryReadFloat(multi, entry, LootWeight, out float weight))
            {
                item.Weight = weight;
            }

            if (TryReadFloat(multi, entry, MinFurnitureRating, out float minFurniture))
            {
                item.MinFurnitureRating = minFurniture;
            }

            if (TryReadFloat(multi, entry, MaxFurnitureRating, out float maxFurniture))
            {
                item.MaxFurnitureRating = maxFurniture;
            }

            if (TryReadFloat(multi, entry, MinChapterProgress, out float minChapter))
            {
                item.MinChapter = minChapter;
            }

            if (TryReadFloat(multi, entry, MaxChapterProgress, out float maxChapter))
            {
                item.MaxChapter = maxChapter;
            }

            items.Add(item);
        }
    }

    private static bool TryReadFloat(
        GdbMulti multi, GdbCursor cursor, uint fieldHash, out float value)
    {
        value = 0f;
        if (!multi.TryFindInherited(
                cursor, fieldHash, (int)GdbFieldType.Float, out _, out uint raw, out _))
        {
            return false;
        }

        value = System.BitConverter.Int32BitsToSingle(unchecked((int)raw));
        return float.IsFinite(value);
    }

    private static void ReadBool(
        GdbMulti multi, GdbCursor cursor, uint fieldHash, System.Action<int> assign)
    {
        if (multi.TryFindInherited(
                cursor, fieldHash, -1, out _, out uint raw, out GdbFieldType type) &&
            type == GdbFieldType.Bool)
        {
            assign(raw != 0 ? 1 : 0);
        }
    }

    private static bool IsRef(uint hash) => hash != 0 && hash != GdbHash.Null;

    private static bool IsIntType(GdbFieldType type) =>
        type is GdbFieldType.Int or GdbFieldType.Enum;

    private static bool IsTagType(GdbFieldType type) =>
        type is GdbFieldType.String or GdbFieldType.ResourceReference;

    private static bool IsListRefType(GdbFieldType type) =>
        type is GdbFieldType.String or GdbFieldType.Reference
            or GdbFieldType.ResourceReference;
}
