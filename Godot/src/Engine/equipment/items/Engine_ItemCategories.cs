using System;
using System.Collections.Generic;

namespace Refii.Equipment;

public static class Engine_ItemCategories
{
    public const int Uncategorised = 0;
    public const int Weapon = 1;
    public const int Archaeology = 2;
    public const int Gift = 3;
    public const int Trophy = 4;
    public const int Alcohol = 5;
    public const int Potion = 6;
    public const int Augment = 7;
    public const int Dye = 8;
    public const int Hair = 9;
    public const int Beard = 10;
    public const int Moustache = 11;
    public const int Tattoo = 12;
    public const int Makeup = 13;
    public const int HeroTitle = 14;
    public const int Furniture = 15;
    public const int Quest = 16;
    public const int Hat = 17;
    public const int Coat = 18;
    public const int Shirt = 19;
    public const int Gloves = 20;
    public const int Trousers = 21;
    public const int Boots = 22;
    public const int Accessory = 23;
    public const int Outfit = 25;
    public const int MiscProp = 26;

    public const string UnknownFolder = "Other";

    public static readonly int[] Order =
    {
        Quest,
        Weapon,
        Outfit,
        Hat,
        Coat,
        Shirt,
        Gloves,
        Trousers,
        Boots,
        Accessory,
        Hair,
        Beard,
        Moustache,
        Tattoo,
        Makeup,
        Dye,
        Potion,
        Augment,
        Alcohol,
        Gift,
        Trophy,
        Archaeology,
        HeroTitle,
        Furniture,
        MiscProp,
        Uncategorised,
    };

    public static string FolderOf(int category) => category switch
    {
        Uncategorised => "Uncategorised",
        Weapon => "Weapons",
        Archaeology => "Archaeology",
        Gift => "Gifts",
        Trophy => "Trophies",
        Alcohol => "Alcohol",
        Potion => "Potions",
        Augment => "Augments",
        Dye => "Dyes",
        Hair => "Hair",
        Beard => "Beards",
        Moustache => "Moustaches",
        Tattoo => "Tattoos",
        Makeup => "Makeup",
        HeroTitle => "Hero Titles",
        Furniture => "Furniture",
        Quest => "Quest Items",
        Hat => "Hats",
        Coat => "Coats",
        Shirt => "Shirts",
        Gloves => "Gloves",
        Trousers => "Trousers",
        Boots => "Boots",
        Accessory => "Accessories",
        Outfit => "Outfits",
        MiscProp => "Misc Props",
        < 0 => UnknownFolder,
        _ => $"Category {category}",
    };

    public static string LabelOf(int category) => category switch
    {
        Uncategorised => "Uncategorised",
        Weapon => "Weapon",
        Archaeology => "Archaeology",
        Gift => "Gift",
        Trophy => "Trophy",
        Alcohol => "Alcohol",
        Potion => "Potion",
        Augment => "Augment",
        Dye => "Dye",
        Hair => "Hair",
        Beard => "Beard",
        Moustache => "Moustache",
        Tattoo => "Tattoo",
        Makeup => "Makeup",
        HeroTitle => "Hero Title",
        Furniture => "Furniture",
        Quest => "Quest Item",
        Hat => "Hat",
        Coat => "Coat",
        Shirt => "Shirt",
        Gloves => "Gloves",
        Trousers => "Trousers",
        Boots => "Boots",
        Accessory => "Accessory",
        Outfit => "Outfit",
        MiscProp => "Misc Prop",
        < 0 => "Unknown",
        _ => $"Category {category}",
    };

    public static string DescribeCategory(int category) =>
        category < 0 ? "-1 (unset)" : $"{category} ({LabelOf(category)})";

    public static bool TryParseFolder(string folder, out int category)
    {
        foreach (int candidate in Order)
        {
            if (FolderOf(candidate).Equals(folder, StringComparison.OrdinalIgnoreCase))
            {
                category = candidate;
                return true;
            }
        }

        if (folder.Equals(UnknownFolder, StringComparison.OrdinalIgnoreCase))
        {
            category = -1;
            return true;
        }

        if (folder.StartsWith("Category ", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(folder["Category ".Length..], out category))
        {
            return true;
        }

        category = -1;
        return false;
    }

    public static int Rank(int category)
    {
        int rank = Array.IndexOf(Order, category);
        return rank < 0 ? Order.Length : rank;
    }

    public static int CompareCategories(int a, int b)
    {
        int rank = Rank(a).CompareTo(Rank(b));
        return rank != 0 ? rank : a.CompareTo(b);
    }

    public static List<int> SortCategories(IEnumerable<int> categories)
    {
        var sorted = new List<int>(categories);
        sorted.Sort(CompareCategories);
        return sorted;
    }
}
