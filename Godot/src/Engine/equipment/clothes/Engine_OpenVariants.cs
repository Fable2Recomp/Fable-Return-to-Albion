using System;
using System.Collections.Generic;
using Refii.Level;

namespace Refii.Equipment;

public static class Engine_OpenVariants
{
    public const string Suffix = "_Open";

    private static readonly object CacheLock = new();

    private static readonly Dictionary<string, string> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    public static bool IsOpen(string path)
    {
        string stem = StemOf(path);
        return stem.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase);
    }

    public static List<string> Candidates(string path)
    {
        var candidates = new List<string>();
        if (path.Length == 0)
        {
            return candidates;
        }

        int sep = path.LastIndexOfAny(new[] { '/', '\\' });
        string directory = sep < 0 ? string.Empty : path[..sep];
        string file = sep < 0 ? path : path[(sep + 1)..];
        char separator = sep < 0 ? '\\' : path[sep];

        int dot = file.LastIndexOf('.');
        string stem = dot < 0 ? file : file[..dot];
        string extension = dot < 0 ? string.Empty : file[dot..];

        if (directory.Length == 0)
        {
            candidates.Add(stem + Suffix + extension);
            return candidates;
        }

        int parentSep = directory.LastIndexOfAny(new[] { '/', '\\' });
        string parent = parentSep < 0 ? string.Empty : directory[..parentSep];
        string leaf = parentSep < 0 ? directory : directory[(parentSep + 1)..];

        if (leaf.Equals(stem, StringComparison.OrdinalIgnoreCase))
        {
            string folder = parent.Length == 0
                ? leaf + Suffix
                : parent + separator + leaf + Suffix;

            candidates.Add(folder + separator + stem + Suffix + extension);
        }

        candidates.Add(directory + separator + stem + Suffix + extension);
        return candidates;
    }

    public static string Resolve(string path)
    {
        if (path.Length == 0 || IsOpen(path))
        {
            return string.Empty;
        }

        lock (CacheLock)
        {
            if (Cache.TryGetValue(path, out string? cached))
            {
                return cached;
            }
        }

        string found = string.Empty;
        foreach (string candidate in Candidates(path))
        {
            string? resolved = Engine_PropModelIndex.ResolveByPath(candidate);
            if (resolved is not null && HasGeometry(resolved))
            {
                found = resolved;
                break;
            }
        }

        lock (CacheLock)
        {
            Cache[path] = found;
        }

        return found;
    }

    public static void Invalidate()
    {
        lock (CacheLock)
        {
            Cache.Clear();
        }
    }

    private static bool HasGeometry(string path)
    {
        byte[]? bytes = Refii.Models.Engine_BnkMeshLoader.ReadBytes(path);
        if (bytes is null)
        {
            return true;
        }

        return Refii.Models.Engine_MdlData.TryParseInfo(
                bytes, path, out Refii.Models.MdlInfo info, out _) &&
            !info.IsStub;
    }

    private static string StemOf(string path)
    {
        int sep = path.LastIndexOfAny(new[] { '/', '\\' });
        string file = sep < 0 ? path : path[(sep + 1)..];
        int dot = file.LastIndexOf('.');
        return dot < 0 ? file : file[..dot];
    }
}
