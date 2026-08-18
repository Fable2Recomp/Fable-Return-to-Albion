using System;
using System.Collections.Generic;
using System.Globalization;
using Refii.Entities;
using Refii.Gdb;

namespace Refii.Equipment;

public sealed class ItemDetail
{
    public uint RecordHash { get; set; }

    public string InternalName { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public uint NameTag { get; set; }

    public uint DescTag { get; set; }

    public string NameTagName { get; set; } = string.Empty;

    public string DescTagName { get; set; } = string.Empty;

    public uint ModelPathHash { get; set; }

    public string ModelPath { get; set; } = string.Empty;

    public uint WornModelPathHash { get; set; }

    public string WornModelPath { get; set; } = string.Empty;

    public uint FemaleWornModelPathHash { get; set; }

    public string FemaleWornModelPath { get; set; } = string.Empty;

    public uint BodyAreasCovered { get; set; }

    public int ClusterSortLayer { get; set; } = -100;

    public List<string> ClustersCovered { get; } = new();

    public List<string> FemaleClustersCovered { get; } = new();

    public string IconTex { get; set; } = string.Empty;

    public int Category { get; set; } = -1;

    public int WeaponType { get; set; }

    public uint WeaponComponent { get; set; }

    public int Money { get; set; } = -1;

    public bool Unnamed { get; set; }

    public bool IsMoney { get; set; }

    public List<KeyValuePair<string, string>> Stats { get; } = new();
}

public static class Engine_ItemDetails
{
    public const uint NameTag = 0x9555A6FC;
    public const uint DescTag = 0xD823B12B;
    public const uint IconGraphic = 0x46F0F9CE;
    public const uint InventoryItemComponent = 0xC3318103;
    public const uint MoneyComponent = 0xE21AB7A0;
    public const uint Category = 0xFE6FDF41;
    public const uint WeaponComponent = 0x2C34431E;
    public const uint WeaponType = 0xC7596B9D;
    public const uint AppearanceModifierComponent = 0xD949A6F5;
    public const uint GraphicAppearanceStaticMeshComponent = 0x29CF50D1;
    public const uint ModelFile = 0x0C17DB4E;
    public const uint OverrideFemaleModel = 0xD71D2B14;
    public const uint ClustersCovered = 0x194C174A;
    public const uint BodyAreasCovered = 0xB3ADD081;
    public const uint OverrideFemaleClustersCovered = 0xF736AC4A;
    public const uint ClusterSortLayer = 0x88FD2918;
    public const uint Augmentable = 0x9DC93C5B;

    public const string InventoryItemTagPrefix = "INV_ITEM_";

    public const int StatLimit = 40;

    public const int StatDepth = 24;

    public const int WalkDepth = 64;

    private static readonly uint[] AugmentSlots =
    {
        0xFCF3EB8E, 0xFCF3EB8D, 0xFCF3EB8C, 0xFCF3EB8B,
        0xFCF3EB8A, 0xFCF3EB89, 0xFCF3EB88, 0xFCF3EB87,
        0xFCF3EB86,
    };

    private static readonly uint[] StatComponents =
    {
        0x2C34431E,
        0x0A644D42,
        0xE21AB7A0,
        0x6D04D9A2,
    };

    private static readonly string[] NonStatFields =
    {
        "GUIScreen", "Icon", "parent", "Component", "Tag",
        "Offset", "Rotation", "Translation", "Mesh", "Model",
        "Texture", "ScriptName", "SoundEvent", "Expression",
    };

    public static List<ItemDetail> Build(GdbMulti multi)
    {
        var details = new List<ItemDetail>();
        var seen = new HashSet<uint>();

        foreach (GdbView view in multi.Views)
        {
            for (int index = 0; index < view.RecordCount; index++)
            {
                if (!IsItemRecord(multi, view, index))
                {
                    continue;
                }

                uint recordHash = view.RecordHash(index);
                if (!seen.Add(recordHash))
                {
                    continue;
                }

                details.Add(Read(multi, new GdbCursor(view, index), recordHash));
            }
        }

        details.Sort(Compare);
        return details;
    }

    public static ItemDetail? BuildOne(GdbMulti multi, uint recordHash) =>
        multi.TryLookup(recordHash, out GdbCursor cursor)
            ? Read(multi, cursor, recordHash)
            : null;

