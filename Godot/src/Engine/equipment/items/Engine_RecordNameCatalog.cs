using System;
using System.Collections.Generic;
using Refii.Core;
using Refii.Gdb;

namespace Refii.Equipment;

/// <summary>
/// Schema definition for a known field inside a GDB component record.
/// </summary>
public sealed record ComponentFieldSchema(
    string FieldName,
    uint FieldHash,
    GdbFieldType FieldType,
    string DefaultValue,
    string Description = "");

/// <summary>
/// Metadata and default schema for a GDB component slot that can be attached to or inherited by a GDB node.
/// </summary>
public sealed record ComponentSlotSchema(
    string SlotName,
    uint SlotHash,
    string DisplayName,
    string Category,
    string Summary,
    IReadOnlyList<ComponentFieldSchema> DefaultFields);

/// <summary>
/// Shared catalog of curated human-readable names for anonymous/generic GDB prototype records
/// and schema definitions for GDB entity components across all domains (Items, Weapons, Clothing,
/// Creatures &amp; NPCs, Buildings &amp; Shops, Props, Loot Tables, Visual FX, and World Markers).
/// Used across both the Modding Studio and the Game:// FileSystem so high-level nodes never
/// display as "Gui Needs Text" or "Baseitem".
/// </summary>
public static class Engine_RecordNameCatalog
{
    public const uint WorldEntityRootHash = 0x46F79080;
    public const uint InteractiveWorldObjectBaseHash = 0x6D8C4798;
    public const uint InventoryItemBaseHash = 0xC9397216;
    public const uint CreaturesAndNpcsBaseHash = 0xCD69A0A8;
    public const uint BuildingsAndShopsBaseHash = 0xBC1E195A;
    public const uint InteractivePropsBaseHash = 0x586E4A9F;
    public const uint VisualEffectsAndSoundBaseHash = 0x9A779DBF;
    public const uint WorldMarkersAndTriggersBaseHash = 0xFEBBC06F;
    public const uint LootTablesAndGeneratorsBaseHash = 0x01136AFB;
    public const uint FurnitureBaseHash = 0xDBA16BCA;

    /// <summary>
    /// FNV-1a hash of the direct <c>RemoveComponent</c> field (<c>0x9B41D00A</c>, <see cref="GdbFieldType.String"/>)
    /// placed on a node record to strip an inherited component slot by its slot hash.
    /// </summary>
    public const uint RemoveComponentFieldHash = 0x9B41D00A;

