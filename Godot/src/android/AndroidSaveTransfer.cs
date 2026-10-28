using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Refii.AndroidRuntime;

/// <summary>
/// Import save: restores the save folder from an export that holds saves (<see cref="AndroidLogBundle"/>),
/// picked in Android's document picker, after writing the current save to Download as a backup export.
/// </summary>
/// <remarks>
/// <para>Only from the launcher, before the game has started in this process
/// (<see cref="GameStarted"/>): the running game holds the save folder and would write over an
/// import, or read half of one.</para>
/// <para>Every entry is checked before anything changes: only paths under the export's save root,
/// no empty, <c>.</c> or <c>..</c> segment, no absolute or backslashed path, no duplicate, sizes within
/// <see cref="MaxFileBytes"/> and <see cref="MaxTotalBytes"/>, and at least one hero's save header. The
/// zip's other entries (device.txt, logs, the manifest) are left out, and so is any <c>rexnet/</c>:
/// the phone keeps its own RexNet identity and friends, which an export never carries.</para>
/// <para>The save is extracted into a fresh staging folder beside the save folder, then swapped in;
/// a failure before the swap leaves the current save untouched, and one during it puts it back.</para>
/// </remarks>
public static class AndroidSaveTransfer
{
    /// <summary>The prefix of a save file's name in an export.</summary>
    public const string SaveRoot = "files/" + AndroidLogBundle.SaveFolder + "/";

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

    /// <summary>A hero's save header: <c>&lt;profile&gt;/&lt;title&gt;/Headers/&lt;slot&gt;/HeroNNN.header</c>.</summary>
    private static readonly Regex HeroHeader = new(
        @"\A[0-9A-Fa-f]{16}/[0-9A-Fa-f]{8}/Headers/[0-9A-Fa-f]{8}/[^/]+\.header\z", RegexOptions.CultureInvariant);

    /// <summary>What a valid export's save holds.</summary>
    /// <param name="Files">Save files.</param>
    /// <param name="Bytes">Their size in all.</param>
    /// <param name="Heroes">Hero save headers among them.</param>
    public sealed record Inspection(int Files, long Bytes, int Heroes);

    /// <summary>The outcome of an import.</summary>
    /// <param name="Imported">What was restored.</param>
    /// <param name="Backup">The backup export's path, or null when there was no save to back up.</param>
    public sealed record Result(Inspection Imported, string? Backup);

    /// <summary>A save entry's path inside the save folder, or null when the entry is not a save file.</summary>
    /// <param name="entryName">The zip entry's full name.</param>
    /// <exception cref="InvalidDataException">The name is under the save root but unsafe to extract.</exception>
    public static string? SaveRelativePath(string entryName)
    {
        if (!entryName.StartsWith(SaveRoot, StringComparison.Ordinal)) return null;
        string relative = entryName[SaveRoot.Length..];
        if (relative.Length == 0 || relative.EndsWith('/')) return null;
        if (relative.Contains('\\') || relative.Contains(':') || relative.StartsWith('/') || relative.Length > 400)
            throw new InvalidDataException("The export has an unsafe file name: " + entryName);
        foreach (string segment in relative.Split('/'))
        {
            if (segment.Length == 0 || segment == "." || segment == ".." || segment.Length > 200)
                throw new InvalidDataException("The export has an unsafe file name: " + entryName);
        }
        if (relative.Split('/')[0] == AndroidLogBundle.PrivateSaveFolder) return null;
        return relative;
    }

    /// <summary>Checks every save entry of <paramref name="zip"/> without extracting anything.</summary>
    /// <exception cref="InvalidDataException">The zip holds no save, or an unsafe or oversized one; the message says which.</exception>
    public static Inspection Inspect(ZipArchive zip)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int files = 0, heroes = 0;
        long bytes = 0;
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            if (SaveRelativePath(entry.FullName) is not { } relative) continue;
            if (!seen.Add(relative)) throw new InvalidDataException("The export has a save file twice: " + relative);
            if (entry.Length < 0 || entry.Length > MaxFileBytes)
                throw new InvalidDataException("The export has a save file too large to be one: " + relative);
            files++;
            bytes += entry.Length;
            if (files > MaxFiles || bytes > MaxTotalBytes)
                throw new InvalidDataException("The export's save is larger than any Fable 2 save.");
            if (HeroHeader.IsMatch(relative)) heroes++;
        }
        if (files == 0)
            throw new InvalidDataException("This export has no save in it. Export \"Saves\" or \"Logs and saves\" to make one that can be imported.");
        if (heroes == 0)
            throw new InvalidDataException("This export's save folder has no hero's save in it.");
        return new Inspection(files, bytes, heroes);
    }

    /// <summary>
    /// Restores the save folder under <paramref name="userDirectory"/> from the export at <paramref name="zipPath"/>,
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
        string saves = Path.Combine(userDirectory, AndroidLogBundle.SaveFolder.Replace('/', Path.DirectorySeparatorChar));
        using ZipArchive zip = ZipFile.OpenRead(zipPath);
        Inspection inspection = Inspect(zip);

        string staging = saves + ".import-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(staging);
        try
        {
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                if (SaveRelativePath(entry.FullName) is not { } relative) continue;
                string target = Path.GetFullPath(Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!target.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    throw new InvalidDataException("The export has an unsafe file name: " + entry.FullName);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using Stream source = entry.Open();
                using var output = new FileStream(target, FileMode.CreateNew, System.IO.FileAccess.Write);
                CopyBounded(source, output, MaxFileBytes, relative);
            }

            string? backupPath = HasFiles(saves) ? backup() : null;
            Swap(saves, staging);
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

    /// <summary>Moves <paramref name="staging"/> into place as <paramref name="saves"/>, carrying the phone's own <c>rexnet/</c> across.</summary>
    /// <remarks>The current save is set aside first and put back if any step fails; it is deleted only once the new one stands.</remarks>
    private static void Swap(string saves, string staging)
    {
        string? replaced = null;
        string rexnet = AndroidLogBundle.PrivateSaveFolder;
        try
        {
            if (Directory.Exists(saves))
            {
                replaced = saves + ".replaced-" + Guid.NewGuid().ToString("N");
                Directory.Move(saves, replaced);
                if (Directory.Exists(Path.Combine(replaced, rexnet)))
                    Directory.Move(Path.Combine(replaced, rexnet), Path.Combine(staging, rexnet));
            }
            Directory.Move(staging, saves);
        }
        catch
        {
            if (replaced != null && Directory.Exists(replaced) && !Directory.Exists(saves))
            {
                if (Directory.Exists(Path.Combine(staging, rexnet)) && !Directory.Exists(Path.Combine(replaced, rexnet)))
                    Directory.Move(Path.Combine(staging, rexnet), Path.Combine(replaced, rexnet));
                Directory.Move(replaced, saves);
            }
            throw;
        }
        if (replaced != null) TryDelete(replaced);
    }

    /// <summary>Whether <paramref name="folder"/> holds any file at all.</summary>
    private static bool HasFiles(string folder)
    {
        if (!Directory.Exists(folder)) return false;
        using IEnumerator<string> files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).GetEnumerator();
        return files.MoveNext();
    }

    private static void TryDelete(string folder)
    {
        try { Directory.Delete(folder, recursive: true); }
        catch (Exception error) { GD.PushWarning("[export] could not remove " + folder + ": " + error.Message); }
    }
}