    private static bool IsItemRecord(GdbMulti multi, GdbView view, int index)
    {
        if (view.TryFindLocalRaw(index, InventoryItemComponent, 6, out _, out _))
        {
            return true;
        }

        return view.TryFindLocalRaw(index, NameTag, -1, out uint tag, out GdbFieldType type) &&
            IsTagType(type) &&
            multi.NameOf(tag).StartsWith(InventoryItemTagPrefix, StringComparison.Ordinal);
    }

    private static ItemDetail Read(GdbMulti multi, GdbCursor cursor, uint recordHash)
    {
        var detail = new ItemDetail
        {
            RecordHash = recordHash,
            InternalName = multi.NameOf(recordHash),
        };

        var info = new ContentsItem();
        Engine_EntityContents.ReadItemInfo(multi, recordHash, info, false);
        detail.Money = info.Money;

        if (multi.TryFindInherited(cursor, MoneyComponent, 6, out _, out uint money, out _) &&
            IsRef(money))
        {
            detail.IsMoney = true;
        }

        ReadAppearance(multi, cursor, detail);

        GdbCursor inventoryComponent = default;
        bool hasInventory =
            multi.TryFindInherited(
                cursor, InventoryItemComponent, 6, out _, out uint inventoryHash, out _) &&
            IsRef(inventoryHash) &&
            multi.TryLookup(inventoryHash, out inventoryComponent);

        if (hasInventory &&
            multi.TryFindInherited(
                inventoryComponent, Category, -1, out _, out uint category,
                out GdbFieldType categoryType) &&
            IsIntType(categoryType))
        {
            detail.Category = unchecked((int)category);
        }

        if (multi.TryFindInherited(
                cursor, WeaponComponent, 6, out _, out uint weaponComponent, out _) &&
            IsRef(weaponComponent))
        {
            detail.WeaponComponent = weaponComponent;

            if (multi.TryLookup(weaponComponent, out GdbCursor weapon) &&
                multi.TryFindInherited(
                    weapon, WeaponType, -1, out _, out uint weaponType,
                    out GdbFieldType weaponTypeType) &&
                IsIntType(weaponTypeType))
            {
                detail.WeaponType = unchecked((int)weaponType);
            }
        }

        detail.NameTag = FindTag(multi, cursor, inventoryComponent, hasInventory, NameTag);
        detail.DescTag = FindTag(multi, cursor, inventoryComponent, hasInventory, DescTag);
        detail.NameTagName = multi.NameOf(detail.NameTag);
        detail.DescTagName = multi.NameOf(detail.DescTag);

        uint icon = FindTag(multi, cursor, inventoryComponent, hasInventory, IconGraphic);
        if (icon != 0)
        {
            detail.IconTex = multi.NameOf(icon);
        }

        ReadModel(multi, cursor, detail);

        if (detail.ModelPath.Length == 0)
        {
            detail.ModelPath = ModelPathFromTag(detail.NameTagName);
        }

        string raw = detail.NameTagName;
        if (raw.Length == 0)
        {
            raw = multi.NameOf(recordHash);
        }

        if (raw.Length == 0)
        {
            raw = info.NameTag;
        }

        if (raw.Length != 0)
        {
            detail.Label = Engine_ItemLabels.Prettify(raw);
        }
        else
        {
            detail.Label = Engine_ItemLabels.Unnamed(recordHash);
            detail.Unnamed = true;
        }

        var statSeen = new HashSet<uint>();
        CollectScalars(multi, cursor, detail, statSeen);

        foreach (uint componentField in StatComponents)
        {
            if (multi.TryFindInherited(
                    cursor, componentField, 6, out _, out uint component, out _) &&
                IsRef(component) && multi.TryLookup(component, out GdbCursor scoped))
            {
                CollectScalars(multi, scoped, detail, statSeen);
            }
        }

        CollectAugments(multi, cursor, detail);
        return detail;
    }

