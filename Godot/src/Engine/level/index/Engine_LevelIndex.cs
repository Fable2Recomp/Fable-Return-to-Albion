using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Refii.Bnk;

namespace Refii.Level;

public sealed class LevelEntry
{
    public string LevelPath { get; init; } = string.Empty;

    public string Group { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string[] Scenarios { get; init; } = Array.Empty<string>();

    /// <summary>
    /// The scenario this entry is being built for, once something has resolved one.
    ///
    /// Settable rather than init-only because the index discovers heightfields and only the
    /// GUEST knows which scenario it is standing in; the resolver stamps it on the entry it
    /// picked. Empty means nothing has said, and the readers fall back to searching.
    /// </summary>
    public string Scenario { get; set; } = string.Empty;

    public string HeightfieldPath { get; init; } = string.Empty;

    public uint HeightfieldSize { get; init; }

    public string[] AdjacentEhfPaths { get; init; } = Array.Empty<string>();

    public string MainEhfPath { get; init; } = string.Empty;

    public string Variant { get; init; } = string.Empty;

    public string ScenePath => Variant.Length == 0
        ? $"res://levels/{World}/{LevelPath}.tscn"
        : $"res://levels/{World}/{LevelPath}/{Variant}.tscn";

    public string World { get; init; } = "albion";

    public string DisplayName =>
        Variant.Length == 0 ? Name : $"{Name}  ({Variant})";

    public string SceneNodeName
    {
        get
        {
            string raw = Variant.Length == 0 ? Name : $"{Name}_{Variant}";
            var text = new StringBuilder(raw.Length);
            foreach (char c in raw)
            {
                text.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
            }

            return text.Length == 0 ? "Level" : text.ToString();
        }
    }

    public override string ToString() => $"{World}/{LevelPath}";
}

public static class Engine_LevelIndex
{
    public const string RegistryPath = "data/scenarios.list";

    private static readonly Regex LevelLine = new(
        @"level:'([^']+)'\s+(\d+)((?:\s*'[^']*')*)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Quoted = new(@"'([^']*)'", RegexOptions.Compiled);

