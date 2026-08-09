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

    private static Dictionary<string, List<PropModelAsset>>? _byToken;

    private static Dictionary<string, List<PropModelAsset>>? _byCompactKey;

    private static List<(string Lower, PropModelAsset Asset)>? _lowerPaths;

    private static Dictionary<string, string?>? _lowerPathCache;

    private static readonly string[] TokenSuffixes =
    {
        "facademid", "facade", "lod1", "lod0", "mid",
    };

    private static readonly string[] EntityPrefixes =
    {
        "NewObjectBuilding", "ObjectBuilding",
        "NewObjectFurniture", "ObjectFurniture",
        "NewObjectStatic", "ObjectStatic",
        "NewObject", "Object",
        "Static", "New",
    };

    public static void SetModelSource(Func<IEnumerable<PropModelAsset>>? source)
    {
        _models = source;
        Invalidate();
    }

    public static void Invalidate()
    {
        _byPathHash = null;
        _byToken = null;
        _byCompactKey = null;
        _lowerPaths = null;
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

        if (_lowerPathCache is not null &&
            _lowerPathCache.TryGetValue(target, out string? cached))
        {
            return cached;
        }

        string? hit = Resolve(PathHash(path), Array.Empty<string>())
            ?? ResolveBySuffix(target);

        if (hit is null)
        {
            string leaf = ModelToken(NameWithoutExtension(path));
            if (leaf.Length != 0 && leaf != "interior" && leaf != "exterior")
            {
                hit = ResolveByToken(leaf);
            }
        }

        Build();
        _lowerPathCache ??= new Dictionary<string, string?>();
        _lowerPathCache[target] = hit;
        return hit;
    }

    private static string? ResolveBySuffix(string target)
    {
        Build();
        if (_lowerPaths is null)
        {
            return null;
        }

        var hits = new List<PropModelAsset>();
        foreach ((string lower, PropModelAsset asset) in _lowerPaths)
        {
            if (lower.Length > target.Length &&
                lower.EndsWith(target, StringComparison.Ordinal) &&
                lower[lower.Length - target.Length - 1] == '/')
            {
                hits.Add(asset);
            }
        }

        return hits.Count == 0 ? null : Choose(hits, Array.Empty<string>());
    }

    public static string? ResolveByEntityName(string entityName)
    {
        string token = EntityToken(entityName);
        return token.Length == 0 ? null : ResolveByToken(token);
    }

    public static string? ResolveByToken(string token)
    {
        Build();

        if (_byToken is null ||
            !_byToken.TryGetValue(token, out List<PropModelAsset>? candidates) ||
            candidates.Count == 0)
        {
            return null;
        }

        return Choose(candidates, Array.Empty<string>());
    }

    public static string ModelToken(string name)
    {
        var text = new System.Text.StringBuilder(name.Length);
        foreach (char c in name)
        {
            if (c != '_')
            {
                text.Append(char.ToLowerInvariant(c));
            }
        }

        string token = text.ToString();
        foreach (string suffix in TokenSuffixes)
        {
            if (token.Length > suffix.Length &&
                token.EndsWith(suffix, StringComparison.Ordinal))
            {
                token = token[..^suffix.Length];
            }
        }

        return token;
    }

    public static string EntityToken(string entityName)
    {
        string name = entityName;

        int underscore = name.LastIndexOf('_');
        if (underscore >= 0 && underscore + 1 < name.Length)
        {
            bool digits = true;
            for (int i = underscore + 1; i < name.Length; i++)
            {
                if (!char.IsAsciiDigit(name[i]))
                {
                    digits = false;
                    break;
                }
            }

            if (digits)
            {
                name = name[..underscore];
            }
        }

        bool stripped = true;
        while (stripped)
        {
            stripped = false;
            foreach (string prefix in EntityPrefixes)
            {
                if (name.Length > prefix.Length &&
                    name.StartsWith(prefix, StringComparison.Ordinal))
                {
                    name = name[prefix.Length..];
                    stripped = true;
                    break;
                }
            }
        }

        return ModelToken(name);
    }

    public static IReadOnlyList<PropModelAsset> ByCompactKey(string key)
    {
        Build();
        return _byCompactKey is not null &&
               _byCompactKey.TryGetValue(key, out List<PropModelAsset>? hits)
            ? hits
            : Array.Empty<PropModelAsset>();
    }

    public static IEnumerable<KeyValuePair<string, List<PropModelAsset>>> CompactKeys()
    {
        Build();
        return _byCompactKey ?? new Dictionary<string, List<PropModelAsset>>();
    }

    public static int ArchivePreference(string archivePath) =>
        ArchiveScore(archivePath, Array.Empty<string>());

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

        var text = new System.Text.StringBuilder(name.Length);
        foreach (char c in name)
        {
            if (char.IsLetterOrDigit(c))
            {
                text.Append(char.ToLowerInvariant(c));
            }
        }

        return text.ToString();
    }

    private static readonly string[] EntityKeyPrefixes =
    {
        "NewObjectBuilding", "ObjectBuilding",
        "NewObjectFurniture", "ObjectFurniture",
        "NewObjectStatic", "ObjectStatic",
        "NewObject", "Object",
        "New",
    };

    public static IEnumerable<string> KeysFor(string virtualPath, string name)
    {
        var keys = new List<string>();

        void Add(string value)
        {
            if (value.Length != 0 && !keys.Contains(value))
            {
                keys.Add(value);
            }
        }

        Add(ModelToken(name));
        Add(Engine_GmdLayout.CompactKey(name));

        string token = ModelToken(name);
        if (token is not ("exterior" or "interior"))
        {
            return keys;
        }

        string normalised = virtualPath.Replace('\\', '/');
        int leaf = normalised.LastIndexOf('/');
        if (leaf <= 0)
        {
            return keys;
        }

        string parent = normalised[..leaf];
        int parentSlash = parent.LastIndexOf('/');
        string folder = parentSlash < 0 ? parent : parent[(parentSlash + 1)..];

        Add(ModelToken(folder));
        Add(Engine_GmdLayout.CompactKey(folder));
        Add(ModelToken(folder) + token);
        Add(Engine_GmdLayout.CompactKey(folder) + token);

        return keys;
    }

    private static string NameWithoutExtension(string path)
    {
        int slash = path.LastIndexOfAny(new[] { '/', '\\' });
        string leaf = slash < 0 ? path : path[(slash + 1)..];
        int dot = leaf.LastIndexOf('.');
        return dot <= 0 ? leaf : leaf[..dot];
    }

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

        return Choose(candidates, preferredArchives);
    }

    private static string Choose(
        List<PropModelAsset> candidates, IReadOnlyList<string> preferredArchives)
    {
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

        if (normalised.Contains("model_headers", StringComparison.Ordinal))
        {
            return -5000;
        }

        for (int i = 0; i < preferred.Count; i++)
        {
            if (normalised.EndsWith(preferred[i], StringComparison.Ordinal))
            {
                return 4000 - i;
            }
        }

        if (normalised.EndsWith("globals_models.bnk", StringComparison.Ordinal))
        {
            return 100;
        }

        return normalised.EndsWith("_models.bnk", StringComparison.Ordinal) ? 50 : 0;
    }

    private static void Build()
    {
        if (_byPathHash is not null || _models is null)
        {
            return;
        }

        var index = new Dictionary<uint, List<PropModelAsset>>();
        var tokens = new Dictionary<string, List<PropModelAsset>>();
        var compact = new Dictionary<string, List<PropModelAsset>>();
        var lowered = new List<(string, PropModelAsset)>();

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
            lowered.Add((model.VirtualPath.Replace('\\', '/').ToLowerInvariant(), model));

            string name = NameWithoutExtension(model.VirtualPath);

            foreach (string key in KeysFor(model.VirtualPath, name))
            {
                if (!compact.TryGetValue(key, out List<PropModelAsset>? keyed))
                {
                    keyed = new List<PropModelAsset>();
                    compact[key] = keyed;
                }

                keyed.Add(model);

                if (!tokens.TryGetValue(key, out List<PropModelAsset>? named))
                {
                    named = new List<PropModelAsset>();
                    tokens[key] = named;
                }

                named.Add(model);
            }
        }

        _byPathHash = index;
        _byToken = tokens;
        _byCompactKey = compact;
        _lowerPaths = lowered;
    }
}
