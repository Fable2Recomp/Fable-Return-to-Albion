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

    private static Dictionary<string, string?>? _lowerPathCache;

    private static readonly Dictionary<uint, PropModelAsset> _registeredPaths = new();

    public static void SetModelSource(Func<IEnumerable<PropModelAsset>>? source)
    {
        // Under the same lock as the build, and the SOURCE is swapped inside it too: a
        // rebuild that reads `_models` while this is halfway through indexes the old banks
        // under the new mount.
        lock (IndexLock)
        {
            _models = source;
            if (source is null)
            {
                _registeredPaths.Clear();
            }
            InvalidateLocked();
        }
    }

    public static void RegisterPath(string virtualPath, string archivePath = "mod")
    {
        if (string.IsNullOrWhiteSpace(virtualPath))
        {
            return;
        }

        string normalized = virtualPath.Trim().Replace('/', '\\');
        uint hash = PathHash(normalized);
        var asset = new PropModelAsset(normalized, archivePath);

        lock (IndexLock)
        {
            _registeredPaths[hash] = asset;
            if (_byPathHash is not null)
            {
                if (!_byPathHash.TryGetValue(hash, out List<PropModelAsset>? bucket))
                {
                    bucket = new List<PropModelAsset>();
                    _byPathHash[hash] = bucket;
                }
                else
                {
                    bucket.RemoveAll(a => string.Equals(a.VirtualPath, normalized, StringComparison.OrdinalIgnoreCase));
                }
                bucket.Insert(0, asset);
            }
            _lowerPathCache = null;
            ++Version;
        }
    }

    /// <summary>
    /// Bumped whenever a lookup may answer differently: the index dropped, or a path registered.
    /// A caller that keeps an answer keeps the version beside it.
    /// </summary>
    public static int Version { get; private set; }

    /// <summary>
    /// Drops the index so the next lookup rebuilds it — a bank remount or a level change.
    ///
    /// Locked because tearing it down is exactly as concurrent as building it, and this is the
    /// more dangerous direction: a reader holding a table this nulls out mid-lookup is reading
    /// an index for banks that are no longer mounted.
    /// </summary>
    public static void Invalidate()
    {
        lock (IndexLock)
        {
            InvalidateLocked();
        }
    }

    private static void InvalidateLocked()
    {
        ++Version;
        _byPathHash = null;
        _lowerPathCache = null;
    }

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

    public static string? ResolveByPath(string path)
    {
        if (path.Length == 0)
        {
            return null;
        }

        string target = path.Replace('\\', '/').ToLowerInvariant();

        lock (IndexLock)
        {
            if (_lowerPathCache is not null &&
                _lowerPathCache.TryGetValue(target, out string? cached))
            {
                return cached;
            }
        }

        // THE STATED PATH, AND NOTHING ELSE. No suffix match, no token match, no scoring.
        //
        // Falling back — exact hash, then any indexed path ending with the same leaf, then a
        // fuzzy token — does not fill gaps, it INVENTS them. Surveyed across all 5,446 model
        // names against the region's own data: every path a level states resolves to a model
        // that has geometry, and the handful that fail to load are paths no level ever asked
        // for, conjured by a fallback and then reported as reader defects.
        //
        // A missing prop is a hole in the world and looks like one. A substituted prop looks
        // like the world is correct, and that is the more expensive failure — it is the same
        // class as the front-end heuristic that read one string four different ways, and the
        // same rule: the guest states, we follow.
        string? hit = Resolve(PathHash(path), Array.Empty<string>());

        lock (IndexLock)
        {
            BuildLocked();
            _lowerPathCache ??= new Dictionary<string, string?>();
            _lowerPathCache[target] = hit;
        }

        return hit;
    }

    // ResolveBySuffix / ResolveByEntityName / ResolveByToken lived here and are GONE.
    //
    // They were the fallback chain: match any indexed path ending with the same leaf, or
    // tokenise a name and take the best-scoring candidate. Both invent. A survey of all 5,446
    // model names against a region's own data showed every STATED path resolves to a model
    // that has geometry — so these never recovered a real miss, they only manufactured paths
    // no level had asked for, which then failed to load and were mistaken for reader defects.
    //
    // Deleted rather than left unreferenced: a fallback that still compiles is a fallback
    // somebody re-wires in six months.

    public static string EntityKey(string entityName)
    {
        string name = entityName;

        foreach (string prefix in EntityKeyPrefixes)
        {
            if (name.Length > prefix.Length &&
                name.StartsWith(prefix, StringComparison.Ordinal))
            {
                name = name[prefix.Length..];
                break;
            }
        }

        return Engine_LevelProps.CompactText(name);
    }

    private static readonly string[] EntityKeyPrefixes =
    {
        "NewObjectBuilding", "ObjectBuilding",
        "NewObjectFurniture", "ObjectFurniture",
        "NewObjectStatic", "ObjectStatic",
        "NewObject", "Object",
        "New",
    };

    public static string? Resolve(uint pathHash, IReadOnlyList<string>? preferredArchives = null)
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

        return Choose(candidates, preferredArchives ?? Array.Empty<string>());
    }

    private static string Choose(
        List<PropModelAsset> candidates, IReadOnlyList<string> preferredArchives)
    {
        PropModelAsset best = candidates[0];
        if (candidates.Count == 1)
        {
            return best.VirtualPath;
        }

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

    /// <remarks>
    /// Compared as if the path were lower-cased with forward slashes, a character at a time
    /// rather than on a normalised copy: an attached child resolves its model through here twice
    /// on every drawn frame, and the copy was a new string per candidate each time.
    /// </remarks>
    private static int ArchiveScore(string archivePath, IReadOnlyList<string> preferred)
    {
        if (archivePath.Contains("model_headers", StringComparison.OrdinalIgnoreCase))
        {
            return -5000;
        }

        for (int i = 0; i < preferred.Count; i++)
        {
            if (EndsWithNormalised(archivePath, preferred[i]))
            {
                return 4000 - i;
            }
        }

        if (EndsWithNormalised(archivePath, "globals_models.bnk"))
        {
            return 100;
        }

        return EndsWithNormalised(archivePath, "_models.bnk") ? 50 : 0;
    }

    /// <summary>
    /// Whether <paramref name="path"/>, lower-cased invariantly with every backslash a forward
    /// slash, ends with <paramref name="suffix"/> (compared ordinally, as given).
    /// </summary>
    internal static bool EndsWithNormalised(string path, string suffix)
    {
        if (suffix.Length > path.Length)
        {
            return false;
        }

        int offset = path.Length - suffix.Length;
        for (int i = 0; i < suffix.Length; i++)
        {
            char c = path[offset + i];
            if (c == '\\')
            {
                c = '/';
            }

            if (char.ToLowerInvariant(c) != suffix[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Serialises the lazy index build and the lookup caches.
    ///
    /// A LEVEL CHANGE HAS TWO THREADS IN HERE. The next region builds on a worker while the
    /// region we are standing in still resolves models on the main thread, so both can reach
    /// an index that has not been built yet.
    ///
    /// AND THE GUARD USED TO BE PUBLISHED BEFORE THE PAYLOAD. `Build` assigned `_byPathHash`
    /// first and the other tables after it, while readers tested `_byPathHash`.
    /// </summary>
    private static readonly object IndexLock = new();

    private static void Build()
    {
        lock (IndexLock)
        {
            BuildLocked();
        }
    }

    private static void BuildLocked()
    {
        if (_byPathHash is not null || (_models is null && _registeredPaths.Count == 0))
        {
            return;
        }

        var index = new Dictionary<uint, List<PropModelAsset>>();

        if (_models is not null)
        {
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
        }

        foreach ((uint regHash, PropModelAsset regAsset) in _registeredPaths)
        {
            if (!index.TryGetValue(regHash, out List<PropModelAsset>? bucket))
            {
                bucket = new List<PropModelAsset>();
                index[regHash] = bucket;
            }
            else
            {
                bucket.RemoveAll(a => string.Equals(a.VirtualPath, regAsset.VirtualPath, StringComparison.OrdinalIgnoreCase));
            }
            bucket.Insert(0, regAsset);
        }

        _byPathHash = index;
    }
}
