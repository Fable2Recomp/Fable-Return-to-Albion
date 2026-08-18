using System;
using System.Collections.Generic;

namespace Refii.Equipment;

public enum ClothingSlot
{
    Hair = 0,
    Beard = 1,
    Moustache = 2,
    Hat = 3,
    Coat = 4,
    Shirt = 5,
    Gloves = 6,
    Trousers = 7,
    Boots = 8,
    Mask = 9,
    Suit = 10,
    Accessories = 11,
    None = 12,
}

public static class Engine_ClothingSlots
{
    public const int CategoryWeapon = 1;
    public const int CategoryUncategorised = 0;
    public const int CategoryHair = 9;
    public const int CategoryBeard = 10;
    public const int CategoryMoustache = 11;
    public const int CategoryHat = 17;
    public const int CategoryCoat = 18;
    public const int CategoryShirt = 19;
    public const int CategoryGloves = 20;
    public const int CategoryTrousers = 21;
    public const int CategoryBoots = 22;
    public const int CategoryAccessory = 23;
    public const int CategorySuit = 25;

    public static readonly ClothingSlot[] Order =
    {
        ClothingSlot.Hair,
        ClothingSlot.Beard,
        ClothingSlot.Moustache,
        ClothingSlot.Hat,
        ClothingSlot.Coat,
        ClothingSlot.Shirt,
        ClothingSlot.Gloves,
        ClothingSlot.Trousers,
        ClothingSlot.Boots,
        ClothingSlot.Mask,
        ClothingSlot.Suit,
        ClothingSlot.Accessories,
    };

    public static string FolderOf(ClothingSlot slot) => slot switch
    {
        ClothingSlot.Hair => "Hair",
        ClothingSlot.Beard => "Beards",
        ClothingSlot.Moustache => "Moustaches",
        ClothingSlot.Hat => "Hats",
        ClothingSlot.Coat => "Coats",
        ClothingSlot.Shirt => "Shirts",
        ClothingSlot.Gloves => "Gloves",
        ClothingSlot.Trousers => "Trousers",
        ClothingSlot.Boots => "Boots",
        ClothingSlot.Mask => "Masks",
        ClothingSlot.Suit => "Outfits",
        ClothingSlot.Accessories => "Accessories",
        _ => "Other",
    };

    public static string LabelOf(ClothingSlot slot) => slot switch
    {
        ClothingSlot.Hair => "Hair",
        ClothingSlot.Beard => "Beard",
        ClothingSlot.Moustache => "Moustache",
        ClothingSlot.Hat => "Hat",
        ClothingSlot.Coat => "Coat",
        ClothingSlot.Shirt => "Shirt",
        ClothingSlot.Gloves => "Gloves",
        ClothingSlot.Trousers => "Trousers",
        ClothingSlot.Boots => "Boots",
        ClothingSlot.Mask => "Mask",
        ClothingSlot.Suit => "Outfit",
        ClothingSlot.Accessories => "Accessories",
        _ => "None",
    };

    public static bool TryParseFolder(string folder, out ClothingSlot slot)
    {
        foreach (ClothingSlot candidate in Order)
        {
            if (FolderOf(candidate).Equals(folder, StringComparison.OrdinalIgnoreCase))
            {
                slot = candidate;
                return true;
            }
        }

        slot = ClothingSlot.None;
        return false;
    }

    public static ClothingSlot Classify(ItemDetail detail, string searchText)
    {
        bool hasWornModel =
            detail.WornModelPathHash != 0 || detail.FemaleWornModelPathHash != 0;

        switch (detail.Category)
        {
            case CategoryHair: return ClothingSlot.Hair;
            case CategoryBeard: return ClothingSlot.Beard;
            case CategoryMoustache: return ClothingSlot.Moustache;
            case CategoryHat: return ClothingSlot.Hat;
            case CategoryCoat: return ClothingSlot.Coat;
            case CategoryShirt: return ClothingSlot.Shirt;
            case CategoryGloves: return ClothingSlot.Gloves;
            case CategoryTrousers: return ClothingSlot.Trousers;
            case CategoryBoots: return ClothingSlot.Boots;
            case CategoryAccessory:
                return searchText.Contains("mask", StringComparison.Ordinal)
                    ? ClothingSlot.Mask
                    : ClothingSlot.Accessories;
            case CategorySuit: return ClothingSlot.Suit;
            default: break;
        }

        if (detail.Category == CategoryUncategorised && hasWornModel &&
            ClassifyByName(searchText) == ClothingSlot.Accessories)
        {
            return ClothingSlot.Accessories;
        }

        return ClothingSlot.None;
    }

