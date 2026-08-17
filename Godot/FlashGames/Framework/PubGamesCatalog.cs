using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Refii.FlashGames;

public sealed class PubGameEntry
{
    public string Name { get; init; } = string.Empty;

    public string Folder { get; init; } = string.Empty;

    public string SwfPath { get; init; } = string.Empty;

    public List<string> Backgrounds { get; } = new();

    public Dictionary<string, string> Localization { get; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class PubGamesSet
{
    public string Root { get; init; } = string.Empty;

    public List<PubGameEntry> Games { get; } = new();

    public List<string> SharedTextures { get; } = new();

    public Dictionary<string, string> SharedFonts { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> SharedLocalization { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string SettingsPath { get; set; } = string.Empty;
}

public static class PubGamesCatalog
{
    public const string FolderName = "pubgames";

    public const string CommonFolder = "common";

    public const string GameFileName = "game.swf";

    public const string SettingsFileName = "settings.xml";

    public static string ResolveRoot(string gameRoot)
    {
        if (gameRoot.Length == 0)
        {
            return string.Empty;
        }

        string direct = Path.Combine(gameRoot, "data", FolderName);
        if (Directory.Exists(direct))
        {
            return direct;
        }

        string flat = Path.Combine(gameRoot, FolderName);
        return Directory.Exists(flat) ? flat : string.Empty;
    }

    public static PubGamesSet? Discover(string gameRoot)
    {
        string root = ResolveRoot(gameRoot);
        if (root.Length == 0)
        {
            return null;
        }

        var set = new PubGamesSet { Root = root };

        string common = Path.Combine(root, CommonFolder);
        if (Directory.Exists(common))
        {
            foreach (string path in Directory.EnumerateFiles(common))
            {
                string name = Path.GetFileName(path);
                string extension = Path.GetExtension(path);
                if (extension.Equals(".dds", StringComparison.OrdinalIgnoreCase))
                {
                    set.SharedTextures.Add(path);
                }
                else if (extension.Equals(".swf", StringComparison.OrdinalIgnoreCase))
                {
                    set.SharedFonts[LanguageOf(name, "fonts_")] = path;
                }
                else if (name.Equals(SettingsFileName, StringComparison.OrdinalIgnoreCase))
                {
                    set.SettingsPath = path;
                }
                else if (extension.Equals(".xml", StringComparison.OrdinalIgnoreCase))
                {
                    set.SharedLocalization[LanguageOf(name, "localization.")] = path;
                }
            }
        }

        foreach (string folder in Directory.EnumerateDirectories(root).OrderBy(f => f))
        {
            string name = Path.GetFileName(folder);
            if (name.Equals(CommonFolder, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string swf = Path.Combine(folder, GameFileName);
            if (!File.Exists(swf))
            {
                continue;
            }

            var entry = new PubGameEntry { Name = name, Folder = folder, SwfPath = swf };
            foreach (string path in Directory.EnumerateFiles(folder).OrderBy(f => f))
            {
                string leaf = Path.GetFileName(path);
                string extension = Path.GetExtension(path);
                if (extension.Equals(".dds", StringComparison.OrdinalIgnoreCase))
                {
                    entry.Backgrounds.Add(path);
                }
                else if (extension.Equals(".xml", StringComparison.OrdinalIgnoreCase))
                {
                    entry.Localization[LanguageOf(leaf, "localization.")] = path;
                }
            }

            set.Games.Add(entry);
        }

        return set;
    }

    private static string LanguageOf(string fileName, string prefix)
    {
        string stem = Path.GetFileNameWithoutExtension(fileName);
        if (stem.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return stem[prefix.Length..];
        }

        int dot = stem.LastIndexOf('.');
        return dot >= 0 ? stem[(dot + 1)..] : stem;
    }
}
