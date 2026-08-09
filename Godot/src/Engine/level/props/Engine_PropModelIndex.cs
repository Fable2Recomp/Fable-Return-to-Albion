using System;
using System.Collections.Generic;

namespace Refii.Level;

public readonly struct PropModelAsset
{
    public PropModelAsset(string virtualPath, string archivePath)
    {
        VirtualPath = virtualPath;
        ArchivePath = archivePath;
    }

    public string VirtualPath { get; }

    public string ArchivePath { get; }
}

public static class Engine_PropModelIndex
{
    public const uint NullHash = 0x811C9DC5;

    private static Func<IEnumerable<PropModelAsset>>? _models;

    private static Dictionary<uint, List<PropModelAsset>>? _byPathHash;

    public static void SetModelSource(Func<IEnumerable<PropModelAsset>>? source)
    {
        _models = source;
        _byPathHash = null;
    }

    public static void Invalidate() => _byPathHash = null;

    public static uint PathHash(string path)
    {
        uint hash = NullHash;
        foreach (char c in path.ToLowerInvariant())
        {
            byte b = (byte)(c == '/' ? '\\' : c);
            hash *= 0x01000193u;
            hash ^= b;
        }

        return hash;
    }

    public static int Count
    {
        get
        {
            Build();
            return _byPathHash?.Count ?? 0;
        }
    }

    public static string? ResolveByPath(string path) =>
        path.Length == 0 ? null : Resolve(PathHash(path), Array.Empty<string>());

    public static string? Resolve(uint pathHash, IReadOnlyList<string> preferredArchives)
    {
        if (pathHash == 0 || pathHash == NullHash)
        {
            return null;
        }

        Build();
        if (_byPathHash is null ||
            !_byPathHash.TryGetValue(pathHash, out List<PropModelAsset>? candidates) ||
            candidates.Count == 0)
        {
            return null;
        }

        PropModelAsset best = candidates[0];
        int bestScore = int.MinValue;

        foreach (PropModelAsset candidate in candidates)
        {
            int score = ArchiveScore(candidate.ArchivePath, preferredArchives);
            score -= Math.Min(candidate.VirtualPath.Length, 200);
            if (score > bestScore)
            {
                best = candidate;
                bestScore = score;
            }
        }

        return best.VirtualPath;
    }

    private static int ArchiveScore(string archivePath, IReadOnlyList<string> preferred)
    {
        string normalised = archivePath.Replace('\\', '/').ToLowerInvariant();

        for (int i = 0; i < preferred.Count; i++)
        {
            if (normalised.EndsWith(preferred[i], StringComparison.Ordinal))
            {
                return 4000 - i;
            }
        }

        return normalised.EndsWith("globals_models.bnk", StringComparison.Ordinal) ? 100 : 0;
    }

    private static void Build()
    {
        if (_byPathHash is not null || _models is null)
        {
            return;
        }

        var index = new Dictionary<uint, List<PropModelAsset>>();
        foreach (PropModelAsset model in _models())
        {
            if (model.VirtualPath.Length == 0)
            {
                continue;
            }

            uint hash = PathHash(model.VirtualPath);
            if (!index.TryGetValue(hash, out List<PropModelAsset>? bucket))
            {
                bucket = new List<PropModelAsset>();
                index[hash] = bucket;
            }

            bucket.Add(model);
        }

        _byPathHash = index;
    }
}