    public static ClothingSlot ClassifyByName(string searchText)
    {
        if (Engine_ItemNames.ContainsAny(searchText, "moustache", "mustache"))
        {
            return ClothingSlot.Moustache;
        }

        if (searchText.Contains("beard", StringComparison.Ordinal))
        {
            return ClothingSlot.Beard;
        }

        if (Engine_ItemNames.ContainsAny(searchText, "hairstyle", "hair_", "_hair", "/hair"))
        {
            return ClothingSlot.Hair;
        }

        if (Engine_ItemNames.ContainsAny(
                searchText, "rifle", "pistol", "blunderbuss", "crossbow", "firearm", "ranged"))
        {
            return ClothingSlot.None;
        }

        if (Engine_ItemNames.ContainsAny(
                searchText, "weapon", "sword", "katana", "hammer", "mace", "cleaver", "axe"))
        {
            return ClothingSlot.None;
        }

        if (Engine_ItemNames.ContainsAny(
                searchText, "accessor", "heroaccess", "bandolier", "guildseal",
                "belt", "necklace", "earring", "eyepatch"))
        {
            return ClothingSlot.Accessories;
        }

        if (searchText.Contains("mask", StringComparison.Ordinal))
        {
            return ClothingSlot.Mask;
        }

        if (Engine_ItemNames.ContainsAny(searchText, "boots", "shoes", "footwear"))
        {
            return ClothingSlot.Boots;
        }

        if (Engine_ItemNames.ContainsAny(searchText, "trouser", "pants", "leggings", "skirt"))
        {
            return ClothingSlot.Trousers;
        }

        if (Engine_ItemNames.ContainsAny(searchText, "glove", "gauntlet"))
        {
            return ClothingSlot.Gloves;
        }

        if (Engine_ItemNames.ContainsAny(searchText, "shirt", "upperbody", "waistcoat", "torso"))
        {
            return ClothingSlot.Shirt;
        }

        if (Engine_ItemNames.ContainsAny(searchText, "coat", "jacket", "overcoat"))
        {
            return ClothingSlot.Coat;
        }

        if (Engine_ItemNames.ContainsAny(
                searchText, "hat", "headwear", "hood", "helmet", "helm"))
        {
            return ClothingSlot.Hat;
        }

        if (Engine_ItemNames.ContainsAny(searchText, "suit", "outfit", "costume"))
        {
            return ClothingSlot.Suit;
        }

        return ClothingSlot.None;
    }

    public static readonly string[] FemaleMarkers = { "female", "herofemale", "fhero" };

    public static readonly string[] MaleMarkers = { "heromale", "hero_male", "_male_" };

    public static bool IsOppositeSexAsset(string searchText, bool female)
    {
        if (!female)
        {
            return Engine_ItemNames.ContainsAny(searchText, FemaleMarkers);
        }

        if (Engine_ItemNames.ContainsAny(searchText, FemaleMarkers))
        {
            return false;
        }

        return Engine_ItemNames.ContainsAny(searchText, MaleMarkers);
    }

    public static List<string> ClustersFor(ItemDetail detail, bool female)
    {
        List<string> source =
            female && detail.FemaleClustersCovered.Count != 0
                ? detail.FemaleClustersCovered
                : detail.ClustersCovered;

        var clusters = new List<string>(source.Count);
        foreach (string cluster in source)
        {
            clusters.Add(Engine_ItemNames.Normalise(cluster));
        }

        return clusters;
    }
}
