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
        var candidates = new List<string>();

        try
        {
            candidates.Add(Installer.GetRuntimeConfigPath());
        }
        catch (Exception)
        {
            // The launcher's path config is not available; the conventional ones still are.
        }

        string project = ProjectSettings.GlobalizePath("res://");
        candidates.Add(System.IO.Path.Combine(project, "build", "refii.toml"));
        candidates.Add(System.IO.Path.Combine(project, "refii.toml"));
        candidates.Add(System.IO.Path.GetFullPath(
            System.IO.Path.Combine(project, "..", "refii.toml")));

        foreach (string candidate in candidates)
        {
            if (!string.IsNullOrEmpty(candidate) && System.IO.File.Exists(candidate))
            {
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

        try
        {
            return RuntimeConfig.ReadValues(path);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"[config] could not read {path}: {exception.Message}");
            return new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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
