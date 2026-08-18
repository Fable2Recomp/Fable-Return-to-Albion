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
}
