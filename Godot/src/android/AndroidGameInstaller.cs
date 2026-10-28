using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using Refii.Launcher.Core;
using Refii.Launcher.Stfs;
using Refii.Launcher.Xiso;

namespace Refii.AndroidRuntime;

/// <summary>Extracts a user's disc and title update into a fresh private installation without changing an active game.</summary>
/// <remarks>Original documents are read through injected copy operations. Temporary copies belong
/// only to this attempt and are removed after preparation; complete game assets remain for play or retry.</remarks>
public static class AndroidGameInstaller
{
    /// <summary>Copies one selected document without giving the installer permission to alter its source.</summary>
    /// <param name="destination">New private temporary file owned by this attempt.</param>
    /// <param name="progress">Receives bounded human-readable copy progress.</param>
    /// <param name="cancellation">Stops an owned copy promptly.</param>
    public delegate void CopyInput(string destination, Action<string> progress, CancellationToken cancellation);

    /// <summary>Prepared physical assets and input provenance; activation remains a separate action.</summary>
    /// <param name="Directory">Unique owned installation attempt.</param>
    /// <param name="GameRoot">Extracted disc assets, with the matched update beside the executable.</param>
    /// <param name="IsoSha256">Hash of the privately copied original disc bytes.</param>
    /// <param name="UpdateSha256">Hash of the original selected update package, if supplied.</param>
    /// <param name="Files">Complete extracted regular-file count.</param>
    /// <param name="Bytes">Total extracted bytes required by the installed game.</param>
    public sealed record Prepared(string Directory, string GameRoot, string IsoSha256,
        string? UpdateSha256, int Files, long Bytes);

