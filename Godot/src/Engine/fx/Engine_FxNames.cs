using System;
using System.Collections.Generic;

namespace Refii.Fx;

public static class Engine_FxNames
{
    private static readonly string[] Extensions = { ".gdb", ".lua", ".save", ".list" };

    public static bool WantsFile(string path)
    {
        foreach (string ext in Extensions)
        {
            if (path.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static Dictionary<uint, string> Harvest(
        HashSet<uint> wanted, IEnumerable<string> files, Func<string, byte[]?> read)
    {
        var found = new Dictionary<uint, string>();
        if (wanted.Count == 0)
        {
            return found;
        }

        foreach (string path in files)
        {
            if (!WantsFile(path))
            {
                continue;
            }

            byte[]? bytes = read(path);
            if (bytes is not null && bytes.Length != 0)
            {
                HarvestBuffer(bytes, wanted, found);
            }
        }

        return found;
    }

    public static int Apply(FxBank bank, Dictionary<uint, string> names)
    {
        int applied = 0;
        foreach (FxEffect effect in bank.Effects)
        {
            if (names.TryGetValue(effect.NameHash, out string? name))
            {
                effect.Name = name;
                applied++;
            }
        }

        return applied;
    }

    public static void HarvestBuffer(
        byte[] d, HashSet<uint> wanted, Dictionary<uint, string> outMap)
    {
        int n = d.Length;
        int i = 0;
        while (i < n)
        {
            if (!IsAlpha(d[i]))
            {
                i++;
                continue;
            }

            int j = i + 1;
            while (j < n && IsIdent(d[j]))
            {
                j++;
            }

            int len = j - i;
            if (len >= 5 && len <= 63)
            {
                TryMatch(d, i, j, wanted, outMap);
                if (len >= 6 &&
                    (d[i] | 0x20) == 'm' &&
                    (d[i + 1] | 0x20) == 'f' &&
                    (d[i + 2] | 0x20) == 'x')
                {
                    TryMatch(d, i + 1, j, wanted, outMap);
                }
            }

            i = j;
        }
    }

    private static void TryMatch(
        byte[] d, int a, int b, HashSet<uint> wanted, Dictionary<uint, string> outMap)
    {
        uint h = 0x811C9DC5u;
        for (int k = a; k < b; k++)
        {
            h *= 0x01000193u;
            h ^= ToLower(d[k]);
        }

        if (!wanted.Contains(h))
        {
            return;
        }

        string s = System.Text.Encoding.ASCII.GetString(d, a, b - a);
        if (!outMap.TryGetValue(h, out string? existing) ||
            (char.IsLower(existing[0]) && char.IsUpper(s[0])))
        {
            outMap[h] = s;
        }
    }

    private static bool IsAlpha(byte c) =>
        (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c == '_';

    private static bool IsIdent(byte c) =>
        (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
        (c >= '0' && c <= '9') || c == '_';

    private static byte ToLower(byte c) =>
        c >= 'A' && c <= 'Z' ? (byte)(c + 32) : c;
}
