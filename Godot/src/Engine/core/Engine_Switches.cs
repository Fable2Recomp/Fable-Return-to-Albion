using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Godot;

namespace Refii.Core;

public static class Engine_Switches
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, (string Value, string Source)> Seen =
        new(StringComparer.OrdinalIgnoreCase);
    private static IReadOnlyDictionary<string, string>? _file;
    private static string _filePath = string.Empty;

    public static int Generation { get; private set; }

    public static void Invalidate()
    {
        lock (Gate)
        {
            _file = null;
            _filePath = string.Empty;
            Generation++;
        }
    }

    public static bool Flag(string key, bool fallback)
    {
        string? raw = Raw(key, out string source);
        if (raw is null)
        {
            Record(key, fallback ? "true" : "false", source);
            return fallback;
        }

        string text = raw.Trim().Trim('"');
        bool value = fallback;
        if (text.Equals("true", StringComparison.OrdinalIgnoreCase) || text == "1")
        {
            value = true;
        }
        else if (text.Equals("false", StringComparison.OrdinalIgnoreCase) || text == "0")
        {
            value = false;
        }

        Record(key, value ? "true" : "false", source);
        return value;
    }

    public static int Int(string key, int fallback)
    {
        string? raw = Raw(key, out string source);
        int value = raw is not null &&
            int.TryParse(raw.Trim().Trim('"'), NumberStyles.Integer, CultureInfo.InvariantCulture,
                out int parsed)
            ? parsed
            : fallback;
        Record(key, value.ToString(CultureInfo.InvariantCulture), raw is null ? "default" : source);
        return value;
    }

    public static float Float(string key, float fallback)
    {
        string? raw = Raw(key, out string source);
        float value = raw is not null &&
            float.TryParse(raw.Trim().Trim('"'), NumberStyles.Float, CultureInfo.InvariantCulture,
                out float parsed) && float.IsFinite(parsed)
            ? parsed
            : fallback;
        Record(key, value.ToString(CultureInfo.InvariantCulture), raw is null ? "default" : source);
        return value;
    }

    public static string String(string key, string fallback)
    {
        string? raw = Raw(key, out string source);
        string value = raw is null ? fallback : raw.Trim().Trim('"');
        Record(key, value, raw is null ? "default" : source);
        return value;
    }

    public static string Manifest()
    {
        lock (Gate)
        {
            var text = new StringBuilder();
            text.Append("[switches] ").Append(Seen.Count).Append(" read");
            if (_filePath.Length != 0)
            {
                text.Append(" (toml: ").Append(_filePath).Append(')');
            }

            foreach (KeyValuePair<string, (string Value, string Source)> entry in Seen)
            {
                text.Append("\n  ").Append(entry.Key).Append('=').Append(entry.Value.Value)
                    .Append(" [").Append(entry.Value.Source).Append(']');
            }

            return text.ToString();
        }
    }

    private static string? Raw(string key, out string source)
    {
        string env = "REFII_" + key.Replace('-', '_').ToUpperInvariant();
        string fromEnv = OS.GetEnvironment(env);
        if (fromEnv.Length != 0)
        {
            source = "env";
            return fromEnv;
        }

        IReadOnlyDictionary<string, string> file = File();
        if (file.TryGetValue(key, out string? fromFile))
        {
            source = "toml";
            return fromFile;
        }

        source = "default";
        return null;
    }

    private static IReadOnlyDictionary<string, string> File()
    {
        lock (Gate)
        {
            if (_file is null)
            {
                _file = Engine_RefiiConfig.Read(out _filePath);
            }

            return _file;
        }
    }

    private static void Record(string key, string value, string source)
    {
        lock (Gate)
        {
            Seen[key] = (value, source);
        }
    }
}
