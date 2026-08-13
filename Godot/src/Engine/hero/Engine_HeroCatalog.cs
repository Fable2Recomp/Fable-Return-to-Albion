using System;
using System.Collections.Generic;
using System.Linq;
using Refii.Core;

namespace Refii.Hero;

public sealed class HeroEntry
{
    public string Name { get; init; } = string.Empty;

    public bool Female { get; init; }

    public bool Child { get; init; }

    public List<string> ModelPaths { get; } = new();

    public List<uint> ModelHashes { get; } = new();

    public override string ToString() => Name;
}

public static class Engine_HeroCatalog
{
    public const string Folder = "hero";

    public const string Extension = ".entity";

    private static Func<IEnumerable<string>>? _modelSource;

    private static List<HeroEntry>? _entries;

    public static void SetModelSource(Func<IEnumerable<string>>? source)
    {
        if (_modelSource == source)
        {
            return;
        }

        _modelSource = source;
        _entries = null;
    }

    public static IReadOnlyList<HeroEntry> Entries
    {
        get
        {
            Build();
            return _entries ?? new List<HeroEntry>();
        }
    }

    public static string FileNameOf(HeroEntry entry) => entry.Name + Extension;

    public static string VirtualPathOf(HeroEntry entry) =>
        $"{Folder}/{FileNameOf(entry)}";

    public static bool TryGet(string virtualPath, out HeroEntry entry)
    {
        entry = null!;
        Build();

        if (_entries is null)
        {
            return false;
        }

        string path = virtualPath.Replace('\\', '/');
        int slash = path.LastIndexOf('/');
        string leaf = slash < 0 ? path : path[(slash + 1)..];

        foreach (HeroEntry candidate in _entries)
        {
            if (string.Equals(FileNameOf(candidate), leaf, StringComparison.OrdinalIgnoreCase))
            {
                entry = candidate;
                return true;
            }
        }

        return false;
    }

    public static Refii.Entities.Engine_EntityResource? BuildResource(string virtualPath)
    {
        if (!TryGet(virtualPath, out HeroEntry entry))
        {
            return null;
        }

        var resource = new Refii.Entities.Engine_EntityResource
        {
            EntityName = entry.Name,
            EntityHash = $"0x{Engine_GameHash.ResourcePath(VirtualPathOf(entry)):X8}",
        };

        resource.ModelPaths.AddRange(entry.ModelPaths);
        resource.ModelHashes.AddRange(entry.ModelHashes);

        Refii.Entities.Engine_EntityIndex.AppendComposite(resource);
        return resource;
    }

    private static void Build()
    {
        if (_entries is not null || _modelSource is null)
        {
            return;
        }

        var models = new List<string>();
        foreach (string path in _modelSource())
        {
            if (path.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
            {
                models.Add(path);
            }
        }

        var entries = new List<HeroEntry>();

        foreach (bool female in new[] { false, true })
        {
            foreach (bool child in new[] { false, true })
            {
                HeroEntry? entry = Assemble(models, female, child);
                if (entry is not null)
                {
                    entries.Add(entry);
                }
            }
        }

        _entries = entries;
    }

    private static HeroEntry? Assemble(List<string> models, bool female, bool child)
    {
        var entry = new HeroEntry
        {
            Name = (child ? "Hero Child " : "Hero ") + (female ? "Female" : "Male"),
            Female = female,
            Child = child,
        };

        if (child)
        {
            string? body = ByLeaf(models, female ? "ch_herochild_female" : "ch_herochild_male");
            if (body is null)
            {
                return null;
            }

            Add(entry, body);
            Add(entry, ByLeafSide(models, "ch_eyecase_herochild", left: true));
            Add(entry, ByLeafSide(models, "ch_eyecase_herochild", left: false));
            Add(entry, ByLeafSide(models, "ch_eye_herochild", left: true));
            Add(entry, ByLeafSide(models, "ch_eye_herochild", left: false));
            return entry;
        }

        string gender = female ? "herofemale" : "heromale";

        string? adult = BestBody(models, gender, "morph_body");
        if (adult is null)
        {
            return null;
        }

        Add(entry, adult);
        Add(entry, BestBody(models, gender, "morph_thin"));
        Add(entry, ByLeafSide(models, "ch_eyecase_adult", left: true));
        Add(entry, ByLeafSide(models, "ch_eyecase_adult", left: false));
        Add(entry, Eye(models, left: true));
        Add(entry, Eye(models, left: false));
        return entry;
    }

    private static string? ByLeaf(List<string> models, string leafPrefix)
    {
        foreach (string path in models)
        {
            if (Leaf(path).StartsWith(leafPrefix, StringComparison.Ordinal))
            {
                return path;
            }
        }

        return null;
    }

    private static string? ByLeafSide(List<string> models, string leafPrefix, bool left)
    {
        string side = left ? "_l." : "_r.";

        foreach (string path in models)
        {
            string leaf = Leaf(path);
            if (leaf.StartsWith(leafPrefix, StringComparison.Ordinal) &&
                leaf.Contains(side, StringComparison.Ordinal))
            {
                return path;
            }
        }

        return null;
    }

    private static string Leaf(string path)
    {
        string lower = Lower(path);
        return lower[(lower.LastIndexOf('/') + 1)..];
    }

    private static void Add(HeroEntry entry, string? path)
    {
        if (path is null || entry.ModelPaths.Contains(path))
        {
            return;
        }

        entry.ModelPaths.Add(path);
        entry.ModelHashes.Add(Engine_GameHash.ResourcePath(path));
    }

    private static string? BestBody(List<string> models, string gender, string token)
    {
        string? best = null;
        int bestScore = 0;

        foreach (string path in models)
        {
            string lower = Lower(path);
            if (!lower.Contains(gender, StringComparison.Ordinal) ||
                !lower.Contains(token, StringComparison.Ordinal) ||
                lower.Contains("child", StringComparison.Ordinal))
            {
                continue;
            }

            int score = 100;
            if (lower.Contains("ch_" + gender, StringComparison.Ordinal)) { score += 30; }
            if (lower.Contains("/body/", StringComparison.Ordinal) ||
                lower.Contains("_body", StringComparison.Ordinal)) { score += 10; }

            if (score > bestScore)
            {
                bestScore = score;
                best = path;
            }
        }

        return best;
    }

    private static string? Eye(List<string> models, bool left)
    {
        string side = left ? "_l." : "_r.";
        string? best = null;
        int bestScore = 0;

        foreach (string path in models)
        {
            string lower = Lower(path);
            if (!lower.Contains("ch_eye_adult_", StringComparison.Ordinal))
            {
                continue;
            }

            if (!Leaf(path).Contains(side, StringComparison.Ordinal) || Excluded(lower))
            {
                continue;
            }

            int score = 40;
            if (lower.Contains("adult_brown", StringComparison.Ordinal)) { score += 30; }
            else if (lower.Contains("adult_blue", StringComparison.Ordinal)) { score += 20; }

            if (score > bestScore)
            {
                bestScore = score;
                best = path;
            }
        }

        return best;
    }

    private static bool Excluded(string lower)
    {
        foreach (string token in
            new[] { "blind", "turned", "lucien", "franken", "child", "eyecase" })
        {
            if (lower.Contains(token, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string Lower(string path) =>
        path.Replace('\\', '/').ToLowerInvariant();
}