    private static readonly Dictionary<uint, string> CuratedNodeNames = new()
    {
        // Root & Top-Level World Entity Branches under 0x46F79080
        [0x46F79080] = "World Entity Root",
        [0x6D8C4798] = "Interactive World Object Base",
        [0xC9397216] = "Inventory Item Base",
        [0x9A779DBF] = "Visual Effects & Sound Emitters Base",
        [0xCD69A0A8] = "Creatures & NPCs Base",
        [0xFEBBC06F] = "World Markers & Triggers Base",
        [0xBC1E195A] = "Buildings, Real Estate & Shops Base",
        [0x01136AFB] = "Loot Tables & Random Item Generators Base",
        [0xDBA16BCA] = "Furniture Base",
        [0x24DECFA3] = "Physics Props Base",
        [0x9369F99B] = "Projectiles Base",
        [0x411E1C6C] = "Bullets & Ammunition Base",
        [0xFD53DE2C] = "Hero & Henchman Templates Base",

        // Sub-branches under 0x6D8C4798 (Interactive World Object Base)
        [0x586E4A9F] = "Interactive Props, Chests & Doors Base",
        [0x1FB227B8] = "Mounted & Prop Weapons Base",
        [0x8886BB47] = "Display & Rack Weapons Base",
        [0x82DF620F] = "Breakable Crates, Barrels & Props Base",
        [0x51D81FC9] = "Smashable Barrels & Urns Base",
        [0x4E41B015] = "Smashable Crates Base",
        [0xB7DDF00F] = "Doors & Gates Base",
        [0x1ABD60CB] = "Treasure Chests & Containers Base",
        [0x9A404576] = "Ornate & Silver Chests Base",
        [0xE88BFC6C] = "Switches, Levers & Triggers Base",
        [0xBD085003] = "Digging & Dive Spots Base",
        [0xEF426B2A] = "Quest Seals & Music Boxes Base",
        [0x619A933D] = "Interactive Cage Keys Base",
        [0xD3D5A49A] = "Money Bags Base",
        [0x5B4AF8AB] = "Gold Bags Base",
        [0x1E521B6C] = "Expression Props Root",
        [0xD5EB7C7D] = "Lute Expression Base",

        // Creatures & NPCs Sub-branches under 0xCD69A0A8
        [0xDA0554AB] = "Human & Villager Creatures Base",
        [0x73495263] = "Villagers, Guards & Bandits Base",
        [0x0A3F3369] = "Hammer (Hero Companion) Base",
        [0x6AD2197A] = "Garth (Hero Companion) Base",
        [0x6167BB94] = "Hero Children & Spouses Base",
        [0x9600C530] = "Reaver (Hero Companion) Base",
        [0x6F0D9384] = "Lucien Base",
        [0x1FC8A6E5] = "Monsters & Enemy Creatures Base",
        [0xFD08ADEC] = "Hobbes Base",
        [0x4A0AD8BE] = "Hollow Men Base",
        [0x88CCE036] = "Beetles Base",
        [0x4659E7C4] = "Balverines Base",
        [0x5AAACE13] = "Banshees Base",
        [0x08A1AB45] = "Trolls & Companions Base",
        [0x2BBA9A4A] = "Trolls Base",
        [0x65D9C904] = "Companion Dogs Base",
        [0xE619E6F7] = "Lucien's Soldiers & Spire Guards Base",
        [0x433FE28C] = "Commandants & Necromancers Base",
        [0xFDF0DC5B] = "Spire Shards Base",
        [0xF8B3B2F7] = "Ambient Wildlife Base",
        [0x0EA98958] = "Fauna (Hares & Chickens) Base",

        // Buildings, Real Estate & Shops Sub-branches under 0xBC1E195A
        [0x62B18D20] = "Residential Houses & Estates Base",
        [0xA7018389] = "Farmhouses & Country Estates Base",
        [0xB3E0B615] = "Shops, Stalls & Taverns Base",
        [0x04EFBF42] = "Market Stalls Base",
        [0x6B2D6DC7] = "Town Shops Base",
        [0x056C85A0] = "Public & Landmark Buildings Base",

        // World Markers & Triggers Sub-branches under 0xFEBBC06F
        [0x4D531370] = "Navigation & Spawn Markers Base",
        [0x352B830C] = "Creature & Encounter Spawn Markers Base",
        [0xA6D6EB75] = "Quest & Camera Markers Base",
        [0xC8DACCBA] = "Travel & Waypoint Markers Base",
        [0xEC8EFEAE] = "Player Start Markers Base",
        [0x7B1E0D2A] = "AI & Patrol Markers Base",
        [0x5DC70AF2] = "Village & Region Managers Base",
        [0xF6053A43] = "Villages Base",
        [0x5F7AB8D5] = "Region Controllers Base",

        // Furniture Branch (0xDBA16BCA)
        [0x2AD87D30] = "Double Beds Base",
        [0xF5DBA7D0] = "Desks Base",
        [0x0D3615FE] = "Cupboards Base",
        [0x60345E48] = "Drawers Base",
        [0x22BE1C7E] = "Dining Tables Base",
        [0x71868BD6] = "Comfy Chairs Base",
        [0x784F3B23] = "Bedside Tables Base",
        [0x6A34AC1D] = "Child Beds Base",
        [0xB94B041C] = "Bookcases Base",
        [0x0E50C75F] = "Closets Base",
        [0x65904395] = "Dressers Base",
        [0x6109D38A] = "Stoves Base",
        [0x234FAFB9] = "Sinks Base",

        // Quest & Story Items Branch (0x1A75622B)
        [0x1A75622B] = "Quest & Story Items Base",
        [0x8B27D1C5] = "Letters, Clues & Notes Base",
        [0x6825343B] = "Diaries & Journals Base",
        [0x7E2754CB] = "Archaeology Clues Base",
        [0x64F62772] = "Letters & Warrants Base",
        [0x569F1E23] = "Cave Diary Pages Base",
        [0x50D0223D] = "Ancient Scrolls & Artifacts Base",
        [0x2DE849E7] = "Highlighted Quest Objects Base",
        [0x7EDC8403] = "Quest Collectibles Base",
        [0x1DEDA9C8] = "Knothole Island Books Base",
        [0x50D225D8] = "Murgo Silver Statues Base",
        [0x638A2822] = "Tarot & Fate Cards Base",
        [0xC58FEAFF] = "Contracts & Quest Letters Base",
        [0xEB5F27C1] = "Wanted Warrants Base",
        [0x2D558D9E] = "Quest Keys Base",
        [0x65DD554A] = "Quest Bottles & Seals Base",
        [0x104BF787] = "Sketch Fragments Base",
        [0x16104551] = "Treasure & Gargoyle Maps Base",
        [0xBADD0765] = "Plans & Maps Base",

        // Augments Branch (0x522858DA)
        [0x522858DA] = "Augments Base",
        [0x3A86E0D5] = "Special Augments Base",

        // Accessories & Clothing Branch (0x9870E661, 0x8D7E8E6E)
        [0x9870E661] = "Accessories & Jewellery Base",
        [0x6B9539DA] = "Masks & Costume Accessories Base",
        [0x156A224D] = "Earrings Base",
        [0xB3411F7C] = "Necklaces Base",
        [0x4742CA0B] = "Rings Base",
        [0x8D7E8E6E] = "Clothing & Armor Base",
        [0xB7DC68E3] = "Hats & Headwear Base",
        [0x9195462D] = "Full Outfits & Suits Base",
        [0x4DC80022] = "Coats & Robes Base",
        [0xCCA6AD68] = "Trousers Base",
        [0x9F7A416C] = "Boots & Footwear Base",
        [0x456A6C4F] = "Armored & Guard Boots Base",
        [0xE85892B0] = "Shirts & Vests Base",
        [0x788732A4] = "Gloves & Gauntlets Base",
        [0xF5C2BF8A] = "Skirts & Dress Trousers Base",

        // Books & Documents Branch (0x1D129184)
        [0x1D129184] = "Books & Documents Base",
        [0x376F7130] = "Instructional Books Base",
        [0x9237E9F3] = "Dog Trick Books Base",
        [0xAFA5B81F] = "Lore & History Books Base",
        [0xAED59850] = "See the Future Lore Books Base",
        [0xC41092DD] = "Story & Sequel Books Base",
        [0x4442B886] = "Biographies Base",
        [0x5C9936EF] = "Quest Journals & Diaries Base",
        [0x793D96CD] = "Rare & Adventure Books Base",
        [0x4DA766BA] = "Dog Treasure Hunting Books Base",
        [0x04FCDC8D] = "Dog Combat Books Base",
        [0x362EDFB3] = "Spire & Knothole Books Base",
        [0xC4A987FB] = "Lucien's Diary Base",

        // Trophies & Hero Titles (0x0E36DB93, 0x01C781CD)
        [0x0E36DB93] = "Trophies & Temple Keys Base",
        [0x01C781CD] = "Hero Titles Base",

        // Food & Drink Branch (0x313949F6)
        [0x313949F6] = "Food & Drink Base",
        [0xBDE14A90] = "Apple Pies Base",
        [0x853AE3BE] = "Meat Pies Base",
        [0x3B954315] = "Blueberry Pies Base",
        [0x3281DC99] = "Cheese Base",
        [0xF1E5F275] = "Peanuts Base",
        [0x5B30356C] = "Apples Base",
        [0xEF7E187B] = "Alcoholic Drinks Base",
        [0x86308D81] = "Beers Base",
        [0xEAD730DD] = "Spirits Base",
        [0xC2924265] = "Wines Base",

        // Potions Branch (0x48D0E450)
        [0x48D0E450] = "Potions Base",
        [0xB6F895D9] = "Knothole Morph Potions Base",
        [0x3F550790] = "Weight Gain Potions Base",
        [0xCDCFA1DB] = "Weight Loss Potions Base",
        [0x11948403] = "Height Increase Potions Base",
        [0x63115007] = "Shrinking Potions Base",
        [0xF32ABA4B] = "Muscle Increase Potions Base",
        [0xB6C986CF] = "Muscle Reduction Potions Base",
        [0xC7F19697] = "Scar Vanishing Potions Base",
        [0x62E35DE4] = "Will Increase Potions Base",
        [0xC9CE62A1] = "Will Reduction Potions Base",
        [0xE7C5DDC3] = "Experience Potions Base",
        [0xA28D3CB7] = "Skill Experience Potions Base",
        [0xC4D199FB] = "Strength Experience Potions Base",
        [0xBB302DB3] = "Will Experience Potions Base",
        [0xC374E8EF] = "General Experience Potions Base",
        [0x03015E44] = "Dog Breed Potions Base",

        // Gifts Branch (0x55D61852)
        [0x55D61852] = "Gifts Base",
        [0xAE51EF6F] = "Toy Gifts Base",
        [0x95DCE10E] = "Hero Doll Gifts Base",
        [0x4E91AFFF] = "Gems Base",
        [0xB6F49D5B] = "Rubies Base",
        [0x3068FEA3] = "Diamonds Base",
        [0x65244513] = "Jet Gems Base",
        [0x992288EB] = "Chocolates Base",
        [0x69829543] = "Gift Rings Base",
        [0x941D987B] = "Gift Jewellery Base",
        [0x10555DC3] = "Flowers Base",
        [0x3F8B24A7] = "Perfumes Base",

        // Makeup, Dyes, Hair, Tattoos (0x79B00718, 0x580679E0, 0x4433A650, 0x5DDE51EC)
        [0x79B00718] = "Makeup Base",
        [0x4D5E0247] = "Special Makeup Base",
        [0x580679E0] = "Dyes Base",
        [0x3B92849F] = "Clothing & Hair Dyes Base",
        [0x25CAA249] = "See the Future Dyes Base",
        [0x8D638D17] = "Rare Dyes Base",
        [0x4433A650] = "Hair & Facial Hair Base",
        [0xFE0987CB] = "Haircuts Base",
        [0x8462C0C3] = "Beards Base",
        [0xDD5562FF] = "Moustaches Base",
        [0xA7574A76] = "Special Hair Base",
        [0x5DDE51EC] = "Tattoos Base",
        [0xA9DE9EFC] = "Body Tattoos Base",
        [0xB84D9721] = "Face Tattoos Base",

        // Weapons & Crossbows (0x815D2C8C, 0xFF710592)
        [0x815D2C8C] = "Weapons Base",
        [0x3B44A554] = "NPC Carried Weapons Base",
        [0x75EAFACE] = "NPC Carried Weapon (Shadow_R_Heel)",
        [0xBCEE2FE6] = "Heavy Melee Weapons Base",
        [0x84B52EF9] = "Hammers Base",
        [0x174772EE] = "Unique Hammers Base",
        [0x8B93BE8C] = "Maces Base",
        [0x79A766C2] = "Unique Maces Base",
        [0x0C888863] = "Iron Mace (Socketed Variant)",
        [0xBC5F736D] = "Axes Base",
        [0xEF821E98] = "Unique Axes Base",
        [0x4B6A3899] = "Light Melee Weapons Base",
        [0xE7328E75] = "Longswords Base",
        [0xAFA972FB] = "Iron Longsword (Augmented Variant Base)",
        [0x2FA3A8C9] = "Master Longsword (Augmented Variant Base)",
        [0x113CC573] = "Rusty Longsword (Augmented Variant Base)",
        [0xD3AF5826] = "Steel Longsword (Augmented Variant Base)",
        [0xF2F80A98] = "Cutlasses Base",
        [0x309C5599] = "Championship Cutlass Base",
        [0x69D208F8] = "Cleavers Base",
        [0x47245B15] = "Katanas Base",
        [0xE6A146B2] = "Great Swords Base",
        [0xDA0ECA94] = "Firearms & Bows Base",
        [0x19EE7D56] = "Rifles & Blunderbusses Base",
        [0x6A8641CD] = "Blunderbusses Base",
        [0xFE8A47E2] = "Clockwork Rifles Base",
        [0x8C52C0C8] = "Flintlock Rifles Base",
        [0x819D0A13] = "Turret Rifles Base",
        [0x6EE3899C] = "Pistols & Bows Base",
        [0x39774913] = "Flintlock Pistols Base",
        [0x464255F6] = "Championship Pistol Base",
        [0xC36D5E17] = "Clockwork Pistols Base",
        [0x9A3A5E4F] = "Turret Pistols Base",
        [0x799D0A2A] = "Bows Base",
        [0x7C704E00] = "Longbows Base",
        [0xC5219527] = "Splintered Longbow",
        [0x93B13E26] = "Recurve Bows Base",
        [0x8B2AA3ED] = "Splintered Recurve Bow",
        [0x835C36C9] = "Shortbows Base",
        [0xA40C1378] = "Splintered Shortbow",
        [0xFF710592] = "Crossbows Base",
        [0x7C6F7696] = "Light Crossbows Base",
        [0x065CA857] = "Heavy Crossbows Base",
        [0xFF8D4D0C] = "Repeater Crossbows Base",
    };

