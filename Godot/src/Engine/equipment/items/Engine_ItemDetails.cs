using System;
using System.Collections.Generic;
using System.Globalization;
using Refii.Entities;
using Refii.Gdb;
using Godot;

namespace Refii.Equipment;

public sealed class ItemDetail
{
    public uint RecordHash { get; set; }

    /// <summary>
    /// The DEFINITION that owns this item — the record carrying `WeaponComponent`,
    /// `ShopItemComponent`, `AugmentableComponent` and the `InventoryItemComponent` reference.
    /// Equal to <see cref="RecordHash"/> when the row already resolved to it; otherwise the
    /// record found by <c>InventoryItemComponent == RecordHash</c> (or an ancestor), because a
    /// row that resolves by NAME TAG lands on the inventory COMPONENT, which knows its name,
    /// description, icon and rating and nothing about damage, slots or price.
    /// </summary>
    public uint DefinitionHash { get; set; }

    /// <summary>
    /// The INVENTORY COMPONENT that carries the item's name tag, description tag, rating, and icon.
    /// When an item is authored in Fable 2's GDB, the world definition points to this component via
    /// <c>InventoryItemComponent</c>. Stored here so unified entries can be resolved by either the
    /// definition hash or component hash.
    /// </summary>
    public uint InventoryComponentHash { get; set; }

    /// <summary>`ShopItemComponent.BasePrice` on the definition, −1 when unpriced — the number
    /// the item page writes as `"$" + itoa(BasePrice)` (`sub_829B6B50`). <see cref="Money"/> is
    /// the gold-bag amount, a different thing.</summary>
    public int BasePrice { get; set; } = -1;

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

    /// <summary>The clusters covered while the garment is worn OPEN (`OverrideOpenClustersCovered`);
    /// the guest reads it only when `BodyAreasCovered` carries 0x800 and the wearer's open byte is set.</summary>
    public List<string> OpenClustersCovered { get; } = new();

    /// <summary>`OverrideFemaleOpenClustersCovered`: the open list for a female wearer.</summary>
    public List<string> FemaleOpenClustersCovered { get; } = new();

    public string IconTex { get; set; } = string.Empty;

    /// <summary>
    /// The MENU PREVIEW — the 3D object the item book's right page spins inside its ring.
    ///
    /// `GUIScreenMesh` is a model RESOURCE HASH used as it stands, not a name: it resolves to a
    /// string in none of the seven shipped databases (0 of 399), exactly like `ModelFile`
    /// (0 of 1234), which is how every model reference in this data is stored. So the raw value
    /// is the currency `Engine_PropModelIndex` keys on.
    ///
    /// The two offsets are REFERENCES to a record carrying `X`/`Y`/`Z` floats, and those
    /// components INHERIT — one shipped record states only `Z` and takes X and Y from its
    /// parent — so each is read through the inheritance walk rather than locally.
    /// </summary>
    public uint PreviewMesh { get; set; }

    public Vector3 PreviewTranslation { get; set; }

    public Vector3 PreviewRotation { get; set; }

    /// <summary>`GUIScreenScaleMultiplier`; 0 when the artist stated none.</summary>
    public float PreviewScale { get; set; }

    /// <summary>`GUIScreenRotationAxis`, stored as an enum; 0 when unstated.</summary>
    public int PreviewRotationAxis { get; set; }

    public int Category { get; set; } = -1;

    public int WeaponType { get; set; }

    public uint WeaponComponent { get; set; }

    public int Money { get; set; } = -1;

    public bool Unnamed { get; set; }

    public bool IsMoney { get; set; }

    public bool HasInventoryItem { get; set; }

    /// <summary>Star rating (1..5) from `InventoryItemComponent.Rating`.</summary>
    public int Rating { get; set; }

    /// <summary>Total augment slots from `AugmentableComponent.NumberOfSlots`.</summary>
    public int AugmentSlots { get; set; }

    /// <summary>Names of slotted augments and empty slots.</summary>
    public List<string> Augments { get; } = new();

    /// <summary>Damage multiplier on WeaponComponent, 1.0f when absent.</summary>
    public float DamageMultiplier { get; set; } = 1.0f;

