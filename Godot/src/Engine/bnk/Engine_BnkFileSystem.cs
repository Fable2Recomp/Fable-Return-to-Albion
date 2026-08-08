using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Refii.Bnk;

public sealed class BnkFileSystem : IDisposable
{
    public const int MaxNestingDepth = 4;

    private sealed class Node
    {
        public SortedDictionary<string, Node> Directories { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public SortedDictionary<string, BnkFile> Files { get; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly Node _root = new();
    private readonly List<BnkArchive> _archives = new();
    private readonly List<string> _failures = new();
    private readonly Dictionary<string, BnkContainer> _containers =
        new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, BnkContainer> Containers => _containers;

    public bool TryGetContainer(string virtualPath, out BnkContainer container) =>
        _containers.TryGetValue(Normalize(virtualPath), out container!);

    public IReadOnlyList<BnkArchive> Archives => _archives;

    public IReadOnlyList<string> Failures => _failures;

    public int FileCount { get; private set; }

    public int NestedArchiveCount { get; private set; }

    public int ShadowedFileCount { get; private set; }

    public static readonly string[] DefaultLooseExtensions = { ".gdb", ".list" };

    public static BnkFileSystem Mount(
        string gameDirectory,
        bool expandNested = true,
        IReadOnlyList<string>? looseExtensions = null)
    {
        var filesystem = new BnkFileSystem();
        if (string.IsNullOrEmpty(gameDirectory) || !Directory.Exists(gameDirectory))
        {
            return filesystem;
        }

        IEnumerable<string> banks;
        try
        {
            banks = Directory.EnumerateFiles(gameDirectory, "*.bnk", SearchOption.AllDirectories);
        }
        catch (Exception exception)
        {
            filesystem._failures.Add($"{gameDirectory}: {exception.Message}");
            return filesystem;
        }

        foreach (string bank in banks.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            if (BnkArchive.TryOpen(bank, out BnkArchive? archive, out string error) &&
                archive is not null)
            {
                filesystem._archives.Add(archive);
                BnkArchive owner = archive;
                filesystem.Index(archive, entry => owner.ReadEntryBytes(entry), 0, expandNested);
            }
            else
            {
                filesystem._failures.Add($"{Path.GetFileName(bank)}: {error}");
            }
        }

        filesystem.IndexLoose(gameDirectory, looseExtensions ?? DefaultLooseExtensions);
        return filesystem;
    }

    private void IndexLoose(string gameDirectory, IReadOnlyList<string> extensions)
    {
        foreach (string extension in extensions)
        {
            string pattern = extension.StartsWith('.') ? $"*{extension}" : $"*.{extension}";

            IEnumerable<string> matches;
            try
            {
                matches = Directory.EnumerateFiles(
                    gameDirectory, pattern, SearchOption.AllDirectories);
            }
            catch (Exception exception)
            {
                _failures.Add($"{gameDirectory} ({pattern}): {exception.Message}");
                continue;
            }

            foreach (string path in matches.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                string relative;
                long size;
                try
                {
                    relative = Path.GetRelativePath(gameDirectory, path);
                    size = new FileInfo(path).Length;
                }
                catch (Exception exception)
                {
                    _failures.Add($"{path}: {exception.Message}");
                    continue;
                }

                string normalized = Normalize(relative);
                if (normalized.Length == 0)
                {
                    continue;
                }

                var entry = new BnkEntry
                {
                    RelativePath = normalized,
                    Size = (uint)Math.Min(size, uint.MaxValue),
                    StoredSize = (uint)Math.Min(size, uint.MaxValue),
                };

                string diskPath = path;
                Insert(
                    normalized,
                    new BnkFile(diskPath, entry, normalized, _ => File.ReadAllBytes(diskPath), true),
                    preferNew: true);
            }
        }
    }

    private void Index(
        BnkArchive archive, Func<BnkEntry, byte[]> read, int depth, bool expandNested)
    {
        foreach (BnkEntry entry in archive.Entries)
        {
            string normalized = Normalize(entry.RelativePath);
            if (normalized.Length == 0)
            {
                continue;
            }

            bool isNestedBank = normalized.EndsWith(".bnk", StringComparison.OrdinalIgnoreCase);
            if (isNestedBank && expandNested && depth < MaxNestingDepth)
            {
                if (TryExpandNested(archive, entry, read, depth, normalized))
                {
                    continue;
                }
            }

            Insert(normalized, new BnkFile(archive.ArchivePath, entry, normalized, read));
        }
    }

    private bool TryExpandNested(
        BnkArchive archive,
        BnkEntry entry,
        Func<BnkEntry, byte[]> read,
        int depth,
        string normalized)
    {
        byte[] bytes;
        try
        {
            bytes = read(entry);
        }
        catch (Exception exception)
        {
            _failures.Add($"{normalized}: {exception.Message}");
            return false;
        }

        if (!BnkArchive.TryOpenBytes(normalized, bytes, out BnkArchive? nested, out string error) ||
            nested is null)
        {
            _failures.Add($"{normalized}: {error}");
            return false;
        }

        BnkEntry container = entry;
        Func<BnkEntry, byte[]> nestedRead = target =>
        {
            byte[] containerBytes = read(container);
            if (!BnkArchive.TryOpenBytes(
                    normalized, containerBytes, out BnkArchive? reopened, out string reopenError) ||
                reopened is null)
            {
                throw new InvalidDataException(reopenError);
            }

            using (reopened)
            {
                return reopened.ReadEntryBytes(target);
            }
        };

        using (nested)
        {
            ++NestedArchiveCount;
            var contents = new List<BnkFile>(nested.Entries.Count);
            foreach (BnkEntry nestedEntry in nested.Entries)
            {
                contents.Add(new BnkFile(
                    normalized,
                    nestedEntry,
                    Normalize(nestedEntry.RelativePath),
                    nestedRead));
            }

            _containers[normalized] = new BnkContainer(normalized, archive.ArchivePath, contents);
            Index(nested, nestedRead, depth + 1, true);
        }

        Insert(normalized, new BnkFile(archive.ArchivePath, entry, normalized, read));
        return true;
    }

    private void Insert(string normalized, BnkFile file, bool preferNew = false)
    {
        string[] parts = normalized.Split('/');
        Node node = _root;
        for (int i = 0; i < parts.Length - 1; ++i)
        {
            if (!node.Directories.TryGetValue(parts[i], out Node? child))
            {
                child = new Node();
                node.Directories[parts[i]] = child;
            }

            node = child;
        }

        string name = parts[^1];
        if (!node.Files.TryGetValue(name, out BnkFile? existing))
        {
            ++FileCount;
            node.Files[name] = file;
            return;
        }

        ++ShadowedFileCount;

        if (preferNew || file.Size > existing.Size)
        {
            node.Files[name] = file;
            file.Alternates.Add(existing);
            file.Alternates.AddRange(existing.Alternates);
            existing.Alternates.Clear();
        }
        else
        {
            existing.Alternates.Add(file);
        }
    }

    public IEnumerable<string> EnumerateDirectories(string virtualDirectory) =>
        Resolve(virtualDirectory)?.Directories.Keys ?? Enumerable.Empty<string>();

    public IEnumerable<string> EnumerateFiles(string virtualDirectory) =>
        Resolve(virtualDirectory)?.Files.Keys ?? Enumerable.Empty<string>();

    public bool DirectoryExists(string virtualDirectory) => Resolve(virtualDirectory) is not null;

    public bool TryGetFile(string virtualPath, out BnkFile file)
    {
        file = default!;
        string normalized = Normalize(virtualPath);
        if (normalized.Length == 0)
        {
            return false;
        }

        int split = normalized.LastIndexOf('/');
        string directory = split < 0 ? string.Empty : normalized[..split];
        string name = split < 0 ? normalized : normalized[(split + 1)..];

        Node? node = Resolve(directory);
        if (node is null || !node.Files.TryGetValue(name, out BnkFile? found))
        {
            return false;
        }

        file = found;
        return true;
    }

    public IEnumerable<BnkFile> EnumerateAllFiles()
    {
        var pending = new Stack<Node>();
        pending.Push(_root);
        while (pending.Count != 0)
        {
            Node node = pending.Pop();
            foreach (BnkFile file in node.Files.Values)
            {
                yield return file;
            }

            foreach (Node child in node.Directories.Values)
            {
                pending.Push(child);
            }
        }
    }

    private Node? Resolve(string virtualDirectory)
    {
        string normalized = Normalize(virtualDirectory);
        Node node = _root;
        if (normalized.Length == 0)
        {
            return node;
        }

        foreach (string part in normalized.Split('/'))
        {
            if (!node.Directories.TryGetValue(part, out Node? child))
            {
                return null;
            }

            node = child;
        }

        return node;
    }

    private static string Normalize(string path) => path.Replace('\\', '/').Trim('/');

    public void Dispose()
    {
        foreach (BnkArchive archive in _archives)
        {
            archive.Dispose();
        }

        _archives.Clear();
    }
}

public sealed class BnkContainer
{
    internal BnkContainer(string virtualPath, string hostArchivePath, IReadOnlyList<BnkFile> files)
    {
        VirtualPath = virtualPath;
        HostArchivePath = hostArchivePath;
        Files = files;
    }

    public string VirtualPath { get; }

    public string HostArchivePath { get; }

    public IReadOnlyList<BnkFile> Files { get; }

    public string Name => Path.GetFileName(VirtualPath);

    public override string ToString() => $"{VirtualPath} ({Files.Count} entries)";
}

public sealed class BnkFile
{
    private readonly Func<BnkEntry, byte[]> _read;

    internal BnkFile(
        string sourceArchivePath,
        BnkEntry entry,
        string virtualPath,
        Func<BnkEntry, byte[]> read,
        bool isLoose = false)
    {
        SourceArchivePath = sourceArchivePath;
        Entry = entry;
        VirtualPath = virtualPath;
        _read = read;
        IsLoose = isLoose;
    }

    public string SourceArchivePath { get; }

    public bool IsLoose { get; }

    public string SourceArchiveName => Path.GetFileName(SourceArchivePath);

    public BnkEntry Entry { get; }

    public string VirtualPath { get; }

    public uint Size => Entry.Size;

    internal List<BnkFile> Alternates { get; } = new();

    public byte[] ReadAllBytes()
    {
        byte[] body = _read(Entry);
        if (Alternates.Count == 0)
        {
            return body;
        }

        // A .mdl and a .tex are each ONE logical file served as two bank entries under
        // the same name — a small header in one bank, the body in another. The indexer
        // keeps the larger as primary and the other as an alternate, so reading the
        // primary alone yields a body with no header.
        //
        // Which half we hold is decided by looking for the header's own marker, never by
        // size: a .tex header is 84 bytes and its payload can be smaller than that for a
        // tiny mip, so "the big one is the body" is not safe.
        Func<byte[], bool> isHeader;
        if (VirtualPath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
        {
            isHeader = HasMeshFileMagic;
        }
        else if (VirtualPath.EndsWith(".tex", StringComparison.OrdinalIgnoreCase))
        {
            isHeader = HasTexHeaderMagic;
        }
        else
        {
            return body;
        }

        if (isHeader(body))
        {
            return body;
        }

        foreach (BnkFile alternate in Alternates)
        {
            byte[] header;
            try
            {
                header = alternate._read(alternate.Entry);
            }
            catch (Exception)
            {
                continue;
            }

            if (!isHeader(header))
            {
                continue;
            }

            var combined = new byte[header.Length + body.Length];
            Buffer.BlockCopy(header, 0, combined, 0, header.Length);
            Buffer.BlockCopy(body, 0, combined, header.Length, body.Length);
            return combined;
        }

        return body;
    }

    private static bool HasMeshFileMagic(byte[] d) =>
        d.Length >= 8 && d[0] == 'M' && d[1] == 'e' && d[2] == 's' && d[3] == 'h' &&
        d[4] == 'F' && d[5] == 'i' && d[6] == 'l' && d[7] == 'e';

    /// <summary>
    /// True when the bytes open with a <c>.tex</c> header. It has no ASCII magic — the
    /// marker is that dword 0 holds the NEGATED version, so it reads as a large negative
    /// number (version 2 is 0xFFFFFFFE).
    /// </summary>
    private static bool HasTexHeaderMagic(byte[] d) =>
        d.Length >= 84 && (d[0] & 0x80) != 0 && d[0] == 0xFF;

    public override string ToString() => VirtualPath;
}
