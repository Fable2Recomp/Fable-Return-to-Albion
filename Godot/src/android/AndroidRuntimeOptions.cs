using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Refii.AndroidRuntime;

/// <summary>Explicit on-disk inputs for the Android runtime.</summary>
/// <remarks>Content URIs cannot be read by the System.IO archive readers or native linker.
/// The document-picker installer copies and extracts into app-owned storage before writing this file.</remarks>
public sealed record AndroidRuntimeOptions(string GameRoot, string ModulePath, string? SaveImportRoot,
    bool SkipMovies, double MovieDwellSeconds)
{
    /// <summary>The private configuration filename, relative to Godot's user data directory.</summary>
    public const string FileName = "android-runtime.json";

    /// <summary>Reads a strict schema-one configuration without touching the supplied game or save.</summary>
    /// <param name="json">The user's configuration document.</param>
    /// <param name="userDirectory">The physical app-private directory supplied by Godot.</param>
    /// <returns>Normalized physical paths and explicit movie policy.</returns>
    public static AndroidRuntimeOptions Parse(string json, string userDirectory)
    {
        string user = PhysicalPath(userDirectory, "user directory");
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Android configuration must be an object");
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            if (property.Name is not ("schema" or "game_root" or "module_path" or "save_import_root" or
                "skip_movies" or "movie_dwell_seconds"))
                throw new InvalidDataException("Unknown Android configuration field: " + property.Name);
            if (!fields.TryAdd(property.Name, property.Value))
                throw new InvalidDataException("Duplicate Android configuration field: " + property.Name);
        }
        if (!fields.TryGetValue("schema", out var schema) || schema.ValueKind != JsonValueKind.Number ||
            !schema.TryGetInt32(out int version) || version != 1)
            throw new InvalidDataException("Android configuration requires schema 1");
        string game = StringField(fields, "game_root") ?? throw new InvalidDataException("game_root is required");
        string module = StringField(fields, "module_path") ?? Path.Combine(user, "module", "librefii_generated.so");
        string? save = StringField(fields, "save_import_root");
        bool skip = true;
        if (fields.TryGetValue("skip_movies", out var skipValue))
        {
            if (skipValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidDataException("skip_movies must be a boolean");
            skip = skipValue.GetBoolean();
        }
        double dwell = 2;
        if (fields.TryGetValue("movie_dwell_seconds", out var seconds) &&
            (seconds.ValueKind != JsonValueKind.Number || !seconds.TryGetDouble(out dwell) ||
                !double.IsFinite(dwell) || dwell < 0.5 || dwell > 60))
            throw new InvalidDataException("movie_dwell_seconds must be between 0.5 and 60");
        return new(PhysicalPath(game, "game_root"), PhysicalPath(module, "module_path"),
            save == null ? null : PhysicalPath(save, "save_import_root"), skip, dwell);
    }

    /// <summary>Reads an optional nonempty string without silently accepting mistyped JSON fields.</summary>
    private static string? StringField(Dictionary<string, JsonElement> fields, string name)
    {
        if (!fields.TryGetValue(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidDataException(name + " must be a nonempty string");
        return value.GetString();
    }

    /// <summary>Rejects resource/content URIs and relative paths before native or System.IO consumers see them.</summary>
    internal static string PhysicalPath(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains("://", StringComparison.Ordinal) ||
            !Path.IsPathFullyQualified(value))
            throw new InvalidDataException(name + " must be an absolute filesystem path");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
    }
}

/// <summary>Copies an optional complete native save tree into an empty private destination.</summary>
/// <remarks>The user supplies the full user_data_root, including account/title/profile headers.
/// Existing saves always win. A fresh staging directory makes failed imports non-destructive;
/// partial staging is retained on failure for inspection rather than deleting unknown files.</remarks>
public static class AndroidSaveImporter
{
    /// <summary>The outcome of an import without exposing any private save content.</summary>
    public sealed record Result(string Status, int Files, long Bytes);

    /// <summary>Preserves existing private saves or commits a staged byte-for-byte copy.</summary>
    /// <param name="source">Optional full supplied user_data_root directory.</param>
    /// <param name="destination">The new runtime's private user_data_root.</param>
    /// <returns>A bounded summary of the import.</returns>
    public static Result ImportIfEmpty(string? source, string destination)
    {
        string target = AndroidRuntimeOptions.PhysicalPath(destination, "save destination");
        if (Directory.Exists(target))
        {
            var entries = Directory.EnumerateFileSystemEntries(target).GetEnumerator();
            using (entries) if (entries.MoveNext()) return new("existing private saves preserved", 0, 0);
        }
        if (source == null) return new("no save import requested", 0, 0);
        string from = AndroidRuntimeOptions.PhysicalPath(source, "save source");
        if (!Directory.Exists(from)) throw new DirectoryNotFoundException("Supplied save tree is missing: " + from);
        if (Contains(from, target) || Contains(target, from))
            throw new InvalidDataException("Save source and destination must be separate directory trees");
        RejectLink(from);
        string staging = target + ".import-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(staging);
        int files = 0;
        long bytes = 0;
        CopyDirectory(from, staging, ref files, ref bytes);
        if (files == 0) throw new InvalidDataException("Supplied save tree contains no files; staging retained: " + staging);
        // Delete only the known empty destination. Never remove an existing profile recursively.
        if (Directory.Exists(target)) Directory.Delete(target, recursive: false);
        Directory.Move(staging, target);
        return new("copied supplied save tree", files, bytes);
    }

    /// <summary>Checks path containment using the host filesystem's case rules.</summary>
    private static bool Contains(string parent, string child)
    {
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(parent, child, comparison) ||
            child.StartsWith(parent + Path.DirectorySeparatorChar, comparison);
    }

    /// <summary>Refuses symlink/reparse traversal so private imports cannot escape their supplied tree.</summary>
    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Save import does not follow symbolic links: " + path);
    }

    /// <summary>Streams ordinary files while preserving the complete relative native save hierarchy.</summary>
    private static void CopyDirectory(string source, string destination, ref int files, ref long bytes)
    {
        foreach (string entry in Directory.EnumerateFileSystemEntries(source))
        {
            RejectLink(entry);
            string output = Path.Combine(destination, Path.GetFileName(entry));
            if (Directory.Exists(entry))
            {
                Directory.CreateDirectory(output);
                CopyDirectory(entry, output, ref files, ref bytes);
            }
            else
            {
                File.Copy(entry, output, overwrite: false);
                files++;
                bytes += new FileInfo(output).Length;
            }
        }
    }
}