    /// <summary>Attack speed multiplier on WeaponComponent, 1.0f when absent.</summary>
    public float AttackSpeedMultiplier { get; set; } = 1.0f;

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
    public const uint OverrideOpenClustersCovered = 0x621729A6;
    public const uint OverrideFemaleOpenClustersCovered = 0xD4D501F6;
    public const uint ClusterSortLayer = 0x88FD2918;
    public const uint Augmentable = 0x9DC93C5B;

    // The menu preview — see ItemDetail.PreviewMesh.
    public const uint GUIScreenMesh = 0x0EE6E0A5;
    public const uint GUIScreenTranslationOffset = 0x304AE802;
    public const uint GUIScreenRotationOffset = 0x6C8C3F5D;
    public const uint GUIScreenScaleMultiplier = 0xA1AB8D29;
    public const uint GUIScreenRotationAxis = 0xD8A21E7F;
    public const uint VectorX = 0x050C5D47;
    public const uint VectorY = 0x050C5D46;
    public const uint VectorZ = 0x050C5D45;

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

    public static readonly uint ShopItemComponentField =
        Refii.Core.Engine_GameHash.Fnv1("ShopItemComponent");

    public static readonly uint[] StatComponents =
    {
        WeaponComponent,               // 0x2C34431E
        InventoryItemComponent,        // 0xC3318103
        ShopItemComponentField,        // Fnv1("ShopItemComponent")
        Augmentable,                   // 0x9DC93C5B
        AppearanceModifierComponent,   // 0xD949A6F5
        0x0A644D42,
        0xE21AB7A0,                    // MoneyComponent
        0x6D04D9A2,
    };

    private static readonly string[] NonStatFields =
    {
        "GUIScreen", "Icon", "parent", "Component", "Tag",
        "Offset", "Rotation", "Translation", "Mesh", "Model",
        "Texture", "ScriptName", "SoundEvent", "Expression",
        "BasePrice",
    };

