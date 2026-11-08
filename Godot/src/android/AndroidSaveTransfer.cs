using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Refii.AndroidRuntime;

/// <summary>
/// Import save: restores the active save profile from an export that holds saves (<see cref="AndroidLogBundle"/>),
/// picked in Android's document picker, after writing the current profile to Download as a backup export.
/// </summary>
/// <remarks>
/// <para>Only from the launcher, before the game has started in this process
/// (<see cref="GameStarted"/>): the running game holds the save folder and would write over an
/// import, or read half of one.</para>
/// <para>Two layouts import. An export of the save profiles has its entries under
/// <c>files/runtime/assets/saves/&lt;profile&gt;/</c> and replaces the profile's games whole. One made
/// before them has a single content root under <c>files/runtime/assets/save/</c>; it is migrated into a
/// fresh profile (<see cref="Refii.Save.Engine_SaveMigration.MigrateInto"/>) first, so its saves land in
/// the title folders and past-the-slot-limit saves go to Older saves.</para>
/// <para>Every entry is checked before anything changes: only paths under the export's save root,
/// no empty, <c>.</c> or <c>..</c> segment, no absolute or backslashed path, no duplicate, sizes within
/// <see cref="MaxFileBytes"/> and <see cref="MaxTotalBytes"/>, and at least one hero's save header. The
/// zip's other entries (device.txt, logs, the manifest) are left out, and so are any <c>rexnet/</c> and
/// <c>.profile</c>: the phone keeps its own RexNet identity, friends and profile id, which an export
/// never carries.</para>
/// <para>The save is extracted into a fresh staging folder in the save root, then swapped in;
/// a failure before the swap leaves the current profile untouched, and one during it puts it back.</para>
/// </remarks>
public static class AndroidSaveTransfer
{
    /// <summary>The prefix of a save file's name in an export of the save profiles.</summary>
    public const string SaveRoot = "files/" + AndroidLogBundle.SaveFolder + "/";

    /// <summary>The prefix of a save file's name in an export made before the save profiles.</summary>
    public const string LegacySaveRoot = "files/" + AndroidLogBundle.LegacySaveFolder + "/";

    /// <summary>The largest single save file accepted (a hero's saves are a few MB).</summary>
    public const long MaxFileBytes = 64L * 1024 * 1024;

    /// <summary>The largest save accepted in all.</summary>
    public const long MaxTotalBytes = 512L * 1024 * 1024;

    /// <summary>The most save files accepted.</summary>
    public const int MaxFiles = 20000;

    /// <summary>The label the backup export an import writes first is named by.</summary>
    public const string BackupLabel = "saves-before-import";

    /// <summary>Set once the native game session starts; an import is refused from then until the app restarts.</summary>
    public static volatile bool GameStarted;

    /// <summary>A hero's save header in a profile: <c>&lt;game&gt;/Headers/&lt;type&gt;/HeroNNN.header</c>.</summary>
    private static readonly Regex HeroHeader = new(
        @"\A[^/]+/Headers/[0-9A-Fa-f]{8}/[^/]+\.header\z", RegexOptions.CultureInvariant);

    /// <summary>A hero's save header in a legacy content root: <c>&lt;xuid&gt;/&lt;title&gt;/Headers/&lt;type&gt;/HeroNNN.header</c>.</summary>
    private static readonly Regex LegacyHeroHeader = new(
        @"\A[0-9A-Fa-f]{16}/[0-9A-Fa-f]{8}/Headers/[0-9A-Fa-f]{8}/[^/]+\.header\z", RegexOptions.CultureInvariant);

    /// <summary>What a valid export's save holds.</summary>
    /// <param name="Files">Save files.</param>
    /// <param name="Bytes">Their size in all.</param>
    /// <param name="Heroes">Hero save headers among them.</param>
    /// <param name="Legacy">Whether it is an export made before the save profiles.</param>
    public sealed record Inspection(int Files, long Bytes, int Heroes, bool Legacy = false);

    /// <summary>The outcome of an import.</summary>
    /// <param name="Imported">What was restored.</param>
    /// <param name="Backup">The backup export's path, or null when there was no save to back up.</param>
    public sealed record Result(Inspection Imported, string? Backup);