    private static readonly HashSet<string> LocalizedTagFieldNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "NameTag",
        "DescriptionTag",
        "TextTag",
        "BuildingName",
        "BuildingDescription",
        "BuildingAnecdotes",
        "BuildingBenefits",
        "BuildingAddress",
        "JobTag",
        "WorldIconText",
        "MessageBoxTextTag",
        "OverrideMessageTextTag",
        "OverriddenTextTag",
        "SummerTextTag",
        "WinterTextTag",
        "NameGroupTag",
    };

    /// <summary>
    /// Returns true if the given component field stores a localized Babel text tag key hash
    /// (such as <c>NameTag</c>, <c>DescriptionTag</c>, <c>TextTag</c>, or <c>BuildingName</c>).
    /// </summary>
    public static bool IsLocalizedTextTagField(string? fieldName)
    {
        if (string.IsNullOrWhiteSpace(fieldName)) return false;
        return LocalizedTagFieldNames.Contains(fieldName.Trim());
    }

    /// <summary>
    /// Looks up a curated human-readable name for a known GDB prototype/base record hash.
    /// </summary>
    public static bool TryGetCuratedNodeName(uint recordHash, out string name)
    {
        if (recordHash != 0 && CuratedNodeNames.TryGetValue(recordHash, out string? found) && !string.IsNullOrEmpty(found))
        {
            name = found;
            return true;
        }

        name = string.Empty;
        return false;
    }

    /// <summary>
    /// Returns true if the given localized or prettified name is an unlocalized engine placeholder
    /// (such as "Gui Needs Text", "Baseitem", "Place text here...", or "unnamed 0x...").
    /// </summary>
    public static bool IsGenericPlaceholderName(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        string trimmed = text.Trim();
        if (trimmed.Equals(Engine_ItemNames.Placeholder, StringComparison.OrdinalIgnoreCase)) return true;
        if (trimmed.Equals("Gui Needs Text", StringComparison.OrdinalIgnoreCase)) return true;
        if (trimmed.Equals("GUI_NEEDS_TEXT", StringComparison.OrdinalIgnoreCase)) return true;
        if (trimmed.Equals("Baseitem", StringComparison.OrdinalIgnoreCase)) return true;
        if (trimmed.StartsWith("INV_ITEM_", StringComparison.OrdinalIgnoreCase)) return true;
        if (trimmed.StartsWith("TEXT_", StringComparison.OrdinalIgnoreCase)) return true;
        if (trimmed.StartsWith("GUI_", StringComparison.OrdinalIgnoreCase)) return true;
        if (trimmed.EndsWith("_NAME", StringComparison.OrdinalIgnoreCase) && trimmed.Contains('_')) return true;
        if (trimmed.EndsWith("_DESC", StringComparison.OrdinalIgnoreCase) && trimmed.Contains('_')) return true;
        if (trimmed.StartsWith("unnamed 0x", StringComparison.OrdinalIgnoreCase)) return true;
        if (trimmed.StartsWith("Item 0x", StringComparison.OrdinalIgnoreCase)) return true;
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && trimmed.Length == 10) return true;
        return false;
    }

    private static ComponentFieldSchema Field(string name, GdbFieldType type, string defaultVal, string desc = "") =>
        new(name, Engine_GameHash.Fnv1(name), type, defaultVal, desc);

    private static ComponentSlotSchema Slot(
        string slotName,
        string displayName,
        string category,
        string summary,
        params ComponentFieldSchema[] defaultFields) =>
        new(slotName, Engine_GameHash.Fnv1(slotName), displayName, category, summary, defaultFields);

    /// <summary>
    /// All attachable GDB component schemas across Items, Weapons, Clothing, Creatures &amp; NPCs,
    /// Buildings &amp; Shops, Interactive Props, Visual FX, and World Markers.
    /// </summary>
    public static IReadOnlyList<ComponentSlotSchema> AllComponentSchemas { get; } = new List<ComponentSlotSchema>
    {
        // =========================================================================
        // 1. Core Item & Economy Components
        // =========================================================================
        Slot(
            "InventoryItemComponent",
            "Inventory Item Component",
            "Core Item",
            "Defines localized Display Name (NameTag), Description (DescriptionTag), wheel icon, star rating, inventory category, and usability flags.",
            Field("NameTag", GdbFieldType.String, "", "Localized display name written to book.babel.csv."),
            Field("DescriptionTag", GdbFieldType.String, "", "Localized item description written to book.babel.csv."),
            Field("IconGraphic", GdbFieldType.String, "art\\gui\\wheelicons\\items.tex", "2D UI wheel icon texture path."),
            Field("Rating", GdbFieldType.Int, "1", "Star quality tier (1 to 5)."),
            Field("Category", GdbFieldType.Enum, "16", "Inventory tab category enum."),
            Field("GUIScreenScaleMultiplier", GdbFieldType.Float, "1.0", "Scale multiplier for 3D item preview."),
            Field("UsableFromInventory", GdbFieldType.Bool, "false", "Whether the player can use this item from the inventory."),
            Field("DestroyItemAfterUse", GdbFieldType.Bool, "false", "Whether using the item consumes it."),
            Field("VisibleInGUI", GdbFieldType.Bool, "true", "Whether the item is visible in inventory menus."),
            Field("IsPermanent", GdbFieldType.Bool, "false", "Prevents selling or discarding the item."),
            Field("CanHaveAugmentsRemoved", GdbFieldType.Bool, "true", "Allows removing socketed augments."),
            Field("DLCVersion", GdbFieldType.Int, "0", "DLC requirement flag (0 = base game).")),

        Slot(
            "ShopItemComponent",
            "Shop Item Component",
            "Core Item",
            "Defines base gold value (BasePrice) and merchant restock rate.",
            Field("BasePrice", GdbFieldType.Int, "100", "Base value in gold before regional/opinion modifiers."),
            Field("BaseSalesPerDay", GdbFieldType.Float, "0.25", "Daily restock rate for merchants."),
            Field("Infinite", GdbFieldType.Bool, "false", "Whether merchants have infinite stock of this item.")),

        // =========================================================================
        // 2. Appearance & Visual Components
        // =========================================================================
        Slot(
            "GraphicAppearanceStaticMeshComponent",
            "Static Mesh Appearance Component",
            "Appearance & Visuals",
            "Assigns the 3D static mesh (ModelFile) and shadow/reflection flags for weapons and world props.",
            Field("ModelFile", GdbFieldType.String, "", "Static 3D mesh asset path (.mdl)."),
            Field("ShadowCaster", GdbFieldType.Bool, "true", "Casts dynamic shadows."),
            Field("DontCastDistantShadows", GdbFieldType.Bool, "false", "Skips distant cascade shadow rendering."),
            Field("IncludeInReflection", GdbFieldType.Bool, "false", "Renders in planar water reflections."),
            Field("IncludeInRefraction", GdbFieldType.Bool, "false", "Renders in water refraction pass."),
            Field("UseDisplacement", GdbFieldType.Bool, "false", "Enables vertex displacement.")),

        Slot(
            "GraphicAppearanceAnimatedMeshComponent",
            "Animated Mesh Appearance Component",
            "Appearance & Visuals",
            "Assigns a rigged/skeletal 3D model (ModelFile) for crossbows, creatures, and articulated objects.",
            Field("ModelFile", GdbFieldType.String, "", "Animated skeletal 3D model path (.mdl)."),
            Field("SkeletonFile", GdbFieldType.String, "", "Skeleton hierarchy asset path (.skl)."),
            Field("ShadowCaster", GdbFieldType.Bool, "true", "Casts dynamic shadows."),
            Field("DontCastDistantShadows", GdbFieldType.Bool, "false", "Skips distant cascade shadow rendering."),
            Field("IncludeInReflection", GdbFieldType.Bool, "false", "Renders in planar water reflections."),
            Field("IncludeInRefraction", GdbFieldType.Bool, "false", "Renders in water refraction pass.")),

        Slot(
            "GraphicAppearanceComponent",
            "Graphic Appearance LOD Component",
            "Appearance & Visuals",
            "Controls rendering LOD distances, fade-in ranges, tinting, and alpha depth writes.",
            Field("Alpha", GdbFieldType.Float, "1.0", "Base opacity multiplier."),
            Field("LodFadeDistance", GdbFieldType.Float, "30.0", "Distance at which LOD transitions occur."),
            Field("BillboardDistance", GdbFieldType.Float, "70.0", "Distance at which billboard rendering activates."),
            Field("DecimationDistance", GdbFieldType.Float, "10000.0", "Mesh decimation distance."),
            Field("EnableLightZoneShadowCulling", GdbFieldType.Bool, "true", "Enables light-zone shadow culling.")),

        Slot(
            "GraphicAppearanceMorphComponent",
            "Character Morph Appearance Component",
            "Appearance & Visuals",
            "Configures skeletal and texture morphing for creatures, villagers, and heroes.",
            Field("CharacterFolder", GdbFieldType.String, "", "Character art folder path."),
            Field("BaseSkeletalMorphFilename", GdbFieldType.String, "", "Base skeletal morph asset filename.")),

        Slot(
            "AppearanceModifierComponent",
            "Appearance Modifier Component",
            "Appearance & Visuals",
            "Configures wearable clothing, armor, hair, beard, tattoo, and makeup slots, body coverage, and dye channels.",
            Field("ClusterSortLayer", GdbFieldType.Int, "0", "Sorting layer priority for garment clusters."),
            Field("HasSecondaryDyes", GdbFieldType.Bool, "false", "Supports a secondary dye channel.")),

        Slot(
            "DyeComponent",
            "Dye Component",
            "Appearance & Visuals",
            "Links a dye bottle item to its color palette definitions."),

        Slot(
            "GraphicAppearanceLightComponent",
            "Light Appearance Component",
            "Appearance & Visuals",
            "Attaches a dynamic point or spot light source to an entity or item.",
            Field("Range", GdbFieldType.Float, "6.0", "Light attenuation radius."),
            Field("Intensity", GdbFieldType.Float, "1.0", "Light brightness multiplier."),
            Field("CastShadows", GdbFieldType.Bool, "false", "Enables shadow casting from this light."),
            Field("InitialActive", GdbFieldType.Bool, "true", "Light starts active on spawn.")),

        // =========================================================================
        // 3. Combat & Weapons Components
        // =========================================================================
        Slot(
            "WeaponComponent",
            "Weapon Component",
            "Combat & Weapons",
            "Core combat stats for melee and ranged weapons (DamageMultiplier, AttackSpeedMultiplier, WeaponType).",
            Field("DamageMultiplier", GdbFieldType.Float, "10.0", "Base damage per hit."),
            Field("AttackSpeedMultiplier", GdbFieldType.Float, "1.0", "Attack speed multiplier."),
            Field("WeaponType", GdbFieldType.Enum, "1", "Weapon animation and grip class."),
            Field("IsRanged", GdbFieldType.Bool, "false", "True for ranged firearms/bows/crossbows."),
            Field("FlourishWait", GdbFieldType.Float, "0.6", "Charge/windup time for flourish attacks."),
            Field("BonusWait", GdbFieldType.Float, "0.2", "Combo timing window."),
            Field("Credibility", GdbFieldType.Float, "0.2", "NPC threat/credibility rating."),
            Field("DamageMultipliedBySpeed", GdbFieldType.Bool, "false", "Scales damage by swing velocity."),
            Field("HasInfiniteAmmo", GdbFieldType.Bool, "true", "Whether ranged weapon has unlimited ammo."),
            Field("InitialAmmo", GdbFieldType.Int, "20", "Starting ammo count if finite.")),

        Slot(
            "FirearmComponent",
            "Firearm Component",
            "Combat & Weapons",
            "Ranged ballistics, magazine capacity, reload time, spread cone, and aim ranges for pistols, rifles, crossbows, and bows.",
            Field("BulletCapacity", GdbFieldType.Int, "1", "Rounds held per reload."),
            Field("ShotsPerReload", GdbFieldType.Int, "1", "Rounds loaded per reload cycle."),
            Field("ReloadTime", GdbFieldType.Float, "1.25", "Seconds required to reload."),
            Field("PeriodBetweenShots", GdbFieldType.Float, "0.2", "Minimum delay between shots in multi-shot magazines."),
            Field("ProjectilesPerShot", GdbFieldType.Int, "1", "Pellets/projectiles fired per trigger pull (e.g. blunderbuss > 1)."),
            Field("SpreadConeAngle", GdbFieldType.Float, "2.0", "Projectile spread cone in degrees."),
            Field("Range", GdbFieldType.Float, "35.0", "Maximum effective projectile range."),
            Field("OptimumRange", GdbFieldType.Float, "12.0", "Ideal combat engagement range."),
            Field("NearRange", GdbFieldType.Float, "2.5", "Close-quarters threshold range."),
            Field("ZoomMaxFOV", GdbFieldType.Float, "50.0", "Field of view when zoomed in."),
            Field("Scoped", GdbFieldType.Bool, "false", "Enables scope overlay when aiming."),
            Field("CanLockAndSubTarget", GdbFieldType.Bool, "true", "Allows precision sub-targeting.")),

        Slot(
            "AugmentableComponent",
            "Augmentable Component",
            "Combat & Weapons",
            "Defines augment socket capacity (NumberOfSlots) on weapons/jewelry or marks an item as an augment stone.",
            Field("NumberOfSlots", GdbFieldType.Int, "0", "Number of augment slots on this weapon/item.")),

        Slot(
            "WeaponOpinionEffectsComponent",
            "Weapon Opinion Effects Component",
            "Combat & Weapons",
            "Social reaction modifiers when unsheathing or brandishing a weapon around villagers.",
            Field("Scariness", GdbFieldType.Float, "0.0", "Fear reaction modifier."),
            Field("Attractiveness", GdbFieldType.Float, "0.0", "Attractiveness reaction modifier."),
            Field("Aggressiveness", GdbFieldType.Float, "0.0", "Aggressiveness reaction modifier."),
            Field("Morality", GdbFieldType.Float, "0.0", "Morality reaction modifier.")),

        Slot(
            "CarriedComponent",
            "Carried Holster Component",
            "Combat & Weapons",
            "Controls how a weapon or trophy attaches to the character's holster or carry bone (DefaultCarrySlot).",
            Field("DefaultCarrySlot", GdbFieldType.String, "", "Default character bone/holster slot name."),
            Field("SlotType", GdbFieldType.Enum, "1", "Holster slot type (hip, back, etc.).")),

        Slot(
            "BonusContainerComponent",
            "Bonus Container Component",
            "Combat & Weapons",
            "Holds passive combat and stat bonus references."),

        Slot(
            "ProjectileComponent",
            "Projectile Component",
            "Combat & Weapons",
            "Defines impact particles, knockdown, and damage behavior for thrown/fired projectiles.",
            Field("ApplyDamageToTarget", GdbFieldType.Bool, "true", "Applies direct damage on impact."),
            Field("DestroySelfOnImpact", GdbFieldType.Bool, "false", "Destroys projectile entity on impact."),
            Field("Blockable", GdbFieldType.Bool, "false", "Whether enemies can block the projectile."),
            Field("Knockdown", GdbFieldType.Bool, "true", "Knocks down target on hit.")),

        Slot(
            "BulletComponent",
            "Bullet Component",
            "Combat & Weapons",
            "Configures firearm bullet speed, tracer visuals, and impact physics.",
            Field("Speed", GdbFieldType.Float, "60.0", "Projectile velocity in m/s.")),

        // =========================================================================
        // 4. Consumables, Social & Quest Components
        // =========================================================================
        Slot(
            "OnUseEffectComponent",
            "On-Use Effect Component",
            "Consumables & Social",
            "Defines gameplay effects triggered when consuming food, drink, or potions.",
            Field("InstantEffect", GdbFieldType.Bool, "true", "Applies effect immediately upon consumption."),
            Field("SleepSkipHours", GdbFieldType.Int, "0", "Hours skipped when used (for beds/sleep items).")),

        Slot(
            "OnActionUseComponent",
            "On-Action Use Component",
            "Consumables & Social",
            "World interaction trigger flags and Lua script callback when the player activates an entity.",
            Field("ScriptToRun", GdbFieldType.String, "", "Lua script or action invoked on use."),
            Field("WorldIconText", GdbFieldType.String, "", "Localized interaction prompt text tag."),
            Field("UsableInWorld", GdbFieldType.Bool, "false", "Can be interacted with directly in the 3D world."),
            Field("UsableInCombat", GdbFieldType.Bool, "false", "Can be used while in combat."),
            Field("DisturbOnUse", GdbFieldType.Bool, "false", "Alerts nearby NPCs when used.")),

        Slot(
            "GiftComponent",
            "Gift & Social Component",
            "Consumables & Social",
            "Controls gifting eligibility, romantic/morality/purity shifts, alcohol level, and villager opinion effects.",
            Field("CanBeGift", GdbFieldType.Bool, "true", "Allows presenting this item as a gift to NPCs."),
            Field("IsRomantic", GdbFieldType.Bool, "false", "Marks the gift as romantic."),
            Field("IsRing", GdbFieldType.Bool, "false", "Marks the gift as an engagement/wedding ring."),
            Field("PurityModifier", GdbFieldType.Int, "0", "Purity/corruption shift when consumed or gifted."),
            Field("BeggarMoralityModifier", GdbFieldType.Int, "0", "Morality shift when gifted to a beggar."),
            Field("AlcoholLevel", GdbFieldType.Float, "0.0", "Drunkenness level added when consumed."),
            Field("PeakEffect", GdbFieldType.Float, "0.5", "Peak villager opinion multiplier."),
            Field("InitialEffect", GdbFieldType.Float, "0.2", "Initial villager opinion multiplier."),
            Field("PersistentEffect", GdbFieldType.Float, "0.2", "Permanent villager opinion shift.")),

        Slot(
            "MoneyComponent",
            "Money Pouch Component",
            "Consumables & Social",
            "Configures gold bags and coin pouches that grant gold directly on pickup.",
            Field("Amount", GdbFieldType.Int, "100", "Gold coins granted on pickup."),
            Field("PlaySoundLoop", GdbFieldType.Bool, "true", "Plays coin jingle sound effect.")),

        Slot(
            "ReadableComponent",
            "Readable Text Component",
            "Consumables & Social",
            "Configures books, letters, diaries, and notes that display readable pages or unlock expressions/dog tricks.",
            Field("TextTag", GdbFieldType.String, "", "Localized body text tag for the book or document."),
            Field("IsPopup", GdbFieldType.Bool, "true", "Opens the full-screen reading popup."),
            Field("DestroyBookPostRead", GdbFieldType.Bool, "false", "Consumes the book after reading (e.g. expression/dog manuals).")),

        Slot(
            "HeroTitleComponent",
            "Hero Title Component",
            "Consumables & Social",
            "Configures Hero Title badges sold by Town Criers and binds the localized title text tag.",
            Field("TextTag", GdbFieldType.String, "", "Localized hero title text tag."),
            Field("MessageBoxTextTag", GdbFieldType.String, "", "Localized description text tag.")),

        Slot(
            "TrophyExpressionDataComponent",
            "Trophy Expression Component",
            "Consumables & Social",
            "Configures monster heads and quest trophies that can be shown off to crowds or mounted on house plaques.",
            Field("BaseRenown", GdbFieldType.Int, "100", "Base renown awarded when showing off this trophy."),
            Field("MinAdultsRequired", GdbFieldType.Int, "5", "Minimum crowd size required for full renown.")),

        Slot(
            "FurnitureUnlockComponent",
            "Furniture Unlock Component",
            "Consumables & Social",
            "Categorizes furniture items by room placement type and home value bonus.",
            Field("FurnitureType", GdbFieldType.Enum, "0", "Room furniture slot classification.")),

        // =========================================================================
        // 5. Creatures, NPCs & AI Components
        // =========================================================================
        Slot(
            "CreatureComponent",
            "Creature Component",
            "Creatures & NPCs",
            "Core creature definition including localized NameTag, combat balance table, and audio prefix.",
            Field("NameTag", GdbFieldType.String, "", "Localized creature display name written to book.babel.csv."),
            Field("NameGroupTag", GdbFieldType.String, "", "Name pool tag for random villager names."),
            Field("CombatBalanceDataTableName", GdbFieldType.String, "", "Combat balance table identifier."),
            Field("SoundEventPrefix", GdbFieldType.String, "", "Audio bank event prefix.")),

        Slot(
            "HealthComponent",
            "Health Component",
            "Creatures & NPCs",
            "Controls creature hitpoints, invulnerability, regeneration, and knockdown thresholds.",
            Field("Health", GdbFieldType.Float, "100.0", "Base maximum health."),
            Field("CanDie", GdbFieldType.Bool, "true", "Whether the entity can be killed."),
            Field("Invulnerable", GdbFieldType.Bool, "false", "Ignores incoming damage.")),

        Slot(
            "FactionComponent",
            "Faction Component",
            "Creatures & NPCs",
            "Assigns creature allegiance and hostility rules toward the Hero, villagers, guards, and monsters.",
            Field("Faction", GdbFieldType.Enum, "0", "Primary faction identifier.")),

        Slot(
            "CombatComponent",
            "Combat Component",
            "Creatures & NPCs",
            "Configures AI combat style, block/dodge behavior, and attack engagement rules.",
            Field("CombatStyle", GdbFieldType.String, "", "Combat style record name.")),

        Slot(
            "AIBrainComponent",
            "AI Brain Component",
            "Creatures & NPCs",
            "Binds the high-level AI behavior tree and zone update logic.",
            Field("Brain", GdbFieldType.String, "", "AI brain definition name.")),

        Slot(
            "OpinionReactionComponent",
            "Opinion & Social Reaction Component",
            "Creatures & NPCs",
            "Controls NPC personality traits, opinion thresholds, and reactions to Hero expressions and morality."),

        Slot(
            "GossipComponent",
            "Gossip Component",
            "Creatures & NPCs",
            "Enables ambient villager gossip dialogue and rumor topics."),

        Slot(
            "TalkComponent",
            "Talk & Dialogue Component",
            "Creatures & NPCs",
            "Configures voice narrator ID and dialogue playback capabilities.",
            Field("Narrator", GdbFieldType.String, "", "Voice narrator identifier.")),

        Slot(
            "VillagerComponent",
            "Villager Component",
            "Creatures & NPCs",
            "Configures villager job title (JobTag), daily schedule, and home/workplace bindings.",
            Field("JobTag", GdbFieldType.String, "", "Localized job title text tag.")),

        Slot(
            "VillageMemberComponent",
            "Village Member Component",
            "Creatures & NPCs",
            "Registers an NPC or building with a parent village population manager."),

        Slot(
            "InventoryComponent",
            "Entity Inventory Component",
            "Creatures & NPCs",
            "Holds an NPC's or container's carried items and equipment loadout."),

        Slot(
            "ExperienceRewardComponent",
            "Experience Reward Component",
            "Creatures & NPCs",
            "Configures XP orbs dropped when this creature is defeated in combat.",
            Field("Experience", GdbFieldType.Int, "10", "Base general experience granted.")),

        Slot(
            "NPCRewardGiverComponent",
            "NPC Reward Giver Component",
            "Creatures & NPCs",
            "Configures gifts, discounts, and rewards given by friendly/romanced villagers."),

        Slot(
            "NavigationComponent",
            "Navigation Speed Component",
            "Creatures & NPCs",
            "Controls walk, run, and sprint movement speeds for navigating creatures.",
            Field("WalkSpeed", GdbFieldType.Float, "1.5", "Base walking speed in m/s."),
            Field("RunSpeed", GdbFieldType.Float, "4.5", "Base running speed in m/s.")),

        Slot(
            "PhysicsSimulationCharacterNavigatorComponent",
            "Character Physics Navigator Component",
            "Creatures & NPCs",
            "Havok character capsule controller and pathfinding collision settings.",
            Field("PhysicsFile", GdbFieldType.String, "", "Havok physics file path.")),

        Slot(
            "GenderComponent",
            "Gender Component",
            "Creatures & NPCs",
            "Specifies entity gender for animations, voice selection, and clothing morphs.",
            Field("Gender", GdbFieldType.Enum, "0", "Gender enum value.")),

        Slot(
            "StatsComponent",
            "Hero Stats Component",
            "Creatures & NPCs",
            "Tracks Hero physique, skill, will, morality, and purity stat progressions."),

        Slot(
            "PlayerAbilityComponent",
            "Player Ability Component",
            "Creatures & NPCs",
            "Configures unlocked Hero combat abilities, spells, and skill levels."),

        // =========================================================================
        // 6. Buildings, Shops, Economy & Loot Generators
        // =========================================================================
        Slot(
            "BuildingComponent",
            "Building & Real Estate Component",
            "Buildings & Economy",
            "Defines property name (BuildingName), description, purchase price, rent income, and upgrade tiers.",
            Field("BuildingName", GdbFieldType.String, "", "Localized building name written to book.babel.csv."),
            Field("BuildingAddress", GdbFieldType.String, "", "Localized region/address text tag."),
            Field("BuildingAnecdotes", GdbFieldType.String, "", "Localized property description/anecdote tag."),
            Field("BuildingBenefits", GdbFieldType.String, "", "Localized property bonus description tag.")),

        Slot(
            "ShopComponent",
            "Merchant Shop Component",
            "Buildings & Economy",
            "Configures merchant store stock lists, markup/markdown pricing multipliers, and opening hours."),

        Slot(
            "WorkplaceComponent",
            "Workplace Component",
            "Buildings & Economy",
            "Defines job slots and wages associated with a shop, tavern, or smithy."),

        Slot(
            "LocationManagerComponent",
            "Location Manager Component",
            "Buildings & Economy",
            "Manages interior/exterior bounds and occupant slots for a building or camp."),

        Slot(
            "RandomlyGeneratedItem",
            "Random Loot Table Component",
            "Buildings & Economy",
            "Defines weighted random item generation tables for chests, shops, and enemy drops.",
            Field("TableEntry", GdbFieldType.String, "", "Loot table entry identifier.")),

        // =========================================================================
        // 7. World Simulation, Interactive Props, FX & Markers
        // =========================================================================
        Slot(
            "PhysicsSimulationDynamicComponent",
            "Dynamic Physics Component",
            "World & Simulation",
            "Havok rigid-body collision, mass, friction, and motion settings for dropped/world items.",
            Field("Mass", GdbFieldType.Float, "-1.0", "Rigid-body mass (-1 uses Havok file default)."),
            Field("Radius", GdbFieldType.Float, "1.0", "Bounding sphere radius."),
            Field("HavokFrictionOverride", GdbFieldType.Float, "30.0", "Surface friction override."),
            Field("IgnoredByCamera", GdbFieldType.Bool, "false", "Prevents camera collision clipping.")),

        Slot(
            "PhysicsSimulationStaticComponent",
            "Static Physics Component",
            "World & Simulation",
            "Static non-moving Havok collision for fixed quest pedestals, chests, buildings, and static props.",
            Field("Radius", GdbFieldType.Float, "1.0", "Bounding sphere radius."),
            Field("IgnoredByCamera", GdbFieldType.Bool, "false", "Prevents camera collision clipping."),
            Field("Navigable", GdbFieldType.Bool, "false", "Allows navigation mesh traversal.")),

        Slot(
            "PhysicsSimpleComponent",
            "Simple Physics Transform Component",
            "World & Simulation",
            "Lightweight spatial position/rotation component for particle emitters, sound sources, and markers."),

        Slot(
            "ObjectComponent",
            "World Object Component",
            "World & Simulation",
            "Defines world-object durability, breakability, and scorching behavior.",
            Field("Breakable", GdbFieldType.Bool, "false", "Whether the object can be smashed."),
            Field("Toughness", GdbFieldType.Float, "0.0", "Damage threshold required to break."),
            Field("Scorchable", GdbFieldType.Bool, "true", "Receives spell scorch decals.")),

        Slot(
            "BreakableComponent",
            "Breakable Prop Component",
            "World & Simulation",
            "Configures smashable barrels, crates, and pots, including debris meshes and loot drops."),

        Slot(
            "ChestComponent",
            "Treasure Chest Component",
            "World & Simulation",
            "Configures open/close animations, silver key requirements, and chest loot delivery.",
            Field("OpenerAnimName", GdbFieldType.String, "", "Animation played when opening the chest.")),

        Slot(
            "DoorComponent",
            "Door & Gate Component",
            "World & Simulation",
            "Configures door lock states, opening animations, and breakable door replacements.",
            Field("OverrideMessageTextTag", GdbFieldType.String, "", "Localized prompt text tag when locked.")),

        Slot(
            "BedComponent",
            "Bed Sleep Component",
            "World & Simulation",
            "Configures sleep healing, time advancement, and marital bonuses for beds."),

        Slot(
            "DiggingSpotComponent",
            "Digging Spot Component",
            "World & Simulation",
            "Configures buried treasure spots detected by the player's dog."),

        Slot(
            "TargetedComponent",
            "Targeting Component",
            "World & Simulation",
            "Configures interaction targeting range, highlight priority, and lock-on focal offsets.",
            Field("Targetable", GdbFieldType.Bool, "true", "Allows player targeting/interaction."),
            Field("ValidRange", GdbFieldType.Float, "3.0", "Maximum interaction targeting distance."),
            Field("Priority", GdbFieldType.Float, "0.0", "Targeting selection priority.")),

        Slot(
            "HighlightedItemComponent",
            "Quest Highlight Glow Component",
            "World & Simulation",
            "Renders an interaction glow/sparkle around key quest items and collectibles in the world.",
            Field("Active", GdbFieldType.Bool, "true", "Enables the quest highlight glow."),
            Field("Scale", GdbFieldType.Float, "1.0", "Highlight effect scale.")),

        Slot(
            "ParticleEmitterComponent",
            "Particle Emitter Component",
            "World & Simulation",
            "Spawns visual particle effects (fire, smoke, magic aura, blood, weather).",
            Field("ParticleEmitter", GdbFieldType.String, "", "Particle emitter effect definition name.")),

        Slot(
            "ParticleAttacherComponent",
            "Particle Attacher Component",
            "World & Simulation",
            "Attaches ambient or elemental particle emitters to potions, magic items, and weapons."),

        Slot(
            "SoundComponent",
            "Sound Emitter Component",
            "World & Simulation",
            "Plays ambient or looping 3D positional audio events.",
            Field("PerFrameSoundEvent", GdbFieldType.String, "", "Sound event identifier.")),

        Slot(
            "ExplosionComponent",
            "Explosion Component",
            "World & Simulation",
            "Configures blast radius, impulse force, camera shake, and damage for explosions."),

        Slot(
            "MarkerComponent",
            "World Marker Component",
            "World & Simulation",
            "Defines spawn points, waypoints, patrol nodes, and level script markers."),

        Slot(
            "VillageComponent",
            "Village Manager Component",
            "World & Simulation",
            "Configures village economy, town crier, pub games, and regional laws."),

        Slot(
            "ObjectAttachmentComponent",
            "Object Attachment Component",
            "World & Simulation",
            "Controls dummy/bone attachment rules and physics reactions when an item is attached to a character.",
            Field("DetachOnRagdoll", GdbFieldType.Bool, "false", "Drops the item if the holder ragdolls."),
            Field("PassOnDamageToParent", GdbFieldType.Bool, "false", "Forwards impacts to the parent entity.")),

        Slot("NetworkComponent", "Network Sync Component", "World & Simulation", "Co-op network replication component."),
        Slot("AnimationManagerComponent", "Animation Manager Component", "World & Simulation", "Manages skeletal animation playback for animated entities."),
        Slot(
            "DogLeadToComponent",
            "Dog Lead-To Component",
            "World & Simulation",
            "Allows the player's dog to sniff out and lead the Hero to this object.",
            Field("Active", GdbFieldType.Bool, "true", "Enables dog treasure-sniffing detection."),
            Field("Priority", GdbFieldType.Int, "5", "Dog alert priority."),
            Field("Radius", GdbFieldType.Float, "15.0", "Detection radius in meters.")),
        Slot("ActionManagerSimpleComponent", "Simple Action Manager Component", "World & Simulation", "Lightweight action state machine for multi-shot clockwork weapons."),
        Slot("WhereaboutsComponent", "Whereabouts Component", "World & Simulation", "Tracks world region location for interactive entities."),
        Slot("MapwhoComponent", "Mapwho Spatial Grid Component", "World & Simulation", "Registers the entity in the spatial partition grid."),
    };

    private static readonly Dictionary<uint, ComponentSlotSchema> SchemaByHash = BuildSchemaByHash();
    private static readonly Dictionary<string, ComponentSlotSchema> SchemaByName = BuildSchemaByName();

    private static Dictionary<uint, ComponentSlotSchema> BuildSchemaByHash()
    {
        var map = new Dictionary<uint, ComponentSlotSchema>();
        foreach (var s in AllComponentSchemas)
        {
            map[s.SlotHash] = s;
        }
        return map;
    }

    private static Dictionary<string, ComponentSlotSchema> BuildSchemaByName()
    {
        var map = new Dictionary<string, ComponentSlotSchema>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in AllComponentSchemas)
        {
            map[s.SlotName] = s;
        }
        return map;
    }

    public static bool TryGetComponentSchema(uint slotHash, out ComponentSlotSchema schema) =>
        SchemaByHash.TryGetValue(slotHash, out schema!);

    public static bool TryGetComponentSchema(string slotName, out ComponentSlotSchema schema) =>
        SchemaByName.TryGetValue(slotName, out schema!);
}