    public static List<ItemDetail> Build(GdbMulti multi)
    {
        var rawDetails = new List<ItemDetail>();
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

                rawDetails.Add(Read(multi, new GdbCursor(view, index), recordHash));
            }
        }

        // CANONICAL ITEM DEDUPLICATION:
        // In Fable 2's GDB database, every world item (weapon, garment, or prop) is authored across
        // two mutually-referencing records:
        //   1. The Definition Record (e.g. Weapon_Crossbow_Base, 0x48414876):
        //      Carries world simulation and combat components (WeaponComponent, ShopItemComponent,
        //      augment slots, model reference) and an InventoryItemComponent reference pointing to the
        //      inventory component.
        //   2. The Inventory Component Record (e.g. INV_ITEM_CROSSBOW_BASE, 0xDDEFA3B7):
        //      Carries the localized text tags (NameTag "Base Crossbow", DescTag), GUI rating, and
        //      icon graphic, while its parent/TryDefinitionOf points back to the definition.
        //
        // Because both records pass IsItemRecord(), iterating raw database rows yields two entries
        // that resolve to the exact same display name and exact same 3D mesh. Here, we merge each pair
        // into a single authoritative ItemDetail:
        //   - Keyed by (DefinitionHash, NameTag) so legitimately distinct items never collide.
        //   - Promotes the Definition row as primary (RecordHash == DefinitionHash) so entity spawning,
        //     physics, and combat components are anchored to the world definition.
        //   - Ensures InventoryComponentHash is retained on the canonical item so gameplay, scripts, and
        //     UI books can resolve the item by either the definition hash or the inventory component hash.
        var merged = new Dictionary<(uint CanonicalDef, uint NameTag), ItemDetail>();
        foreach (ItemDetail detail in rawDetails)
        {
            uint defKey = detail.DefinitionHash != 0 ? detail.DefinitionHash : detail.RecordHash;
            var key = (defKey, detail.NameTag);

            if (!merged.TryGetValue(key, out ItemDetail? existing))
            {
                merged[key] = detail;
                continue;
            }

            // Merge stats/attributes between definition and component rows:
            if (existing.Rating != 0 && detail.Rating == 0) detail.Rating = existing.Rating;
            if (detail.Rating != 0 && existing.Rating == 0) existing.Rating = detail.Rating;

            if (existing.BasePrice >= 0 && detail.BasePrice < 0) detail.BasePrice = existing.BasePrice;
            if (detail.BasePrice >= 0 && existing.BasePrice < 0) existing.BasePrice = detail.BasePrice;

            if (existing.AugmentSlots != 0 && detail.AugmentSlots == 0)
            {
                detail.AugmentSlots = existing.AugmentSlots;
                if (detail.Augments.Count == 0) detail.Augments.AddRange(existing.Augments);
            }
            if (detail.AugmentSlots != 0 && existing.AugmentSlots == 0)
            {
                existing.AugmentSlots = detail.AugmentSlots;
                if (existing.Augments.Count == 0) existing.Augments.AddRange(detail.Augments);
            }

            if (string.IsNullOrEmpty(detail.IconTex) && !string.IsNullOrEmpty(existing.IconTex))
            {
                detail.IconTex = existing.IconTex;
            }
            else if (string.IsNullOrEmpty(existing.IconTex) && !string.IsNullOrEmpty(detail.IconTex))
            {
                existing.IconTex = detail.IconTex;
            }

            // If the incoming detail is the definition row, promote it over an earlier component row:
            if (detail.RecordHash == detail.DefinitionHash)
            {
                if (detail.InventoryComponentHash == 0)
                {
                    detail.InventoryComponentHash = existing.RecordHash;
                }
                merged[key] = detail;
            }
            else if (existing.InventoryComponentHash == 0)
            {
                existing.InventoryComponentHash = detail.RecordHash;
            }
        }

        var details = new List<ItemDetail>(merged.Values);
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

        // TWO RECORDS MAKE AN ITEM. The definition (`Weapon_LongswordSteel`-style) carries the
        // components — `WeaponComponent`, `ShopItemComponent`, `AugmentableComponent` — and an
        // `InventoryItemComponent` reference to the record that carries the name tag, the
        // description tag, the icon and the rating. A DLC variant is a child of that component
        // (its own `DescriptionTag`, `parent` → the base component) with a definition of its
        // own pointing at it. The item books resolve a row by NAME TAG, which lands on the
        // COMPONENT; every read that only walked `parent` from there found no damage, no slots
        // and no price ("value unstated" on every weapon). So: find the definition that OWNS the
        // component and read the components off it, keeping the row's own record for the tags.
        GdbCursor owner = cursor;
        uint ownerHash = recordHash;
        bool cursorHasInventory =
            multi.TryFindInherited(cursor, InventoryItemComponent, 6, out _, out uint ownRef, out _) &&
            IsRef(ownRef);
        if (!cursorHasInventory && TryDefinitionOf(multi, cursor, recordHash, out GdbCursor definition))
        {
            owner = definition;
            ownerHash = definition.View.RecordHash(definition.Record);
        }

        detail.DefinitionHash = ownerHash;

        var info = new ContentsItem();
        Engine_EntityContents.ReadItemInfo(multi, ownerHash, info, false);
        detail.Money = info.Money;

        if (multi.TryFindInherited(owner, MoneyComponent, 6, out _, out uint money, out _) &&
            IsRef(money))
        {
            detail.IsMoney = true;
        }

        ReadAppearance(multi, owner, detail);

        GdbCursor inventoryComponent = default;
        bool hasInventory =
            multi.TryFindInherited(
                owner, InventoryItemComponent, 6, out _, out uint inventoryHash, out _) &&
            IsRef(inventoryHash) &&
            multi.TryLookup(inventoryHash, out inventoryComponent);

        // The row IS the component (a DLC variant carries the description override the
        // definition's reference would miss): the tags come from it first.
        if (!cursorHasInventory)
        {
            inventoryComponent = cursor;
            hasInventory = true;
            inventoryHash = recordHash;
        }

        detail.HasInventoryItem = hasInventory;
        detail.InventoryComponentHash = hasInventory && IsRef(inventoryHash) ? inventoryHash : 0;

        if (multi.TryFindInherited(
                owner, ShopItemComponentField, 6, out _, out uint shop, out _) &&
            IsRef(shop) && multi.TryLookup(shop, out GdbCursor shopComponent) &&
            multi.TryFindInherited(
                shopComponent, BasePriceField, -1, out _, out uint price,
                out GdbFieldType priceType) &&
            IsIntType(priceType))
        {
            detail.BasePrice = unchecked((int)price);
        }

        if (hasInventory &&
            multi.TryFindInherited(
                inventoryComponent, Category, -1, out _, out uint category,
                out GdbFieldType categoryType) &&
            IsIntType(categoryType))
        {
            detail.Category = unchecked((int)category);
        }

        if (hasInventory &&
            multi.TryFindInherited(
                inventoryComponent, RatingField, -1, out _, out uint rating,
                out GdbFieldType ratingType) &&
            IsIntType(ratingType))
        {
            detail.Rating = unchecked((int)rating);
        }
        else if (multi.TryFindInherited(
                     owner, RatingField, -1, out _, out uint ownerRating,
                     out GdbFieldType ownerRatingType) &&
                 IsIntType(ownerRatingType))
        {
            detail.Rating = unchecked((int)ownerRating);
        }

        if (multi.TryFindInherited(
                owner, WeaponComponent, 6, out _, out uint weaponComponent, out _) &&
            IsRef(weaponComponent))
        {
            detail.WeaponComponent = weaponComponent;

            if (multi.TryLookup(weaponComponent, out GdbCursor weapon))
            {
                if (multi.TryFindInherited(
                    weapon, WeaponType, -1, out _, out uint weaponType,
                    out GdbFieldType weaponTypeType) &&
                    IsIntType(weaponTypeType))
                {
                    detail.WeaponType = unchecked((int)weaponType);
                }

                if (multi.TryFindInherited(
                    weapon, DamageMultiplierField, -1, out _, out uint rawDmg,
                    out GdbFieldType dmgType) && dmgType == GdbFieldType.Float)
                {
                    detail.DamageMultiplier = BitConverter.Int32BitsToSingle(unchecked((int)rawDmg));
                }

                if (multi.TryFindInherited(
                    weapon, AttackSpeedMultiplierField, -1, out _, out uint rawSpd,
                    out GdbFieldType spdType) && spdType == GdbFieldType.Float)
                {
                    detail.AttackSpeedMultiplier = BitConverter.Int32BitsToSingle(unchecked((int)rawSpd));
                }
            }
        }

        if (detail.DamageMultiplier == 1.0f &&
            multi.TryFindInherited(owner, DamageMultiplierField, -1, out _, out uint rawDefDmg, out GdbFieldType defDmgType) &&
            defDmgType == GdbFieldType.Float)
        {
            detail.DamageMultiplier = BitConverter.Int32BitsToSingle(unchecked((int)rawDefDmg));
        }

        if (detail.AttackSpeedMultiplier == 1.0f &&
            multi.TryFindInherited(owner, AttackSpeedMultiplierField, -1, out _, out uint rawDefSpd, out GdbFieldType defSpdType) &&
            defSpdType == GdbFieldType.Float)
        {
            detail.AttackSpeedMultiplier = BitConverter.Int32BitsToSingle(unchecked((int)rawDefSpd));
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

        ReadPreview(multi, cursor, inventoryComponent, hasInventory, detail);

        ReadModel(multi, owner, detail);

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

        if (Engine_RecordNameCatalog.TryGetCuratedNodeName(recordHash, out string curatedLabel) ||
            (ownerHash != 0 && Engine_RecordNameCatalog.TryGetCuratedNodeName(ownerHash, out curatedLabel)))
        {
            detail.Label = curatedLabel;
        }
        else if (raw.Length != 0)
        {
            string prettified = Engine_ItemLabels.Prettify(raw);
            detail.Label = Engine_RecordNameCatalog.IsGenericPlaceholderName(prettified) && detail.InternalName.Length != 0
                ? Engine_ItemLabels.Prettify(detail.InternalName)
                : prettified;
        }
        else
        {
            detail.Label = Engine_ItemLabels.Unnamed(recordHash);
            detail.Unnamed = true;
        }

        var statSeen = new HashSet<uint>();
        CollectScalars(multi, owner, detail, statSeen);

        foreach (uint componentField in StatComponents)
        {
            if (multi.TryFindInherited(
                    owner, componentField, 6, out _, out uint component, out _) &&
                IsRef(component) && multi.TryLookup(component, out GdbCursor scoped))
            {
                CollectScalars(multi, scoped, detail, statSeen);
            }
        }

        CollectAugments(multi, owner, detail);
        return detail;
    }



    private static readonly uint BasePriceField = Refii.Core.Engine_GameHash.Fnv1("BasePrice");

    private static readonly uint RatingField = Refii.Core.Engine_GameHash.Fnv1("Rating");

    private static readonly uint NumberOfSlotsField = Refii.Core.Engine_GameHash.Fnv1("NumberOfSlots");

    private static readonly uint InitialAugmentationsField = Refii.Core.Engine_GameHash.Fnv1("InitialAugmentations");

    private static readonly uint PrimaryTypeField = Refii.Core.Engine_GameHash.Fnv1("PrimaryType");

    private static readonly uint SecondaryTypeField = Refii.Core.Engine_GameHash.Fnv1("SecondaryType");

    private static readonly uint DamageMultiplierField = Refii.Core.Engine_GameHash.Fnv1("DamageMultiplier");

    private static readonly uint AttackSpeedMultiplierField = Refii.Core.Engine_GameHash.Fnv1("AttackSpeedMultiplier");

    private static Dictionary<uint, uint>? _definitionByComponent;

    private static GdbMulti? _definitionIndexOf;

    /// <summary>
    /// The definition whose `InventoryItemComponent` or attached component names this component, or an ancestor of it
    /// (a DLC variant's own definition names the variant; the base definition names the base).
    /// One reverse index per database, built on first ask.
    /// </summary>
    public static bool TryDefinitionOf(
        GdbMulti multi, GdbCursor component, uint componentHash, out GdbCursor definition)
    {
        definition = default;
        if (!ReferenceEquals(_definitionIndexOf, multi) || _definitionByComponent is null)
        {
            var map = new Dictionary<uint, uint>();
            foreach (GdbView view in multi.Views)
            {
                for (int i = 0; i < view.RecordCount; ++i)
                {
                    uint recHash = view.RecordHash(i);
                    if (view.TryFindLocalRaw(i, InventoryItemComponent, 6, out uint target, out _) &&
                        IsRef(target))
                    {
                        map.TryAdd(target, recHash);
                    }

                    foreach (uint compField in StatComponents)
                    {
                        if (view.TryFindLocalRaw(i, compField, 6, out uint compTarget, out _) &&
                            IsRef(compTarget))
                        {
                            map.TryAdd(compTarget, recHash);
                        }
                    }
                }
            }

            _definitionByComponent = map;
            _definitionIndexOf = multi;
        }

        GdbCursor current = component;
        uint hash = componentHash;
        var visited = new HashSet<uint>();
        for (int depth = 0; depth < WalkDepth && hash != 0; ++depth)
        {
            if (_definitionByComponent.TryGetValue(hash, out uint owner) &&
                multi.TryLookup(owner, out definition))
            {
                return true;
            }

            if (!current.Valid ||
                !current.View.TryFindLocalRaw(
                    current.Record, GdbView.HashParent, 6, out uint parent, out _) ||
                !IsRef(parent) || !visited.Add(parent) || !multi.TryLookup(parent, out current))
            {
                return false;
            }

            hash = parent;
        }

        return false;
    }

    /// <summary>
    /// Finds the owning item definition for a given component hash (or prototype).
    /// </summary>
    public static bool TryDefinitionOf(
        GdbMulti multi, uint componentHash, out GdbCursor definition)
    {
        definition = default;
        if (componentHash == 0 || componentHash == GdbHash.Null)
        {
            return false;
        }

        multi.TryLookup(componentHash, out GdbCursor cursor);
        return TryDefinitionOf(multi, cursor, componentHash, out definition);
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

        ReadModifierLists(multi, modifier, detail);
    }

    /// <summary>
    /// The detail of ONE record by hash: an item record (its appearance-modifier component is
    /// followed) or the modifier record itself, which is what the guest's worn-item handles
    /// name (`sub_824C13A8` reads `ClustersCovered` straight off the entry). Null when the
    /// hash is in no database.
    /// </summary>
    public static ItemDetail? DetailForRecord(GdbMulti multi, uint hash)
    {
        if (!multi.TryLookup(hash, out GdbCursor cursor))
        {
            return null;
        }

        var detail = new ItemDetail { RecordHash = hash };
        ReadAppearance(multi, cursor, detail);
        if (detail.ClustersCovered.Count == 0 && detail.FemaleClustersCovered.Count == 0 &&
            detail.BodyAreasCovered == 0)
        {
            ReadModifierLists(multi, cursor, detail);
        }

        return detail;
    }

    /// <summary>The cluster and body-area lists on an appearance-modifier record.</summary>
    private static void ReadModifierLists(GdbMulti multi, GdbCursor modifier, ItemDetail detail)
    {
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

        if (multi.TryFindInherited(
                modifier, OverrideOpenClustersCovered, 6, out _,
                out uint openClusters, out _) && IsRef(openClusters))
        {
            detail.OpenClustersCovered.AddRange(CollectNameList(multi, openClusters));
        }

        if (multi.TryFindInherited(
                modifier, OverrideFemaleOpenClustersCovered, 6, out _,
                out uint femaleOpenClusters, out _) && IsRef(femaleOpenClusters))
        {
            detail.FemaleOpenClustersCovered.AddRange(CollectNameList(multi, femaleOpenClusters));
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

    /// <summary>The item book's spinning preview — see <see cref="ItemDetail.PreviewMesh"/>.</summary>
    private static void ReadPreview(
        GdbMulti multi, GdbCursor cursor, GdbCursor inventory, bool hasInventory, ItemDetail detail)
    {
        detail.PreviewMesh = FindTag(multi, cursor, inventory, hasInventory, GUIScreenMesh);

        if (FindScalar(multi, cursor, inventory, hasInventory, GUIScreenScaleMultiplier,
                out uint scale, out GdbFieldType scaleType) &&
            scaleType == GdbFieldType.Float)
        {
            detail.PreviewScale = BitConverter.Int32BitsToSingle(unchecked((int)scale));
        }

        if (FindScalar(multi, cursor, inventory, hasInventory, GUIScreenRotationAxis,
                out uint axis, out GdbFieldType axisType) &&
            IsIntType(axisType))
        {
            detail.PreviewRotationAxis = unchecked((int)axis);
        }

        detail.PreviewTranslation =
            ReadVector(multi, cursor, inventory, hasInventory, GUIScreenTranslationOffset);
        detail.PreviewRotation =
            ReadVector(multi, cursor, inventory, hasInventory, GUIScreenRotationOffset);
    }

    /// <summary>A referenced `X`/`Y`/`Z` record, each component INHERITED — see PreviewMesh.</summary>
    private static Vector3 ReadVector(
        GdbMulti multi, GdbCursor cursor, GdbCursor inventory, bool hasInventory, uint field)
    {
        if (!FindScalar(multi, cursor, inventory, hasInventory, field, out uint reference,
                out GdbFieldType type) ||
            type != GdbFieldType.Reference || !IsRef(reference) ||
            !multi.TryLookup(reference, out GdbCursor vector))
        {
            return Vector3.Zero;
        }

        return new Vector3(
            Component(multi, vector, VectorX),
            Component(multi, vector, VectorY),
            Component(multi, vector, VectorZ));

        static float Component(GdbMulti multi, GdbCursor vector, uint axis) =>
            multi.TryFindInherited(vector, axis, (int)GdbFieldType.Float, out _, out uint raw,
                out _)
                ? BitConverter.Int32BitsToSingle(unchecked((int)raw))
                : 0f;
    }

    /// <summary>`FindTag` without the tag-type constraint: the entity first, then its component.</summary>
    private static bool FindScalar(
        GdbMulti multi, GdbCursor cursor, GdbCursor inventory, bool hasInventory, uint field,
        out uint value, out GdbFieldType type)
    {
        if (multi.TryFindInherited(cursor, field, -1, out _, out value, out type))
        {
            return true;
        }

        return hasInventory &&
            multi.TryFindInherited(inventory, field, -1, out _, out value, out type);
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
            !multi.TryLookup(augmentComponent, out GdbCursor augmentable))
        {
            return;
        }

        int slots = 0;
        if (multi.TryFindInherited(
                augmentable, NumberOfSlotsField, -1, out _, out uint rawSlots,
                out GdbFieldType slotsType) &&
            IsIntType(slotsType))
        {
            slots = unchecked((int)rawSlots);
        }

        var seeded = new List<(uint Primary, uint Secondary)>();
        if (multi.TryFindInherited(
                augmentable, InitialAugmentationsField, 6, out _, out uint initRef, out _) &&
            IsRef(initRef) && multi.TryLookup(initRef, out GdbCursor list))
        {
            for (int i = 1; i <= 8; ++i)
            {
                uint augField = Refii.Core.Engine_GameHash.Fnv1(
                    "Augmentation" + i.ToString(CultureInfo.InvariantCulture));
                if (!multi.TryFindInherited(
                        list, augField, 6, out _, out uint entryRef, out _) ||
                    !IsRef(entryRef) || !multi.TryLookup(entryRef, out GdbCursor entry))
                {
                    break;
                }

                uint primary = 0;
                if (multi.TryFindInherited(entry, PrimaryTypeField, -1, out _, out uint p, out _) &&
                    IsRef(p))
                {
                    primary = p;
                }

                if (primary != 0)
                {
                    uint secondary = 0;
                    if (multi.TryFindInherited(entry, SecondaryTypeField, -1, out _, out uint s, out _) &&
                        IsRef(s))
                    {
                        secondary = s;
                    }

                    seeded.Add((primary, secondary));
                }
            }
        }

        if (slots == 0 && seeded.Count > 0)
        {
            slots = seeded.Count;
        }

        detail.AugmentSlots = slots;

        for (int slot = 0; slot < slots && slot < 8; ++slot)
        {
            string augmentName = slot < seeded.Count
                ? ResolveAugmentName(multi, seeded[slot])
                : "Empty Slot";

            detail.Augments.Add(augmentName);
            detail.Stats.Add(new KeyValuePair<string, string>(
                $"Augment {slot + 1}", augmentName));
        }
    }

    private static string ResolveAugmentName(GdbMulti multi, (uint Primary, uint Secondary) pair)
    {
        string pTag = AugmentTagPart(multi, pair.Primary);
        string sTag = AugmentTagPart(multi, pair.Secondary);
        string tagName = "INV_ITEM_AUGMENTATION_" + pTag + sTag + "NAME";
        uint tagHash = Refii.Core.Engine_GameHash.Fnv1(tagName);
        if (Engine_BnkTextBankLoader.TryLookup(tagHash, out string localised) && localised.Length != 0)
        {
            return localised;
        }

        uint textTagHash = Refii.Core.Engine_GameHash.Fnv1("TEXT_" + tagName);
        if (Engine_BnkTextBankLoader.TryLookup(textTagHash, out string textLocalised) &&
            textLocalised.Length != 0)
        {
            return textLocalised;
        }

        if (pTag.Length != 0)
        {
            return Engine_ItemLabels.Prettify(pTag.TrimEnd('_'));
        }

        return tagName;
    }

    private static string AugmentTagPart(GdbMulti multi, uint type)
    {
        if (type == 0 || !multi.TryLookup(type, out GdbCursor record) ||
            !multi.TryFindInherited(
                record, NameTag, -1, out _, out uint tag, out _))
        {
            return string.Empty;
        }

        string text = multi.NameOf(tag);
        return text.Length != 0 ? text + "_" : string.Empty;
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

    public static bool IsStatField(string name)
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
