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

    /// <summary>
    /// Where a title's installs, builds, install state and this file live: the app's data directory
    /// for Fable 2, as before there were titles, and a folder named for any other title inside it.
    /// Each title prunes only its own folder, so installing one never removes another's game or build.
    /// </summary>
    /// <param name="userDirectory">The app's data directory (<c>OS.GetUserDataDir</c>).</param>
    /// <param name="title">The title; the active one when null.</param>
    public static string TitleDirectory(string userDirectory, Refii.Core.Engine_Title? title = null)
    {
        title ??= Refii.Core.Engine_Title.Active;
        return title == Refii.Core.Engine_Title.Fable2 ? userDirectory : Path.Combine(userDirectory, title.Folder);
    }

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

/// <summary>Migrates an optional supplied native save tree into the active save profile, once.</summary>
/// <remarks>The user supplies a full content root (account/title/profile headers, in any layout the
/// native side has used). It is copied, never changed, through <see cref="Refii.Save.Engine_SaveMigration"/>:
/// nothing in the profile is overwritten, identical saves merge, past-the-slot-limit saves go to
/// Older saves, and the source is recorded so a later boot takes nothing from it again.</remarks>
public static class AndroidSaveImporter
{
    /// <summary>The outcome of an import without exposing any private save content.</summary>
    public sealed record Result(string Status, int Items);

    /// <summary>Copies what the supplied tree holds into the active profile under <paramref name="savesRoot"/>.</summary>
    /// <param name="source">Optional full supplied user_data_root directory.</param>
    /// <param name="savesRoot">The app's save folder, <c>runtime/assets/saves</c>.</param>
    /// <returns>A bounded summary of the import.</returns>
    public static Result ImportIfEmpty(string? source, string savesRoot)
    {
        string target = AndroidRuntimeOptions.PhysicalPath(savesRoot, "save destination");
        if (source == null) return new("no save import requested", 0);
        string from = AndroidRuntimeOptions.PhysicalPath(source, "save source");
        if (!Directory.Exists(from)) throw new DirectoryNotFoundException("Supplied save tree is missing: " + from);
        if (Contains(from, target) || Contains(target, from))
            throw new InvalidDataException("Save source and destination must be separate directory trees");
        RejectLink(from);
        Refii.Save.Engine_SaveMigration.Report report = Refii.Save.Engine_SaveMigration.Run(target, new[]
        {
            new Refii.Save.Engine_SaveMigration.Source(from, "supplied save tree", Owned: false, Refii.Core.Engine_Title.Fable2.TitleId),
        }, DateTime.Now);
        return report.Ran
            ? new("migrated supplied save tree: " + report.Summary, report.Copied)
            : new("supplied save tree already migrated", 0);
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
}