    /// <summary>
    /// A save entry's path inside the profile (below the exporting phone's profile folder), or null
    /// when the entry is not a save file of an export of the save profiles.
    /// </summary>
    /// <param name="entryName">The zip entry's full name.</param>
    /// <exception cref="InvalidDataException">The name is under the save root but unsafe to extract.</exception>
    public static string? SaveRelativePath(string entryName)
    {
        if (Checked(entryName, SaveRoot) is not { } relative) return null;
        int slash = relative.IndexOf('/');
        if (slash <= 0) return null;
        string inside = relative[(slash + 1)..];
        if (inside == AndroidLogBundle.PrivateSaveFile || inside.Split('/')[0] == AndroidLogBundle.PrivateSaveFolder) return null;
        return inside;
    }

    /// <summary>A save entry's path inside an export made before the save profiles, or null.</summary>
    /// <param name="entryName">The zip entry's full name.</param>
    /// <exception cref="InvalidDataException">The name is under the save root but unsafe to extract.</exception>
    public static string? LegacyRelativePath(string entryName)
    {
        if (Checked(entryName, LegacySaveRoot) is not { } relative) return null;
        if (relative.Split('/')[0] == AndroidLogBundle.PrivateSaveFolder) return null;
        return relative;
    }

    /// <summary>The entry's path below <paramref name="root"/>, checked safe; null when it is not below it or names a folder.</summary>
    private static string? Checked(string entryName, string root)
    {
        if (!entryName.StartsWith(root, StringComparison.Ordinal)) return null;
        string relative = entryName[root.Length..];
        if (relative.Length == 0 || relative.EndsWith('/')) return null;
        if (relative.Contains('\\') || relative.Contains(':') || relative.StartsWith('/') || relative.Length > 400)
            throw new InvalidDataException("The export has an unsafe file name: " + entryName);
        foreach (string segment in relative.Split('/'))
        {
            if (segment.Length == 0 || segment == "." || segment == ".." || segment.Length > 200)
                throw new InvalidDataException("The export has an unsafe file name: " + entryName);
        }
        return relative;
    }

    /// <summary>Whether <paramref name="zip"/> is an export made before the save profiles: it has no entry under <see cref="SaveRoot"/>.</summary>
    private static bool IsLegacy(ZipArchive zip) =>
        !zip.Entries.Any(entry => entry.FullName.StartsWith(SaveRoot, StringComparison.Ordinal));

    private static string? RelativePath(string entryName, bool legacy) =>
        legacy ? LegacyRelativePath(entryName) : SaveRelativePath(entryName);