    /// <summary>Copies, validates and extracts a complete disc/update pair with cancellation and safe failure cleanup.</summary>
    /// <param name="iso">Read-only selected ISO document copy.</param>
    /// <param name="update">Optional read-only title-update package or extracted patch copy.</param>
    /// <param name="privateRoot">App-private parent for unique owned installation directories.</param>
    /// <param name="progress">Receives preparation stages and extraction progress.</param>
    /// <param name="cancellation">Stops preparation without modifying a working install.</param>
    /// <returns>Complete prepared inputs for on-phone compilation and later activation.</returns>
    public static Prepared Prepare(CopyInput iso, CopyInput? update, string privateRoot,
        Action<string> progress, CancellationToken cancellation)
    {
        privateRoot = AndroidRuntimeOptions.PhysicalPath(privateRoot, "installer root");
        cancellation.ThrowIfCancellationRequested();
        string attempt = Path.Combine(privateRoot, "install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(attempt);
        File.WriteAllText(Path.Combine(attempt, ".refii-install-owner"), Path.GetFileName(attempt));
        string input = Path.Combine(attempt, "input"), game = Path.Combine(attempt, "game");
        Directory.CreateDirectory(input);
        try
        {
            string image = Path.Combine(input, "disc.iso"), package = Path.Combine(input, "title-update");
            progress("Copying selected disc into temporary private storage");
            iso(image, progress, cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (!XisoExtractor.TryReadRootFileHead(image, "default.xex", 64 * 1024, out byte[] head, out string error) ||
                !TitleUpdate.TryReadExecutable(head, out XexIdentity executable, out error) ||
                !SupportedGame.IsExecutable(executable, out error))
                throw new InvalidDataException(error);
            TitleUpdate? patch = null;
            string? updateHash = null;
            if (update != null)
            {
                progress("Copying selected title update");
                update(package, progress, cancellation);
                cancellation.ThrowIfCancellationRequested();
                ValidatePackage(package);
                if (!TitleUpdate.TryLoad(package, out patch, out error) ||
                    !SupportedGame.IsTitleUpdate(patch.Identity, out error) || !patch.AppliesTo(executable, out error))
                    throw new InvalidDataException(error);
                updateHash = Hash(package);
            }
            string imageHash = Hash(image);
            long lastBytes = -1;
            long lastReport = Environment.TickCount64;
            bool spaceChecked = false;
            progress("Extracting game files on this phone");
            bool extracted = XisoExtractor.ExtractImage(image, game, out error, state =>
            {
                cancellation.ThrowIfCancellationRequested();
                if (!spaceChecked)
                {
                    if (new DriveInfo(attempt).AvailableFreeSpace < state.TotalBytes + 2L * 1024 * 1024 * 1024)
                        throw new IOException("Not enough space to extract and compile the game; free additional storage and retry");
                    spaceChecked = true;
                }
                if (state.CopiedBytes != lastBytes &&
                    (Environment.TickCount64 - lastReport >= 250 || state.CopiedBytes == state.TotalBytes))
                {
                    lastBytes = state.CopiedBytes;
                    lastReport = Environment.TickCount64;
                    int percent = state.TotalBytes == 0 ? 0 : (int)(state.CopiedBytes * 100 / state.TotalBytes);
                    progress($"Extracting game files: {percent}% ({state.CompletedFiles}/{state.TotalFiles})");
                }
            });
            cancellation.ThrowIfCancellationRequested();
            if (!extracted) throw new InvalidDataException(error);
            if (!Directory.Exists(Path.Combine(game, "data")))
                throw new InvalidDataException("The disc is missing the game data directory");
            if (!TitleUpdate.TryReadExecutable(Path.Combine(game, "default.xex"), out executable, out error) ||
                !SupportedGame.IsExecutable(executable, out error)) throw new InvalidDataException(error);
            if (patch != null)
            {
                progress("Extracting the title update");
                ExtractUpdate(package, game, patch, cancellation);
            }
            cancellation.ThrowIfCancellationRequested();
            var files = new Dictionary<string, object>(StringComparer.Ordinal);
            long bytes = 0;
            progress("Verifying extracted game files");
            foreach (string file in System.IO.Directory.EnumerateFiles(game, "*", SearchOption.AllDirectories))
            {
                cancellation.ThrowIfCancellationRequested();
                long length = new FileInfo(file).Length;
                bytes += length;
                files.Add(Path.GetRelativePath(game, file).Replace('\\', '/'), new { bytes = length, sha256 = Hash(file) });
            }
            var result = new Prepared(attempt, game, imageHash, updateHash, files.Count, bytes);
            File.WriteAllText(Path.Combine(attempt, "extracted-files.json"), Refii.Core.Engine_Json.Serialize(files));
            DeleteOwnedChild(attempt, "input");
            File.WriteAllText(Path.Combine(attempt, "prepared.json"), Refii.Core.Engine_Json.Serialize(result));
            progress("Game files extracted and verified; temporary ISO and update copies removed");
            return result;
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(attempt, "failure.json"), Refii.Core.Engine_Json.Serialize(new {
                phase = "preparation", cancelled = cancellation.IsCancellationRequested, error = error.Message }));
            DeleteOwnedChild(attempt, "input");
            DeleteOwnedChild(attempt, "game");
            throw;
        }
    }

    /// <summary>Bounds memory before invoking the existing STFS reader and rejects packages for another title.</summary>
    private static void ValidatePackage(string package)
    {
        using var stream = File.OpenRead(package);
        byte[] head = new byte[4];
        stream.ReadExactly(head);
        if (!StfsPackage.IsPackage(head))
        {
            if (stream.Length > 64L * 1024 * 1024) throw new InvalidDataException("Title-update patch is too large");
            return;
        }
        if (!StfsPackage.TryReadHeader(package, out var header, out string error) ||
            !StfsPackage.TryListEntries(package, out var entries, out error)) throw new InvalidDataException(error);
        if (header.ContentType != StfsPackage.ContentTypeTitleUpdate || header.TitleId != SupportedGame.TitleId)
            throw new InvalidDataException("Select the Fable 2 title-update package");
        if (entries.Any(entry => !entry.IsDirectory && (entry.Size > 64L * 1024 * 1024 || entry.Size > stream.Length)) ||
            entries.Sum(entry => (long)entry.Size) > 512L * 1024 * 1024)
            throw new InvalidDataException("Title-update package has invalid or oversized entries");
        if (entries.Count(entry => !entry.IsDirectory && entry.Name.EndsWith(".xexp", StringComparison.OrdinalIgnoreCase)) != 1)
            throw new InvalidDataException("Title update must contain exactly one executable patch");
        ResolveEntries(entries);
    }

    /// <summary>Extracts every update entry, retaining its hierarchy and selecting the matched patch for compilation.</summary>
    private static void ExtractUpdate(string package, string game, TitleUpdate patch, CancellationToken cancellation)
    {
        if (patch.SourceKind == "title update package")
        {
            if (!StfsPackage.TryListEntries(package, out var entries, out string error)) throw new InvalidDataException(error);
            string[] paths = ResolveEntries(entries);
            for (int i = 0; i < entries.Count; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                var expected = entries[i];
                string target = Path.Combine(game, paths[i]);
                if (expected.IsDirectory) { Directory.CreateDirectory(target); continue; }
                if (expected.Name.EndsWith(".xexp", StringComparison.OrdinalIgnoreCase)) continue;
                if (!StfsPackage.TryReadFile(package, entry => entry.Name == expected.Name &&
                    entry.ParentIndex == expected.ParentIndex && entry.StartBlock == expected.StartBlock,
                    out _, out byte[] data, out error)) throw new InvalidDataException(error);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                // A title update intentionally replaces its corresponding disc asset,
                // but this entire game tree is a new owned attempt, never the active install.
                File.WriteAllBytes(target, data);
            }
        }
        File.WriteAllBytes(Path.Combine(game, "default.xexp"), patch.Patch);
    }

    /// <summary>Rejects traversal, ambiguous names and cyclic or invalid STFS parent references before extraction.</summary>
    /// <param name="entries">The existing package reader's complete file table.</param>
    /// <returns>Unique safe relative paths for all files and directories.</returns>
    public static string[] ResolveEntries(IReadOnlyList<StfsPackage.Entry> entries)
    {
        var resolved = new string[entries.Count];
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < entries.Count; i++)
        {
            var components = new List<string>();
            var seen = new HashSet<int>();
            int current = i;
            while (current != 0xffff)
            {
                if (current < 0 || current >= entries.Count || !seen.Add(current) || seen.Count > 256)
                    throw new InvalidDataException("Invalid or cyclic title-update directory hierarchy");
                var entry = entries[current];
                if (entry.Name.Length == 0 || entry.Name is "." or ".." ||
                    entry.Name.Any(character => character < 32 || character is '/' or '\\' or ':' || character == '\0') ||
                    (current != i && !entry.IsDirectory)) throw new InvalidDataException("Unsafe title-update entry");
                components.Add(entry.Name);
                current = entry.ParentIndex;
            }
            components.Reverse();
            resolved[i] = Path.Combine(components.ToArray());
            if (!unique.Add(resolved[i])) throw new InvalidDataException("Duplicate title-update path");
        }
        return resolved;
    }

