using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Refii.Launcher.Core;

public static class Ini
{
    private static readonly Dictionary<string, Dictionary<string, string>> Sections = new(StringComparer.Ordinal);
    private static string _loadedPath = string.Empty;
    private static bool _loaded;

    public static void Load(string? path = null)
    {
        string resolved = path ?? LauncherPaths.GetConfigPath();
        LoadFromLines(
            File.Exists(resolved) ? File.ReadLines(resolved) : Array.Empty<string>(),
            resolved);
    }

    public static void LoadFromText(string text, string writePath)
    {
        LoadFromLines(text.Replace("\r\n", "\n").Split('\n'), writePath);
    }

    private static void LoadFromLines(IEnumerable<string> lines, string writePath)
    {
        _loadedPath = writePath;
        Sections.Clear();
        _loaded = true;

        string section = string.Empty;
        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line[0] == ';' || line[0] == '#')
            {
                continue;
            }

            if (line[0] == '[' && line[^1] == ']')
            {
                section = line.Substring(1, line.Length - 2).Trim();
                continue;
            }

            int equals = line.IndexOf('=');
            if (equals < 0)
            {
                continue;
            }

            string key = line.Substring(0, equals).Trim();
            string value = line[(equals + 1)..].Trim();
            if (key.Length == 0)
            {
                continue;
            }

            if (!Sections.TryGetValue(section, out Dictionary<string, string>? bucket))
            {
                bucket = new Dictionary<string, string>(StringComparer.Ordinal);
                Sections[section] = bucket;
            }

            bucket[key] = value;
        }
    }

    private static void EnsureLoaded()
    {
        if (!_loaded)
        {
            Load();
        }
    }

    public static string GetString(string key, string fallback, string section)
    {
        EnsureLoaded();
        if (Sections.TryGetValue(section, out Dictionary<string, string>? bucket) &&
            bucket.TryGetValue(key, out string? value) &&
            !string.IsNullOrEmpty(value))
        {
            return value;
        }

        return fallback;
    }

    public static IReadOnlyDictionary<string, string> GetSection(string section)
    {
        EnsureLoaded();
        return Sections.TryGetValue(section, out Dictionary<string, string>? bucket)
            ? bucket
            : new Dictionary<string, string>(StringComparer.Ordinal);
    }

    public static bool GetBool(string key, bool fallback, string section)
    {
        string value = GetString(key, fallback ? "true" : "false", section).ToLowerInvariant();
        return value is "1" or "true" or "yes" or "on";
    }

    public static int GetInt(string key, int fallback, string section)
    {
        string value = GetString(key, fallback.ToString(CultureInfo.InvariantCulture), section);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : fallback;
    }

    public static void SetString(string key, string value, string section)
    {
        EnsureLoaded();
        if (!Sections.TryGetValue(section, out Dictionary<string, string>? bucket))
        {
            bucket = new Dictionary<string, string>(StringComparer.Ordinal);
            Sections[section] = bucket;
        }

        bucket[key] = value;
    }

    public static void SetBool(string key, bool value, string section) =>
        SetString(key, value ? "true" : "false", section);

    public static void SetInt(string key, int value, string section) =>
        SetString(key, value.ToString(CultureInfo.InvariantCulture), section);

    public static void Save()
    {
        EnsureLoaded();
        string path = string.IsNullOrEmpty(_loadedPath) ? LauncherPaths.GetConfigPath() : _loadedPath;
        string? parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent!);
        }

        var builder = new StringBuilder();
        foreach ((string section, Dictionary<string, string> bucket) in Sections)
        {
            if (section.Length != 0)
            {
                builder.Append('[').Append(section).Append("]\n");
            }

            foreach ((string key, string value) in bucket)
            {
                builder.Append(key).Append('=').Append(value).Append('\n');
            }

            builder.Append('\n');
        }

        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
    }
}