    /// <summary>Checks every save entry of <paramref name="zip"/> without extracting anything.</summary>
    /// <exception cref="InvalidDataException">The zip holds no save, or an unsafe or oversized one; the message says which.</exception>
    public static Inspection Inspect(ZipArchive zip)
    {
        bool legacy = IsLegacy(zip);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var profiles = new HashSet<string>(StringComparer.Ordinal);
        int files = 0, heroes = 0;
        long bytes = 0;
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            if (RelativePath(entry.FullName, legacy) is not { } relative) continue;
            if (!legacy) profiles.Add(entry.FullName[SaveRoot.Length..].Split('/')[0]);
            if (!seen.Add(relative)) throw new InvalidDataException("The export has a save file twice: " + relative);
            if (entry.Length < 0 || entry.Length > MaxFileBytes)
                throw new InvalidDataException("The export has a save file too large to be one: " + relative);
            files++;
            bytes += entry.Length;
            if (files > MaxFiles || bytes > MaxTotalBytes)
                throw new InvalidDataException("The export's save is larger than any Fable save.");
            if ((legacy ? LegacyHeroHeader : HeroHeader).IsMatch(relative)) heroes++;
        }
        if (files == 0)
            throw new InvalidDataException("This export has no save in it. Export \"Saves\" or \"Logs and saves\" to make one that can be imported.");
        if (profiles.Count > 1)
            throw new InvalidDataException("This export holds more than one save profile.");
        if (heroes == 0)
            throw new InvalidDataException("This export's save folder has no hero's save in it.");
        return new Inspection(files, bytes, heroes, legacy);
    }

    /// <summary>
    /// Restores the active save profile under <paramref name="userDirectory"/> from the export at <paramref name="zipPath"/>,
    /// calling <paramref name="backup"/> first when there is a save to replace.
    /// </summary>
    /// <param name="zipPath">A local copy of the export.</param>
    /// <param name="userDirectory">The app's data directory.</param>
    /// <param name="backup">Writes the current save as an export and returns its path; a throw stops the import before anything changes.</param>
    /// <exception cref="InvalidOperationException">The game has started in this process.</exception>
    /// <exception cref="InvalidDataException">The export is not one that can be imported.</exception>
    public static Result Import(string zipPath, string userDirectory, Func<string> backup)
    {
        if (GameStarted)
            throw new InvalidOperationException("The game has already started. Close the app, open it again and import from the launcher.");
        string root = AndroidLogBundle.SavesRoot(userDirectory);
        using ZipArchive zip = ZipFile.OpenRead(zipPath);
        Inspection inspection = Inspect(zip);
        string profile = Refii.Save.Engine_SaveProfiles.Ensure(root);

        string staging = Path.Combine(root, ".import-" + Guid.NewGuid().ToString("N"));
        string extracted = inspection.Legacy ? Path.Combine(staging, "legacy") : Path.Combine(staging, "profile");
        Directory.CreateDirectory(extracted);
        try
        {
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                if (RelativePath(entry.FullName, inspection.Legacy) is not { } relative) continue;
                string target = Path.GetFullPath(Path.Combine(extracted, relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!target.StartsWith(extracted + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    throw new InvalidDataException("The export has an unsafe file name: " + entry.FullName);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using Stream source = entry.Open();
                using var output = new FileStream(target, FileMode.CreateNew, System.IO.FileAccess.Write);
                CopyBounded(source, output, MaxFileBytes, relative);
            }

            string incoming = Path.Combine(staging, "profile");
            if (inspection.Legacy)
            {
                Refii.Save.Engine_SaveMigration.MigrateInto(incoming, new[]
                {
                    new Refii.Save.Engine_SaveMigration.Source(extracted, "imported", Owned: true, Refii.Core.Engine_Title.Fable2.TitleId),
                }, root, DateTime.Now);
            }

            string? backupPath = HasSaveFiles(profile) ? backup() : null;
            Swap(profile, incoming, root);
            if (Directory.Exists(staging)) TryDelete(staging);
            return new Result(inspection, backupPath);
        }
        catch
        {
            if (Directory.Exists(staging)) TryDelete(staging);
            throw;
        }
    }

    /// <summary>
    /// Copies the picked document into the app's cache, imports it on a worker and returns the line
    /// to show: what was imported and where the backup is, or why not. Never throws.
    /// </summary>
    /// <param name="uri">The document picker's content URI.</param>
    public static async Task<string> ImportAsync(string uri)
    {
        string user = OS.GetUserDataDir();
        string copy = Path.Combine(user, "cache", "import-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            if (GameStarted)
                return "Save not imported: the game has already started. Close the app, open it again and import from the launcher.";
            string downloads = OS.GetSystemDir(OS.SystemDir.Downloads);
            if (string.IsNullOrWhiteSpace(downloads)) return "Save not imported: this phone has no Download folder for the backup.";
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            CopyDocument(uri, copy);
            DateTime now = AndroidLogBundle.LocalNow();
            string device = AndroidLogBundle.DescribeDevice();
            GD.Print("[export] importing a save from " + uri);
            Result result = await Task.Run(() => Import(copy, user, () => AndroidLogBundle.Write(
                downloads, user, device, now, contents: AndroidLogBundle.Contents.Saves, label: BackupLabel)));
            GD.Print($"[export] imported {result.Imported.Files} save files ({result.Imported.Heroes} heroes); backup {result.Backup ?? "none"}");
            string heroes = result.Imported.Heroes == 1 ? "1 hero" : result.Imported.Heroes + " heroes";
            return result.Backup is { } backup
                ? $"Save imported ({heroes}). Your previous save is in Download/{Path.GetFileName(backup)}."
                : $"Save imported ({heroes}).";
        }
        catch (Exception error)
        {
            GD.PushWarning("[export] save not imported: " + error.Message);
            return "Save not imported: " + error.Message;
        }
        finally
        {
            try { if (File.Exists(copy)) File.Delete(copy); } catch (Exception) { }
        }
    }

    /// <summary>Streams a picked document to <paramref name="target"/> through Godot, which reads content URIs.</summary>
    private static void CopyDocument(string uri, string target)
    {
        using var source = Godot.FileAccess.Open(uri, Godot.FileAccess.ModeFlags.Read)
            ?? throw new IOException("Cannot read the picked file (" + Godot.FileAccess.GetOpenError() + ")");
        ulong length = source.GetLength();
        // The zip also carries the logs, which may be larger than the save.
        if (length == 0 || length > (ulong)(MaxTotalBytes + 1024L * 1024 * 1024))
            throw new InvalidDataException("The picked file has an unsupported size");
        using var output = new FileStream(target, FileMode.CreateNew, System.IO.FileAccess.Write);
        while (source.GetPosition() < length)
        {
            byte[] data = source.GetBuffer((long)Math.Min(1024UL * 1024, length - source.GetPosition()));
            if (data.Length == 0) throw new EndOfStreamException("The picked file ended early");
            output.Write(data);
        }
    }

    /// <summary>Copies at most <paramref name="limit"/> bytes, failing when the entry holds more than its header said it would.</summary>
    private static void CopyBounded(Stream source, Stream output, long limit, string name)
    {
        byte[] buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > limit) throw new InvalidDataException("The export has a save file too large to be one: " + name);
            output.Write(buffer, 0, read);
        }
    }

    /// <summary>Moves <paramref name="staging"/> into place as <paramref name="profile"/>, carrying the phone's own <c>rexnet/</c> and <c>.profile</c> across.</summary>
    /// <remarks>The current profile is set aside first and put back if any step fails; it is deleted only once the new one stands.</remarks>
    private static void Swap(string profile, string staging, string root)
    {
        string? replaced = null;
        string[] own = { AndroidLogBundle.PrivateSaveFolder, AndroidLogBundle.PrivateSaveFile };
        try
        {
            if (Directory.Exists(profile))
            {
                replaced = Path.Combine(root, ".replaced-" + Guid.NewGuid().ToString("N"));
                Directory.Move(profile, replaced);
                foreach (string name in own) MoveEntry(Path.Combine(replaced, name), Path.Combine(staging, name));
            }
            Directory.Move(staging, profile);
        }
        catch
        {
            if (replaced != null && Directory.Exists(replaced) && !Directory.Exists(profile))
            {
                foreach (string name in own)
                {
                    if (!Exists(Path.Combine(replaced, name))) MoveEntry(Path.Combine(staging, name), Path.Combine(replaced, name));
                }
                Directory.Move(replaced, profile);
            }
            throw;
        }
        if (replaced != null) TryDelete(replaced);
    }

    private static bool Exists(string path) => Directory.Exists(path) || File.Exists(path);

    private static void MoveEntry(string from, string to)
    {
        if (Directory.Exists(from)) Directory.Move(from, to);
        else if (File.Exists(from)) File.Move(from, to);
    }

    /// <summary>Whether the profile holds any file but its own identity and id: a save worth backing up.</summary>
    private static bool HasSaveFiles(string profile)
    {
        if (!Directory.Exists(profile)) return false;
        foreach (string file in Directory.EnumerateFiles(profile, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(profile, file).Replace(Path.DirectorySeparatorChar, '/');
            if (relative == AndroidLogBundle.PrivateSaveFile || relative.StartsWith(AndroidLogBundle.PrivateSaveFolder + "/", StringComparison.Ordinal))
                continue;
            return true;
        }
        return false;
    }

    private static void TryDelete(string folder)
    {
        try { Directory.Delete(folder, recursive: true); }
        catch (Exception error) { GD.PushWarning("[export] could not remove " + folder + ": " + error.Message); }
    }
}
