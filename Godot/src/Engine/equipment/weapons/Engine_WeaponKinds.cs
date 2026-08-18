using System;
using System.Collections.Generic;

namespace Refii.Equipment;

public static class Engine_WeaponKinds
{
    public const string Fallback = "Other";

    public const string Unnamed = "Unnamed";

    public const string PlaceholderTag = "GUI_NEEDS_TEXT";

    private static readonly (string Keyword, string Folder)[] Families =
    {
        ("SPECIAL", "Special"),
        ("CROSSBOW", "Crossbows"),
        ("PISTOL", "Pistols"),
        ("RIFLE", "Rifles"),
        ("BOW", "Bows"),
        ("KATANA", "Katanas"),
        ("CUTLASS", "Cutlasses"),
        ("CLEAVER", "Cleavers"),
        ("SWORD", "Swords"),
        ("AXE", "Axes"),
        ("MACE", "Maces"),
        ("HAMMER", "Hammers"),
        ("SCEPTRE", "Staves"),
        ("STAFF", "Staves"),
    };

    public static readonly string[] FolderOrder =
    {
        "Swords", "Cutlasses", "Katanas", "Cleavers",
        "Axes", "Maces", "Hammers", "Staves",
        "Pistols", "Rifles", "Crossbows", "Bows",
        "Special", Unnamed, Fallback,
    };

    public static string FamilyOf(ItemDetail detail)
    {
        string family = Match(detail.NameTagName);
        if (family.Length != 0)
        {
            return family;
        }

        family = Match(detail.InternalName);
        if (family.Length != 0)
        {
            return family;
        }

        family = Match(detail.ModelPath);
        if (family.Length != 0)
        {
            return family;
        }

        return detail.NameTagName == PlaceholderTag || detail.Unnamed ? Unnamed : Fallback;
    }

    private static string Match(string text)
    {
        if (text.Length == 0)
        {
            return string.Empty;
        }

        string upper = text.ToUpperInvariant();
        foreach ((string keyword, string folder) in Families)
        {
            if (upper.Contains(keyword, StringComparison.Ordinal))
            {
                return folder;
            }
        }

        return string.Empty;
    }

    public static string DescribeType(int weaponType) => weaponType switch
    {
        0 => $"{weaponType} (unset)",
        >= 2 and <= 3 => $"{weaponType} (melee)",
        4 => $"{weaponType} (melee, two-handed)",
        >= 5 and <= 8 => $"{weaponType} (ranged)",
        _ => weaponType.ToString(),
    };

    public static int FolderRank(string folder)
    {
        int rank = Array.IndexOf(FolderOrder, folder);
        return rank < 0 ? FolderOrder.Length : rank;
    }

    public static int CompareFolders(string a, string b)
    {
        int rank = FolderRank(a).CompareTo(FolderRank(b));
        return rank != 0 ? rank : string.CompareOrdinal(a, b);
    }

    public static List<string> SortFolders(IEnumerable<string> folders)
    {
        var sorted = new List<string>(folders);
        sorted.Sort(CompareFolders);
        return sorted;
    }
}