    private static void ReadAppearance(GdbMulti multi, GdbCursor cursor, ItemDetail detail)
    {
        if (multi.TryFindInherited(
                cursor, GraphicAppearanceStaticMeshComponent, 6, out _,
                out uint meshComponent, out _) &&
            IsRef(meshComponent) && multi.TryLookup(meshComponent, out GdbCursor mesh))
        {
            if (TryReadModelField(multi, mesh, ModelFile, out uint worn, out string wornPath))
            {
                detail.WornModelPathHash = worn;
                detail.WornModelPath = wornPath;
            }
        }

        if (!multi.TryFindInherited(
                cursor, AppearanceModifierComponent, 6, out _,
                out uint modifierComponent, out _) ||
            !IsRef(modifierComponent) ||
            !multi.TryLookup(modifierComponent, out GdbCursor modifier))
        {
            return;
        }

        if (TryReadModelField(
                multi, modifier, OverrideFemaleModel, out uint female, out string femalePath))
        {
            detail.FemaleWornModelPathHash = female;
            detail.FemaleWornModelPath = femalePath;
        }

        if (multi.TryFindInherited(
                modifier, ClusterSortLayer, -1, out _, out uint layer,
                out GdbFieldType layerType) &&
            IsIntType(layerType))
        {
            detail.ClusterSortLayer = unchecked((int)layer);
        }

        if (multi.TryFindInherited(
                modifier, BodyAreasCovered, 6, out _, out uint areas, out _) && IsRef(areas))
        {
            detail.BodyAreasCovered = CollectBitmask(multi, areas);
        }

        if (multi.TryFindInherited(
                modifier, ClustersCovered, 6, out _, out uint clusters, out _) &&
            IsRef(clusters))
        {
            detail.ClustersCovered.AddRange(CollectNameList(multi, clusters));
        }

        if (multi.TryFindInherited(
                modifier, OverrideFemaleClustersCovered, 6, out _,
                out uint femaleClusters, out _) && IsRef(femaleClusters))
        {
            detail.FemaleClustersCovered.AddRange(CollectNameList(multi, femaleClusters));
        }
    }

    private static bool TryReadModelField(
        GdbMulti multi, GdbCursor component, uint field, out uint hash, out string path)
    {
        hash = 0;
        path = string.Empty;

        if (!multi.TryFindInherited(
                component, field, -1, out _, out uint value, out GdbFieldType type) ||
            !IsTagType(type) || !IsRef(value))
        {
            return false;
        }

        hash = value;
        path = multi.NameOf(value);
        return true;
    }

    private static void ReadModel(GdbMulti multi, GdbCursor cursor, ItemDetail detail)
    {
        List<uint> models = Engine_EntityModels.ForEntity(
            multi, cursor.View.RecordHash(cursor.Record));

        if (models.Count == 0)
        {
            return;
        }

        detail.ModelPathHash = models[0];
        detail.ModelPath = multi.NameOf(models[0]);
    }

    public const string WeaponTagPrefix = "INV_ITEM_WEAPON_";

    private static string ModelPathFromTag(string tag)
    {
        if (!tag.StartsWith(WeaponTagPrefix, StringComparison.Ordinal))
        {
            return string.Empty;
        }

        string core = tag[WeaponTagPrefix.Length..];
        int name = core.LastIndexOf(Engine_ItemLabels.NameSuffix, StringComparison.Ordinal);
        if (name >= 0)
        {
            core = core[..name];
        }

        core = core.Replace("BASE", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        return core.IndexOf('_') >= 0 ? core + ".mdl" : string.Empty;
    }

    private static uint FindTag(
        GdbMulti multi, GdbCursor cursor, GdbCursor inventory, bool hasInventory, uint field)
    {
        if (multi.TryFindInherited(
                cursor, field, -1, out _, out uint value, out GdbFieldType type) &&
            IsTagType(type) && value != GdbHash.Null)
        {
            return value;
        }

        if (hasInventory &&
            multi.TryFindInherited(
                inventory, field, -1, out _, out value, out type) &&
            IsTagType(type) && value != GdbHash.Null)
        {
            return value;
        }

        return 0;
    }

    private static List<string> CollectNameList(GdbMulti multi, uint listHash)
    {
        var names = new List<string>();
        if (!multi.TryLookup(listHash, out GdbCursor walk))
        {
            return names;
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
                if (type != GdbFieldType.Enum || !IsRef(raw))
                {
                    continue;
                }

                string name = multi.NameOf(raw);
                if (name.Length != 0)
                {
                    names.Add(name);
                }
            }

            foreach (uint hash in local)
            {
                inherited.Add(hash);
            }

            if (!TryStepToParent(multi, ref walk, visited))
            {
                break;
            }
        }

        return names;
    }

    private static uint CollectBitmask(GdbMulti multi, uint listHash)
    {
        uint mask = 0;
        if (!multi.TryLookup(listHash, out GdbCursor walk))
        {
            return mask;
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
                if (type == GdbFieldType.Enum)
                {
                    mask |= raw;
                }
            }

            foreach (uint hash in local)
            {
                inherited.Add(hash);
            }

            if (!TryStepToParent(multi, ref walk, visited))
            {
                break;
            }
        }