    private static readonly Regex IdSuffix =
        new(@"_id_[0-9a-f]{8}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private sealed record RegisteredLevel(string World, string Path, string[] Scenarios);

    public static List<LevelEntry> Discover(BnkFileSystem filesystem)
    {
        List<RegisteredLevel> registry = ReadRegistry(filesystem);

        var owned = new Dictionary<RegisteredLevel, List<BnkFile>>();

        foreach (BnkFile file in filesystem.EnumerateAllFiles())
        {
            if (!file.VirtualPath.EndsWith(".ghf", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            RegisteredLevel? owner = null;
            foreach (RegisteredLevel level in registry)
            {
                string prefix = $"worlds/{level.World}/{level.Path}/";
                if (!file.VirtualPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (owner is null || level.Path.Length > owner.Path.Length)
                {
                    owner = level;
                }
            }

            if (owner is null)
            {
                continue;
            }

            if (!owned.TryGetValue(owner, out List<BnkFile>? files))
            {
                owned[owner] = files = new List<BnkFile>();
            }

            files.Add(file);
        }

        Dictionary<string, string[]> adjacency = BuildAdjacency(filesystem);

        var mainEhf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (BnkFile file in filesystem.EnumerateAllFiles())
        {
            if (file.VirtualPath.EndsWith(".ehf", StringComparison.OrdinalIgnoreCase))
            {
                mainEhf[StemOf(file.VirtualPath)] = file.VirtualPath;
            }
        }

        var levels = new List<LevelEntry>();
        foreach ((RegisteredLevel level, List<BnkFile> files) in owned)
        {
            string[] segments = level.Path.Split('/');
            string name = segments[^1];
            string group = segments.Length > 1 ? segments[0] : level.World;
            bool needsVariant = files.Count > 1;

            files.Sort((a, b) =>
                string.Compare(a.VirtualPath, b.VirtualPath, StringComparison.OrdinalIgnoreCase));

            foreach (BnkFile file in files)
            {
                string stem = file.VirtualPath[(file.VirtualPath.LastIndexOf('/') + 1)..];
                stem = stem[..^".ghf".Length];

                levels.Add(new LevelEntry
                {
                    World = level.World,
                    LevelPath = level.Path,
                    Group = group,
                    Name = name,
                    Scenarios = level.Scenarios,
                    HeightfieldPath = file.VirtualPath,
                    HeightfieldSize = file.Size,
                    Variant = needsVariant ? IdSuffix.Replace(stem, string.Empty) : string.Empty,
                    AdjacentEhfPaths = adjacency.TryGetValue(stem, out string[]? adjacent)
                        ? adjacent
                        : Array.Empty<string>(),
                    MainEhfPath = mainEhf.TryGetValue(stem, out string? own) ? own : string.Empty,
                });
            }
        }

        levels.Sort((a, b) =>
        {
            int byGroup = string.Compare(a.Group, b.Group, StringComparison.OrdinalIgnoreCase);
            if (byGroup != 0)
            {
                return byGroup;
            }

            int byPath = string.Compare(a.LevelPath, b.LevelPath, StringComparison.OrdinalIgnoreCase);
            return byPath != 0
                ? byPath
                : string.Compare(a.Variant, b.Variant, StringComparison.OrdinalIgnoreCase);
        });

        return levels;
    }

    private static Dictionary<string, string[]> BuildAdjacency(BnkFileSystem filesystem)
    {
        var adjacency = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        var ehfByLeaf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ghfStems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var engineLevels = new List<BnkFile>();

        foreach (BnkFile file in filesystem.EnumerateAllFiles())
        {
            string path = file.VirtualPath;
            if (path.EndsWith(".ehf", StringComparison.OrdinalIgnoreCase))
            {
                ehfByLeaf[LeafOf(path)] = path;
            }
            else if (path.EndsWith(".ghf", StringComparison.OrdinalIgnoreCase))
            {
                ghfStems.Add(StemOf(path));
            }
            else if (path.EndsWith(".engine_level", StringComparison.OrdinalIgnoreCase))
            {
                engineLevels.Add(file);
            }
        }

        foreach (BnkFile engineLevel in engineLevels)
        {
            byte[] bytes;
            try
            {
                bytes = engineLevel.ReadAllBytes();
            }
            catch (Exception)
            {
                continue;
            }

            List<string> references = Engine_EngineLevel.ReadEhfReferences(bytes);
            if (references.Count < 2)
            {
                continue;
            }

            string? mainStem = null;
            var adjacent = new List<string>();

            foreach (string reference in references)
            {
                string leaf = Engine_EngineLevel.LeafOf(reference);
                string stem = leaf.EndsWith(".ehf", StringComparison.OrdinalIgnoreCase)
                    ? leaf[..^".ehf".Length]
                    : leaf;

                if (mainStem is null && ghfStems.Contains(stem))
                {
                    mainStem = stem;
                    continue;
                }

                if (ehfByLeaf.TryGetValue(leaf, out string? resolved))
                {
                    adjacent.Add(resolved);
                }
            }

            if (mainStem is not null && adjacent.Count != 0)
            {
                adjacency[mainStem] = adjacent.ToArray();
            }
        }

        return adjacency;
    }

    private static string LeafOf(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash < 0 ? path : path[(slash + 1)..];
    }

    private static string StemOf(string path)
    {
        string leaf = LeafOf(path);
        int dot = leaf.LastIndexOf('.');
        return dot < 0 ? leaf : leaf[..dot];
    }

    private static List<RegisteredLevel> ReadRegistry(BnkFileSystem filesystem)
    {
        var registry = new List<RegisteredLevel>();

        if (!filesystem.TryGetFile(RegistryPath, out BnkFile file))
        {
            return registry;
        }

        string text;
        try
        {
            text = Encoding.UTF8.GetString(file.ReadAllBytes());
        }
        catch (Exception)
        {
            return registry;
        }

        foreach (Match match in LevelLine.Matches(text))
        {
            string full = match.Groups[1].Value.Replace((char)92, '/').Trim().Trim('/');
            if (full.Length == 0)
            {
                continue;
            }

            int split = full.IndexOf('/');
            string world = split < 0 ? full : full[..split];
            string path = split < 0 ? string.Empty : full[(split + 1)..];
            if (path.Length == 0)
            {
                continue;
            }

            string[] scenarios = Quoted.Matches(match.Groups[3].Value)
                .Select(m => m.Groups[1].Value)
                .Where(s => s.Length != 0)
                .ToArray();

            registry.Add(new RegisteredLevel(world, path, scenarios));
        }

        return registry;
    }

    public static List<IGrouping<string, LevelEntry>> ByGroup(BnkFileSystem filesystem) =>
        Discover(filesystem)
            .GroupBy(level => level.Group, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