    /// <summary>Streams one file hash for provenance without loading multi-gigabyte images into memory.</summary>
    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(Refii.Core.Engine_Hash.Sha256(stream)).ToLowerInvariant();
    }

    /// <summary>Removes only a known temporary child of an installer-owned attempt, without following links.</summary>
    /// <param name="attempt">Unique directory carrying this installer's ownership marker.</param>
    /// <param name="child">Exact installer-created child, never a supplied original path.</param>
    public static void DeleteOwnedChild(string attempt, string child)
    {
        if (child is not ("input" or "game")) throw new InvalidDataException("Unknown installer temporary directory");
        string marker = Path.Combine(attempt, ".refii-install-owner");
        if (!File.Exists(marker) || File.ReadAllText(marker) != Path.GetFileName(attempt) ||
            !Path.GetFileName(attempt).StartsWith("install-", StringComparison.Ordinal) ||
            (File.GetAttributes(attempt) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Temporary directory is not owned by this installer");
        string path = Path.Combine(attempt, child);
        if (!Directory.Exists(path)) return;
        RejectLinks(path);
        Directory.Delete(path, recursive: true);
    }

    /// <summary>Checks every descendant before recursive removal so cleanup cannot escape through a link.</summary>
    private static void RejectLinks(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Installer cleanup does not follow symbolic links");
        foreach (string entry in Directory.EnumerateFileSystemEntries(path))
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Installer cleanup does not follow symbolic links");
            if (Directory.Exists(entry)) RejectLinks(entry);
        }
    }
}