        return mask;
    }

    private static void CollectScalars(
        GdbMulti multi, GdbCursor start, ItemDetail detail, HashSet<uint> statSeen)
    {
        GdbCursor walk = start;
        var visited = new HashSet<uint>();

        for (int depth = 0; depth < StatDepth && detail.Stats.Count < StatLimit; depth++)
        {
            foreach ((uint hash, GdbFieldType type, uint raw) in
                     walk.View.RawFields(walk.Record))
            {
                if (detail.Stats.Count >= StatLimit)
                {
                    break;
                }

                if (hash == GdbView.HashParent || !statSeen.Add(hash) || !IsScalarType(type))
                {
                    continue;
                }

                string name = multi.NameOf(hash);
                if (name.Length == 0 || !IsStatField(name))
                {
                    continue;
                }

                detail.Stats.Add(
                    new KeyValuePair<string, string>(name, DescribeScalar(type, raw)));
            }

            if (!TryStepToParent(multi, ref walk, visited))
            {
                break;
            }
        }
    }

    private static void CollectAugments(GdbMulti multi, GdbCursor cursor, ItemDetail detail)
    {
        if (!multi.TryFindInherited(
                cursor, Augmentable, 6, out _, out uint augmentComponent, out _) ||
            !IsRef(augmentComponent) ||
            !multi.TryLookup(augmentComponent, out GdbCursor augments))
        {
            return;
        }

        int slot = 0;
        foreach (uint field in AugmentSlots)
        {
            if (!multi.TryFindInherited(
                    augments, field, -1, out _, out uint augment, out _) || !IsRef(augment))
            {
                continue;
            }

            string name = multi.NameOf(augment);
            if (name.Length == 0 && multi.TryLookup(augment, out GdbCursor record) &&
                multi.TryFindInherited(
                    record, NameTag, -1, out _, out uint tag, out GdbFieldType tagType) &&
                IsTagType(tagType))
            {
                name = multi.NameOf(tag);
            }

            if (name.Length == 0)
            {
                continue;
            }

            slot++;
            detail.Stats.Add(new KeyValuePair<string, string>(
                $"Augment {slot}", Engine_ItemLabels.Prettify(name)));
        }
    }

    private static bool TryStepToParent(GdbMulti multi, ref GdbCursor walk, HashSet<uint> visited)
    {
        if (!walk.View.TryFindLocalRaw(
                walk.Record, GdbView.HashParent, 6, out uint parent, out _) ||
            !IsRef(parent) || !visited.Add(parent) ||
            !multi.TryLookup(parent, out GdbCursor next))
        {
            return false;
        }

        walk = next;
        return true;
    }

    private static string DescribeScalar(GdbFieldType type, uint raw) => type switch
    {
        GdbFieldType.Float => DescribeFloat(BitConverter.Int32BitsToSingle(unchecked((int)raw))),
        GdbFieldType.Bool => raw != 0 ? "yes" : "no",
        _ => raw.ToString(CultureInfo.InvariantCulture),
    };

    private static string DescribeFloat(float value)
    {
        if (!float.IsFinite(value))
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        return value == MathF.Truncate(value) && MathF.Abs(value) < 1e9f
            ? ((long)value).ToString(CultureInfo.InvariantCulture)
            : value.ToString("0.#####", CultureInfo.InvariantCulture);
    }

    private static bool IsStatField(string name)
    {
        foreach (string skip in NonStatFields)
        {
            if (name.Contains(skip, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static int Compare(ItemDetail a, ItemDetail b)
    {
        if (a.Unnamed != b.Unnamed)
        {
            return a.Unnamed ? 1 : -1;
        }

        return Engine_ItemLabels.CompareLabels(a.Label, b.Label);
    }

    private static bool IsRef(uint hash) => hash != 0 && hash != GdbHash.Null;

    private static bool IsIntType(GdbFieldType type) =>
        type is GdbFieldType.Int or GdbFieldType.Enum;

    private static bool IsTagType(GdbFieldType type) =>
        type is GdbFieldType.String or GdbFieldType.ResourceReference;

    private static bool IsScalarType(GdbFieldType type) =>
        type is GdbFieldType.Bool or GdbFieldType.Int
            or GdbFieldType.Float or GdbFieldType.Enum;
}
