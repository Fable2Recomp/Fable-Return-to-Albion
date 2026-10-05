using System;
using System.Text;
using Refii.Gdb;

namespace Refii.Equipment;

public static class Engine_ItemNames
{
    public const string Placeholder = "Place text here...";

    public static string DisplayNameOf(ItemDetail detail)
    {
        if (detail.NameTag != 0 &&
            Engine_BnkTextBankLoader.TryLookup(detail.NameTag, out string localised) &&
            localised.Length != 0 &&
            !localised.Equals(Placeholder, StringComparison.Ordinal))
        {
            return localised;
        }

        return detail.Label;
    }

    public static string DescriptionOf(ItemDetail detail) =>
        detail.DescTag != 0 &&
        Engine_BnkTextBankLoader.TryLookup(detail.DescTag, out string text)
            ? text
            : string.Empty;

    /// <summary>
    /// Returns the unpatched vanilla display name strictly from base game data, ignoring any active mod overlays.
    /// </summary>
    public static string VanillaDisplayNameOf(ItemDetail detail)
    {
        if (detail.NameTag != 0 &&
            Engine_BnkTextBankLoader.TryLookupVanilla(detail.NameTag, out string localised) &&
            localised.Length != 0 &&
            !localised.Equals(Placeholder, StringComparison.Ordinal))
        {
            return localised;
        }

        return detail.Label;
    }

    /// <summary>
    /// Returns the unpatched vanilla description strictly from base game data, ignoring any active mod overlays.
    /// </summary>
    public static string VanillaDescriptionOf(ItemDetail detail) =>
        detail.DescTag != 0 &&
        Engine_BnkTextBankLoader.TryLookupVanilla(detail.DescTag, out string text)
            ? text
            : string.Empty;

    public static string SearchTextOf(ItemDetail detail, bool female)
    {
        var text = new StringBuilder();
        text.Append(detail.InternalName).Append(' ');
        text.Append(detail.Label).Append(' ');
        text.Append(DisplayNameOf(detail)).Append(' ');
        text.Append(detail.ModelPath).Append(' ');
        text.Append(female ? detail.FemaleWornModelPath : detail.WornModelPath);
        return Normalise(text.ToString());
    }

    public static string Normalise(string text) =>
        text.Replace('\\', '/').ToLowerInvariant();

    public static bool ContainsAny(string text, params string[] needles)
    {
        foreach (string needle in needles)
        {
            if (text.Contains(needle, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    public static string FileNameOf(string name, uint recordHash, string extension)
    {
        var text = new StringBuilder(name.Length + 12);
        foreach (char c in name)
        {
            text.Append(char.IsLetterOrDigit(c) || c is '_' or '-' or ' ' or '\'' ? c : '_');
        }

        string safe = text.ToString().Trim();
        if (safe.Length == 0)
        {
            safe = "item";
        }

        return $"{safe} [{recordHash:X8}]{extension}";
    }

    public static string LeafOf(string path)
    {
        if (path.Length == 0)
        {
            return string.Empty;
        }

        string normalised = path.Replace('\\', '/');
        int slash = normalised.LastIndexOf('/');
        return slash < 0 ? normalised : normalised[(slash + 1)..];
    }

    /// <summary>
    /// Constructs a multi-line informative tooltip for an item, displaying its localized display name,
    /// category/family, weapon type, star rating/tier, base price, augment slots / socketed augments,
    /// 3D model path, GDB record hash, and lore description.
    /// </summary>
    public static string FormatTooltip(
        ItemDetail detail,
        string? categoryOrFamily = null,
        string? extraPath = null)
    {
        var sb = new StringBuilder();
        string name = DisplayNameOf(detail);
        sb.AppendLine(name);

        if (!string.IsNullOrWhiteSpace(categoryOrFamily))
        {
            sb.Append("Category: ").AppendLine(categoryOrFamily);
        }

        if (detail.WeaponType != 0)
        {
            sb.Append("Type: ").AppendLine(Engine_WeaponKinds.DescribeType(detail.WeaponType));
        }

        if (detail.Rating > 0)
        {
            sb.Append("Tier / Rating: ").AppendLine(Engine_EquipmentScene.DescribeRating(detail.Rating));
        }

        if (detail.BasePrice >= 0)
        {
            sb.Append("Price: ").AppendFormat("{0:N0} Gold", detail.BasePrice).AppendLine();
        }
        else if (detail.Money >= 0)
        {
            sb.Append("Value: ").AppendFormat("{0:N0} Gold", detail.Money).AppendLine();
        }

        if (detail.AugmentSlots > 0 || detail.Augments.Count > 0)
        {
            if (detail.Augments.Count > 0)
            {
                sb.Append("Augments: ").AppendLine(string.Join(", ", detail.Augments));
            }
            else
            {
                sb.Append("Augment Slots: ").Append(detail.AugmentSlots).AppendLine(" (Empty)");
            }
        }

        string mesh = !string.IsNullOrWhiteSpace(extraPath)
            ? extraPath
            : detail.ModelPath.Length > 0 ? detail.ModelPath : "No 3D mesh";
        sb.Append("Model: ").AppendLine(mesh);
        sb.AppendFormat("Record: 0x{0:X8}", detail.RecordHash).AppendLine();

        string desc = DescriptionOf(detail);
        if (!string.IsNullOrWhiteSpace(desc))
        {
            sb.AppendLine();
            sb.Append(desc);
        }

        return sb.ToString().TrimEnd();
    }
}
