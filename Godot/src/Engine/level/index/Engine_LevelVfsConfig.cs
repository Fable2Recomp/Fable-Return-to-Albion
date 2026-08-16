using System;
using System.Collections.Generic;
using Godot;

namespace Refii.Level;

public sealed class LevelVfsConfig
{
    public List<string> BankPaths { get; } = new();
    public List<string> TextureBodyBanks { get; } = new();
    public List<string> ModelBanks { get; } = new();
    public List<string> StreamingBanks { get; } = new();
}

public static class Engine_LevelVfsConfig
{
    public const string FileName = "level.vfsconfig";

    public static LevelVfsConfig Parse(string text)
    {
        var config = new LevelVfsConfig();

        bool insideTexturesGroup = false;
        int groupDepth = 0;

        int pos = 0;
        while (pos < text.Length)
        {
            int lt = text.IndexOf('<', pos);
            if (lt < 0)
            {
                break;
            }

            int gt = text.IndexOf('>', lt);
            if (gt < 0)
            {
                break;
            }

            if (lt + 3 < text.Length && text[lt + 1] == '!' &&
                text[lt + 2] == '-' && text[lt + 3] == '-')
            {
                int close = text.IndexOf("-->", lt + 4, StringComparison.Ordinal);
                pos = close < 0 ? text.Length : close + 3;
                continue;
            }

            if (lt + 1 < text.Length && text[lt + 1] == '/')
            {
                if (StartsWithAt(text, lt + 2, "Group"))
                {
                    --groupDepth;
                    if (groupDepth <= 0)
                    {
                        insideTexturesGroup = false;
                    }
                }

                pos = gt + 1;
                continue;
            }

            int nameStart = lt + 1;
            int nameEnd = nameStart;
            while (nameEnd < gt && text[nameEnd] != ' ' && text[nameEnd] != '/' &&
                   text[nameEnd] != '\t' && text[nameEnd] != '\n' && text[nameEnd] != '\r')
            {
                ++nameEnd;
            }

            string tag = text[nameStart..nameEnd];

            if (tag.Equals("Group", StringComparison.OrdinalIgnoreCase))
            {
                ++groupDepth;
                string id = ExtractAttribute(text, lt, gt, "ID");
                if (id.Equals("level_textures", StringComparison.OrdinalIgnoreCase))
                {
                    insideTexturesGroup = true;
                }
            }
            else if (tag.Equals("Bank", StringComparison.OrdinalIgnoreCase))
            {
                string path = ExtractAttribute(text, lt, gt, "Path");
                if (path.Length != 0)
                {
                    config.BankPaths.Add(path);
                    string lower = path.ToLowerInvariant();
                    if (insideTexturesGroup)
                    {
                        if (!lower.Contains("texture_headers", StringComparison.Ordinal) &&
                            !lower.Contains("1024mip0", StringComparison.Ordinal))
                        {
                            config.TextureBodyBanks.Add(path);
                        }
                    }
                    else if (lower.Contains("streaming", StringComparison.Ordinal))
                    {
                        config.StreamingBanks.Add(path);
                    }
                    else if (lower.Contains("model", StringComparison.Ordinal))
                    {
                        config.ModelBanks.Add(path);
                    }
                }
            }

            pos = gt + 1;
        }

        return config;
    }

    public static IEnumerable<string> CandidatePaths(LevelEntry level)
    {
        string world = level.World.ToLowerInvariant();
        string levelPath = level.LevelPath.ToLowerInvariant();
        string scenario =
            (level.Scenario.Length != 0
                ? level.Scenario
                : Engine_LevelManifest.DefaultScenario).ToLowerInvariant();

        yield return $"worlds/{world}/{levelPath}/{scenario}/{FileName}";
        yield return $"worlds/{world}/{levelPath}/{FileName}";
    }

    public static bool TryRead(
        LevelEntry level,
        Func<string, byte[]?> read,
        out LevelVfsConfig config,
        out string configPath)
    {
        config = new LevelVfsConfig();
        configPath = string.Empty;

        foreach (string candidate in CandidatePaths(level))
        {
            byte[]? bytes = read(candidate) ?? read("data/" + candidate);
            if (bytes is null)
            {
                continue;
            }

            config = Parse(System.Text.Encoding.ASCII.GetString(bytes));
            configPath = candidate;
            return true;
        }

        return false;
    }

    public static void Apply(LevelEntry level, Func<string, byte[]?> read)
    {
        if (!TryRead(level, read, out LevelVfsConfig config, out string path))
        {
            GD.Print($"[level] {level}: no {FileName}");
            return;
        }

        GD.Print(
            $"[level] {level}: {path} — {config.BankPaths.Count} bank(s) " +
            $"({config.TextureBodyBanks.Count} texture body, {config.ModelBanks.Count} model, " +
            $"{config.StreamingBanks.Count} streaming)");
    }

    private static bool StartsWithAt(string text, int at, string needle)
    {
        if (at + needle.Length > text.Length)
        {
            return false;
        }

        return string.Compare(
            text, at, needle, 0, needle.Length, StringComparison.OrdinalIgnoreCase) == 0;
    }

    private static string ExtractAttribute(string text, int tagOpen, int tagEnd, string name)
    {
        int pos = tagOpen + 1;
        while (pos < tagEnd)
        {
            if (StartsWithAt(text, pos, name) &&
                pos + name.Length < tagEnd &&
                (text[pos + name.Length] == '=' || text[pos + name.Length] == ' '))
            {
                pos += name.Length;
                while (pos < tagEnd && text[pos] == ' ')
                {
                    ++pos;
                }

                if (pos >= tagEnd || text[pos] != '=')
                {
                    return string.Empty;
                }

                ++pos;
                while (pos < tagEnd && text[pos] == ' ')
                {
                    ++pos;
                }

                if (pos >= tagEnd || text[pos] != '"')
                {
                    return string.Empty;
                }

                ++pos;
                int valueStart = pos;
                while (pos < tagEnd && text[pos] != '"')
                {
                    ++pos;
                }

                return pos >= tagEnd ? string.Empty : text[valueStart..pos];
            }

            ++pos;
        }

        return string.Empty;
    }
}
