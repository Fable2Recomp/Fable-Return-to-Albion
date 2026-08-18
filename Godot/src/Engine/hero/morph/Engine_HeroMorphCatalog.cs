using System;
using System.Collections.Generic;
using Godot;
using Refii.Gdb;
using Refii.Level;

namespace Refii.Hero;

public sealed class HeroMorphSet
{
    public string NeutralPath { get; init; } = string.Empty;

    public Dictionary<HeroMorphKind, string> Targets { get; } = new();

    public bool Any => Targets.Count != 0;
}

public static class Engine_HeroMorphCatalog
{
    public const string MorphToken = "_morph_";

    public const uint TypeFat = 4;
    public const uint TypeStrong = 5;
    public const uint TypeEvil = 12;
    public const uint TypeEvilPure = 13;
    public const uint TypeEvilImpure = 14;
    public const uint TypeImpure = 16;
    public const uint TypeYoung = 17;
    public const uint TypeOld = 18;

    private static readonly object CatalogLock = new();

    private static readonly Dictionary<string, HeroMorphSet> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    private static Func<string, byte[]?>? _resolver;

    private static List<MorphTargetPair>? _pairs;

    public static void SetResolver(Func<string, byte[]?>? resolver)
    {
        lock (CatalogLock)
        {
            _resolver = resolver;
            _pairs = null;
            Cache.Clear();
        }
    }

    public static void Invalidate()
    {
        lock (CatalogLock)
        {
            _pairs = null;
            Cache.Clear();
        }
    }

    public static HeroMorphKind? KindOfType(uint morphType) => morphType switch
    {
        TypeFat => HeroMorphKind.Fat,
        TypeStrong => HeroMorphKind.Strong,
        TypeEvilPure => HeroMorphKind.EvilPure,
        TypeEvil or TypeEvilImpure => HeroMorphKind.EvilImpure,
        TypeImpure => HeroMorphKind.Impure,
        TypeYoung => HeroMorphKind.Young,
        TypeOld => HeroMorphKind.Old,
        _ => null,
    };

    public static HeroMorphKind? KindOfPath(string path)
    {
        string leaf = Leaf(path).ToLowerInvariant();
        int at = leaf.LastIndexOf(MorphToken, StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }

        string token = leaf[(at + MorphToken.Length)..];
        int dot = token.IndexOf('.');
        if (dot >= 0)
        {
            token = token[..dot];
        }

        foreach (HeroMorphKind kind in Enum.GetValues<HeroMorphKind>())
        {
            if (token.Equals(TokenOf(kind), StringComparison.OrdinalIgnoreCase))
            {
                return kind;
            }
        }

        return null;
    }

    public static string TokenOf(HeroMorphKind kind) => kind switch
    {
        HeroMorphKind.Strong => "strong",
        HeroMorphKind.Fat => "fat",
        HeroMorphKind.Young => "young",
        HeroMorphKind.Old => "old",
        HeroMorphKind.Impure => "impure",
        HeroMorphKind.EvilPure => "evilpure",
        HeroMorphKind.EvilImpure => "evilimpure",
        _ => string.Empty,
    };

    public static bool IsMorphable(string path) => KindOfPath(path) is null &&
        Leaf(path).Contains(MorphToken, StringComparison.OrdinalIgnoreCase);

    public static HeroMorphSet? For(string neutralPath, bool female) =>
        For(neutralPath, female, null);

    public static HeroMorphSet? For(
        string neutralPath, bool female, Func<string, ArrayMesh?>? resolve)
    {
        if (neutralPath.Length == 0)
        {
            return null;
        }

        lock (CatalogLock)
        {
            if (Cache.TryGetValue(neutralPath, out HeroMorphSet? cached))
            {
                return cached.Any ? cached : null;
            }
        }

        HeroMorphSet built = Discover(neutralPath, female);

        if (resolve is not null)
        {
            Verify(built, resolve);
        }

        lock (CatalogLock)
        {
            Cache[neutralPath] = built;
        }

        return built.Any ? built : null;
    }

    public static void Verify(HeroMorphSet set, Func<string, ArrayMesh?> resolve)
    {
        ArrayMesh? neutral = resolve(set.NeutralPath);
        if (neutral is null)
        {
            set.Targets.Clear();
            return;
        }

        var drop = new List<HeroMorphKind>();

        foreach (KeyValuePair<HeroMorphKind, string> entry in set.Targets)
        {
            if (entry.Value.Equals(set.NeutralPath, StringComparison.OrdinalIgnoreCase))
            {
                drop.Add(entry.Key);
                continue;
            }

            ArrayMesh? target = resolve(entry.Value);
            if (target is null || !Engine_HeroMorph.TopologyMatches(neutral, target))
            {
                drop.Add(entry.Key);
            }
        }

        foreach (HeroMorphKind kind in drop)
        {
            set.Targets.Remove(kind);
        }
    }

