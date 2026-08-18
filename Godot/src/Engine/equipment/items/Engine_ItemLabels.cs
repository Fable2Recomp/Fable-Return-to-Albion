using System;
using System.Text;

namespace Refii.Equipment;

public static class Engine_ItemLabels
{
    private static readonly string[] Prefixes = { "INV_ITEM_", "OBJECT_", "TEXT_" };

    public const string NameSuffix = "_NAME";

    public static string Prettify(string tag)
    {
        if (tag.Length == 0)
        {
            return tag;
        }

        foreach (string prefix in Prefixes)
        {
            if (tag.Length > prefix.Length &&
                tag.StartsWith(prefix, StringComparison.Ordinal))
            {
                tag = tag[prefix.Length..];
                break;
            }
        }

        if (tag.Length > NameSuffix.Length &&
            tag.EndsWith(NameSuffix, StringComparison.Ordinal))
        {
            tag = tag[..^NameSuffix.Length];
        }

        bool hasLower = false;
        foreach (char c in tag)
        {
            if (char.IsLower(c))
            {
                hasLower = true;
                break;
            }
        }

        if (tag.IndexOf('_') < 0 && hasLower)
        {
            return tag;
        }

        var text = new StringBuilder(tag.Length);
        bool wordStart = true;
        foreach (char c in tag)
        {
            if (c == '_')
            {
                text.Append(' ');
                wordStart = true;
                continue;
            }

            text.Append(wordStart ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c));
            wordStart = false;
        }

        return text.ToString();
    }

    public static string Unnamed(uint recordHash) => $"unnamed 0x{recordHash:X8}";

    public static int CompareLabels(string a, string b)
    {
        int shared = Math.Min(a.Length, b.Length);
        for (int i = 0; i < shared; i++)
        {
            int ca = char.ToLowerInvariant(a[i]);
            int cb = char.ToLowerInvariant(b[i]);
            if (ca != cb)
            {
                return ca - cb;
            }
        }

        return a.Length - b.Length;
    }
}
