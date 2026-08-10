using System;

namespace Refii.Level;

/// <summary>
/// The scenario manifest — the file that says what a level is made of.
///
/// `Level_BuildDataWorldsFilePath` (0x8250EED0) builds
/// <c>Data\Worlds\&lt;World&gt;\&lt;Level&gt;\&lt;Scenario&gt;\&lt;Scenario&gt;.list</c>, and
/// `Level_ParseLevelManifestByExtension` (0x8250F168) buckets its lines by extension and hands
/// each bucket to a loader. The heightfield bucket is taken wholesale — the loader does no
/// selection of its own — so the manifest, not the file name, is what decides which map and
/// which placements a level has.
///
/// One line per file, backslash-separated, case as authored.
/// </summary>
public static class Engine_LevelManifest
{
    /// <summary>Reads the manifest for a level's active scenario, or null when there is none.</summary>
    public static string? TryRead(
        LevelEntry level, Func<string, byte[]?> read, out string manifestPath)
    {
        manifestPath = string.Empty;
        if (level.Scenario.Length == 0)
        {
            return null;
        }

        string scenario = level.Scenario.ToLowerInvariant();
        string path =
            $"worlds/{level.World.ToLowerInvariant()}/{level.Name.ToLowerInvariant()}/" +
            $"{scenario}/{scenario}.list";

        byte[]? bytes = read(path) ?? read("data/" + path);
        if (bytes is null)
        {
            return null;
        }

        manifestPath = path;
        return System.Text.Encoding.ASCII.GetString(bytes);
    }

    /// <summary>
    /// The first file the manifest names with the given extension, as a forward-slashed
    /// lowercase virtual path.
    /// </summary>
    public static bool TryFind(
        LevelEntry level, Func<string, byte[]?> read, string extension, out string found)
    {
        found = string.Empty;

        string? text = TryRead(level, read, out _);
        if (text is null)
        {
            return false;
        }

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim().Replace('\\', '/');
            if (line.Length == 0 ||
                !line.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            found = line.ToLowerInvariant();
            return true;
        }

        return false;
    }
}