    private static HeroMorphSet Discover(string neutralPath, bool female)
    {
        var set = new HeroMorphSet { NeutralPath = neutralPath };

        AddAuthored(set, neutralPath, female);
        AddByName(set, neutralPath);
        return set;
    }

    private static void AddAuthored(HeroMorphSet set, string neutralPath, bool female)
    {
        List<MorphTargetPair> pairs = Pairs();
        if (pairs.Count == 0)
        {
            return;
        }

        string? original = Engine_PropModelIndex.ResolveByPath(neutralPath);
        string neutralDirectory = DirectoryOf(original ?? neutralPath);
        uint neutralHash = Engine_PropModelIndex.PathHash(original ?? neutralPath);

        var best = new Dictionary<HeroMorphKind, (int Score, string Path)>();

        foreach (MorphTargetPair pair in pairs)
        {
            if (pair.OriginalModelHash != neutralHash)
            {
                continue;
            }

            if (KindOfType(pair.MorphType) is not HeroMorphKind kind)
            {
                continue;
            }

            string? target = Engine_PropModelIndex.Resolve(
                pair.TargetModelHash, Array.Empty<string>());

            if (target is null)
            {
                continue;
            }

            string lowered = target.Replace('\\', '/').ToLowerInvariant();
            if (Refii.Equipment.Engine_ClothingSlots.IsOppositeSexAsset(lowered, female))
            {
                continue;
            }

            int score = 100;
            if (neutralDirectory.Length != 0 &&
                lowered.StartsWith(neutralDirectory, StringComparison.Ordinal))
            {
                score += 30;
            }

            if (female && Engine_ItemNamesContains(lowered, "female", "herofemale", "fhero"))
            {
                score += 10;
            }
            else if (!female && Engine_ItemNamesContains(lowered, "heromale", "hero_male"))
            {
                score += 10;
            }

            if (!best.TryGetValue(kind, out (int Score, string Path) current) ||
                score > current.Score)
            {
                best[kind] = (score, target);
            }
        }

        foreach (KeyValuePair<HeroMorphKind, (int Score, string Path)> entry in best)
        {
            set.Targets[entry.Key] = entry.Value.Path;
        }
    }

    private static void AddByName(HeroMorphSet set, string neutralPath)
    {
        string normalised = neutralPath.Replace('\\', '/');
        string leaf = Leaf(normalised);
        int at = leaf.LastIndexOf(MorphToken, StringComparison.OrdinalIgnoreCase);
        if (at < 0)
        {
            return;
        }

        string stem = leaf[..(at + MorphToken.Length)];
        string extension = System.IO.Path.GetExtension(leaf);
        string parent = ParentOf(DirectoryOf(normalised));

        foreach (HeroMorphKind kind in Enum.GetValues<HeroMorphKind>())
        {
            if (set.Targets.ContainsKey(kind))
            {
                continue;
            }

            string token = TokenOf(kind);
            if (token.Length == 0)
            {
                continue;
            }

            string name = stem + token;
            string candidate = parent.Length == 0
                ? name + extension
                : $"{parent}{name}/{name}{extension}";

            string? resolved = Engine_PropModelIndex.ResolveByPath(candidate);
            if (resolved is not null)
            {
                set.Targets[kind] = resolved;
            }
        }
    }

    private static List<MorphTargetPair> Pairs()
    {
        lock (CatalogLock)
        {
            if (_pairs is not null)
            {
                return _pairs;
            }

            if (_resolver is null)
            {
                _pairs = new List<MorphTargetPair>();
                return _pairs;
            }
        }

        var blobs = new List<byte[]?>();
        foreach (string path in Engine_LevelProps.SupplementalGdbPaths)
        {
            blobs.Add(_resolver(path) ?? _resolver("data/" + path));
        }

        List<MorphTargetPair> built = Engine_GdbMorphTargets.Build(GdbMulti.Build(blobs));
        GD.Print($"[morph] {built.Count} authored morph target pair(s)");

        lock (CatalogLock)
        {
            _pairs = built;
        }

        return built;
    }

    private static bool Engine_ItemNamesContains(string text, params string[] needles) =>
        Refii.Equipment.Engine_ItemNames.ContainsAny(text, needles);

    private static string Leaf(string path)
    {
        string normalised = path.Replace('\\', '/');
        int slash = normalised.LastIndexOf('/');
        return slash < 0 ? normalised : normalised[(slash + 1)..];
    }

    private static string DirectoryOf(string path)
    {
        string normalised = path.Replace('\\', '/').ToLowerInvariant();
        int slash = normalised.LastIndexOf('/');
        return slash < 0 ? string.Empty : normalised[..(slash + 1)];
    }

    private static string ParentOf(string directory)
    {
        if (directory.Length == 0)
        {
            return string.Empty;
        }

        string trimmed = directory.TrimEnd('/');
        int slash = trimmed.LastIndexOf('/');
        return slash < 0 ? string.Empty : trimmed[..(slash + 1)];
    }
}
