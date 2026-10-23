using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using Refii.Launcher.Core;

namespace Refii.Core;

/// <summary>
/// Finds and reads <c>refii.toml</c>, the one file a player edits.
/// </summary>
/// <remarks>
/// The locating used to live privately inside <see cref="Refii.Game.GameWindowSettings"/>, which
/// was fine while the window was the only thing configured from the file. It is here now because
/// the texture pipeline needs the same file, and a second copy of the search order is a second
/// thing to keep in agreement — the failure mode being a settings panel that offers a key the
/// engine looks for somewhere the launcher does not write.
///
/// BY CONVENTION, NOT BY ASKING THE LAUNCHER. <see cref="Installer.GetRuntimeConfigPath"/>
/// resolves against a directory the launcher sets at startup, so anything that runs without the
/// launcher — a debug scene, the editor, a headless tool — silently gets defaults with no
/// indication the file was never read. The staged build directory and the repository root are
/// where the file actually lives, so they are searched too.
/// </remarks>
public static class Engine_RefiiConfig
{
    /// <summary>The path to <c>refii.toml</c>, or empty when there is not one.</summary>
    public static string ResolvePath()
    {
        lock (_cacheGate)
        {
            if (_cachedPath.Length != 0)
            {
                return _cachedPath;
            }
        }

        var candidates = new List<string>(4);

        try
        {
            candidates.Add(Installer.GetRuntimeConfigPath());
        }
        catch (Exception)
        {
            // The launcher's path config is not available; the conventional ones still are.
        }

        string project = "";
        if (!AppDomain.CurrentDomain.FriendlyName.Contains("testhost", StringComparison.OrdinalIgnoreCase) &&
            !System.Environment.CommandLine.Contains("testhost", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                project = ProjectSettings.GlobalizePath("res://");
            }
            catch (Exception)
            {
            }
        }

        if (string.IsNullOrEmpty(project))
        {
            string? dir = AppContext.BaseDirectory;
            while (!string.IsNullOrEmpty(dir) && !System.IO.File.Exists(System.IO.Path.Combine(dir, "project.godot")))
            {
                string? parent = System.IO.Path.GetDirectoryName(dir);
                if (parent == dir) break;
                dir = parent;
            }
            if (!string.IsNullOrEmpty(dir)) project = dir;
        }

        candidates.Add(System.IO.Path.Combine(project, "build", "refii.toml"));
        candidates.Add(System.IO.Path.Combine(project, "refii.toml"));
        candidates.Add(System.IO.Path.GetFullPath(
            System.IO.Path.Combine(project, "..", "refii.toml")));

        foreach (string candidate in candidates)
        {
            if (!string.IsNullOrEmpty(candidate) && System.IO.File.Exists(candidate))
            {
                lock (_cacheGate)
                {
                    _cachedPath = candidate;
                }
                return candidate;
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Every key in the file, or an empty set when it is missing or unreadable.
    /// </summary>
    /// <remarks>
    /// <paramref name="path"/> comes back so a caller can say in its log line WHICH file the
    /// values came from. "settings had no effect" is nearly always the wrong file being read, and
    /// a log that names the path settles it in one line.
    /// </remarks>
    public static IReadOnlyDictionary<string, string> Read(out string path)
    {
        path = ResolvePath();
        if (path.Length == 0)
        {
            return new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        // Parsed once per change, keyed on path and write time; readers ask per second or per
        // relight.
        DateTime stamp;
        try
        {
            stamp = System.IO.File.GetLastWriteTimeUtc(path);
        }
        catch (Exception)
        {
            stamp = DateTime.MinValue;
        }

        lock (_cacheGate)
        {
            if (_cached is not null && _cachedPath == path && _cachedStamp == stamp)
            {
                return ApplyOverrides(_cached);
            }
        }

        IReadOnlyDictionary<string, string> values;
        try
        {
            values = RuntimeConfig.ReadValues(path);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"[config] could not read {path}: {exception.Message}");
            return ApplyOverrides(new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        }

        lock (_cacheGate)
        {
            _cached = values;
            _cachedPath = path;
            _cachedStamp = stamp;
        }

        Engine_Switches.Invalidate();
        return ApplyOverrides(values);
    }

    /// <summary>
    /// Throttled check (at most once per 500 ms) that reloads <c>refii.toml</c> and invalidates
    /// <see cref="Engine_Switches.Generation"/> if the file was modified on disk (for example, by
    /// the launcher's lighting settings dialog while the game is running).
    /// </summary>
    public static void PollReload()
    {
        long now = System.Environment.TickCount64;
        long last = System.Threading.Interlocked.Read(ref _lastPollTick);
        if (now - last < 500)
        {
            return;
        }

        System.Threading.Interlocked.Exchange(ref _lastPollTick, now);
        _ = Read(out _);
    }

    private static readonly object _cacheGate = new();
    private static IReadOnlyDictionary<string, string>? _cached;
    private static string _cachedPath = string.Empty;
    private static DateTime _cachedStamp;
    private static long _lastPollTick = long.MinValue;
    private static readonly SortedDictionary<string, string> _overrides = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Invoked on the main thread or caller thread whenever a Godot CVar is updated live via
    /// <see cref="TrySetGodotCvar"/>.
    /// </summary>
    public static event Action<string, string>? OnConfigChanged;

    private static IReadOnlyDictionary<string, string> ApplyOverrides(IReadOnlyDictionary<string, string> baseValues)
    {
        lock (_cacheGate)
        {
            if (_overrides.Count == 0)
            {
                return baseValues;
            }

            var merged = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach ((string k, string v) in baseValues)
            {
                merged[k] = v;
            }
            foreach ((string k, string v) in _overrides)
            {
                merged[k] = v;
            }
            return merged;
        }
    }

    /// <summary>
    /// Sets a live Godot CVar override in memory (and persists to <c>refii.toml</c> when possible),
    /// then notifies listeners such as <c>Engine_LevelLights</c> and <c>Engine_LevelLighting</c>.
    /// </summary>
    public static bool TrySetGodotCvar(string name, string value)
    {
        RuntimeCVarDefinition? def = null;
        foreach (RuntimeCVarDefinition candidate in RuntimeCVarsGodot.Definitions)
        {
            if (candidate.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                def = candidate;
                break;
            }
        }

        if (def is null)
        {
            return false;
        }

        string trimmed = value.Trim().Trim('"');
        switch (def.Type)
        {
            case RuntimeCVarType.Bool:
                if (!trimmed.Equals("true", StringComparison.OrdinalIgnoreCase) &&
                    !trimmed.Equals("false", StringComparison.OrdinalIgnoreCase) &&
                    trimmed != "1" && trimmed != "0")
                {
                    return false;
                }
                trimmed = (trimmed.Equals("true", StringComparison.OrdinalIgnoreCase) || trimmed == "1")
                    ? "true"
                    : "false";
                break;
            case RuntimeCVarType.Int:
                if (!int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                {
                    return false;
                }
                break;
            case RuntimeCVarType.Double:
                if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ||
                    !double.IsFinite(d))
                {
                    return false;
                }
                break;
        }

        lock (_cacheGate)
        {
            _overrides[def.Name] = trimmed;
        }

        Engine_Switches.Invalidate();
        OnConfigChanged?.Invoke(def.Name, trimmed);
        return true;
    }

    public static IEnumerable<(RuntimeCVarDefinition Def, string Value, bool Modified)> GetGodotCvars()
    {
        IReadOnlyDictionary<string, string> current = Read(out _);
        foreach (RuntimeCVarDefinition def in RuntimeCVarsGodot.Definitions)
        {
            string val = current.TryGetValue(def.Name, out string? raw) && raw.Length > 0
                ? raw.Trim().Trim('"')
                : def.DefaultValue;
            bool modified = !val.Equals(def.DefaultValue, StringComparison.OrdinalIgnoreCase);
            yield return (def, val, modified);
        }
    }

    public static int Int(IReadOnlyDictionary<string, string> values, string key, int fallback) =>
        values.TryGetValue(key, out string? raw) &&
        int.TryParse(raw.Trim().Trim('"'), NumberStyles.Integer, CultureInfo.InvariantCulture,
            out int parsed)
            ? parsed
            : fallback;

    public static float Float(
        IReadOnlyDictionary<string, string> values, string key, float fallback) =>
        values.TryGetValue(key, out string? raw) &&
        float.TryParse(raw.Trim().Trim('"'), NumberStyles.Float, CultureInfo.InvariantCulture,
            out float parsed) && float.IsFinite(parsed)
            ? parsed
            : fallback;

    /// <summary>
    /// A boolean, where anything the file does not clearly say is <c>false</c> counts as true.
    /// </summary>
    /// <remarks>
    /// The quote stripping is not cosmetic. The launcher writes strings quoted and bools bare,
    /// and a player editing by hand does either — <c>"true"</c> read literally is not
    /// <c>true</c>, and the setting would silently take its fallback.
    /// </remarks>
    public static bool Bool(
        IReadOnlyDictionary<string, string> values, string key, bool fallback)
    {
        if (!values.TryGetValue(key, out string? raw))
        {
            return fallback;
        }

        string text = raw.Trim().Trim('"');
        if (text.Equals("true", StringComparison.OrdinalIgnoreCase) || text == "1")
        {
            return true;
        }

        if (text.Equals("false", StringComparison.OrdinalIgnoreCase) || text == "0")
        {
            return false;
        }

        return fallback;
    }

    public static string String(
        IReadOnlyDictionary<string, string> values, string key, string fallback) =>
        values.TryGetValue(key, out string? raw) && raw.Trim().Trim('"').Length > 0
            ? raw.Trim().Trim('"')
            : fallback;
}
