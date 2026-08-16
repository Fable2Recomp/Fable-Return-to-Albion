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

    private const long NestedCacheBudget = 1024L << 20;

    private sealed class NestedSlot
    {
        public required string Key;
        public required Func<BnkEntry, byte[]> ReadOuter;
        public required BnkEntry Container;
        public readonly object LoadLock = new();
        public volatile BnkArchive? Archive;
        public long Bytes;
        public LinkedListNode<NestedSlot>? Node;
    }

    private readonly object _nestedLock = new();
    private readonly LinkedList<NestedSlot> _nestedLoaded = new();
    private long _nestedLoadedBytes;

    private byte[] ReadNestedEntry(NestedSlot slot, BnkEntry target)
    {
        BnkArchive? archive = slot.Archive;
        if (archive is null)
        {
            lock (slot.LoadLock)
            {
                archive = slot.Archive;
                if (archive is null)
                {
                    byte[] container = slot.ReadOuter(slot.Container);
                    if (!BnkArchive.TryOpenBytes(
                            slot.Key, container, out archive, out string error) ||
                        archive is null)
                    {
                        throw new InvalidDataException(error);
                    }

                    slot.Bytes = container.LongLength;
                    slot.Archive = archive;
                    Retain(slot);
                }
            }
        }

        Touch(slot);
        return archive.ReadEntryBytes(target);
    }

    private void Retain(NestedSlot slot)
    {
        lock (_nestedLock)
        {
            slot.Node = _nestedLoaded.AddFirst(slot);
            _nestedLoadedBytes += slot.Bytes;

            while (_nestedLoadedBytes > NestedCacheBudget && _nestedLoaded.Count > 1)
            {
                NestedSlot evicted = _nestedLoaded.Last!.Value;
                _nestedLoaded.RemoveLast();
                _nestedLoadedBytes -= evicted.Bytes;
                evicted.Node = null;
                evicted.Bytes = 0;
                evicted.Archive = null;
            }
        }
    }

    private void Touch(NestedSlot slot)
    {
        lock (_nestedLock)
        {
            if (slot.Node?.List == _nestedLoaded && slot.Node != _nestedLoaded.First)
            {
                _nestedLoaded.Remove(slot.Node);
                _nestedLoaded.AddFirst(slot.Node);
            }
        }
    }

    public IReadOnlyDictionary<string, BnkContainer> Containers => _containers;

    public bool TryGetContainer(string virtualPath, out BnkContainer container) =>
        _containers.TryGetValue(Normalize(virtualPath), out container!);

    public IReadOnlyList<BnkArchive> Archives => _archives;

    public IReadOnlyList<string> Failures => _failures;

    public int FileCount { get; private set; }

    public int NestedArchiveCount { get; private set; }

    public int ShadowedFileCount { get; private set; }

    public static readonly string[] DefaultLooseExtensions =
        { ".gdb", ".list", ".animation_toc", ".animation_data" };

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
            banks = EnumerateByExtension(gameDirectory, ".bnk").ToList();
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

        Refii.FlashGames.Engine_SwfAssets.Index(filesystem, gameDirectory);
        return filesystem;
    }

    public void AddVirtualFile(string virtualPath, string sourcePath, long size, Func<byte[]> read)
    {
        string normalized = Normalize(virtualPath);
        if (normalized.Length == 0)
        {
            return;
        }

        var entry = new BnkEntry
        {
            RelativePath = normalized,
            Size = (uint)Math.Min(size, uint.MaxValue),
            StoredSize = (uint)Math.Min(size, uint.MaxValue),
        };

        Insert(normalized, new BnkFile(sourcePath, entry, normalized, _ => read(), true), preferNew: true);
    }

    private static IEnumerable<string> EnumerateByExtension(string root, params string[] extensions)
    {
        foreach (string path in Directory.EnumerateFiles(
            root, "*", SearchOption.AllDirectories))
        {
            foreach (string extension in extensions)
            {
                string suffix = extension.StartsWith('.') ? extension : "." + extension;
                if (path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    yield return path;
                    break;
                }
            }
        }
    }

    private void IndexLoose(string gameDirectory, IReadOnlyList<string> extensions)
    {
        foreach (string extension in extensions)
        {
            string pattern = extension.StartsWith('.') ? $"*{extension}" : $"*.{extension}";

            IEnumerable<string> matches;
            try
            {
                matches = EnumerateByExtension(gameDirectory, extension).ToList();
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

        var slot = new NestedSlot { Key = normalized, ReadOuter = read, Container = entry };
        Func<BnkEntry, byte[]> nestedRead = target => ReadNestedEntry(slot, target);

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

    internal byte[] ReadOwn() => _read(Entry);

    /// <summary>
    /// The file's bytes, with a header entry and a payload entry rejoined when the format keeps
    /// them apart.
    ///
    /// A .mdl and a .tex each ship as TWO bank entries under ONE name: a small header, always in
    /// a `*_headers.bnk`, and a payload in a models/textures bank — usually a region's, nested
    /// inside `streaming.bnk`. The guest never sees the split. It opens one logical file by name
    /// hash and byte-ranges into it: LOD i of a model is read at 104 + the sum of the preceding
    /// LOD sizes, for the length the header states (`sub_82AB4868`). So a caller has to be given
    /// the concatenation, in that order, or every offset in the file is 104 bytes wrong.
    ///
    /// Which of the two entries won the shadow contest in <see cref="BnkFileSystem.Insert"/> is
    /// not something this can rest on — that picks the larger, and "larger" is a property of the
    /// data rather than of the format. The header is identified by its magic and joined in front
    /// whichever side it arrived on.
    /// </summary>
    public byte[] ReadAllBytes()
    {
        if (Alternates.Count != 0 &&
            VirtualPath.EndsWith(".tex", StringComparison.OrdinalIgnoreCase) &&
            TryReadTexture(out byte[] texture))
        {
            return texture;
        }

        byte[] own = _read(Entry);
        if (Alternates.Count == 0)
        {
            return own;
        }

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
            return own;
        }

        bool ownIsHeader = isHeader(own);

        foreach (BnkFile alternate in Alternates)
        {
            byte[] other;
            try
            {
                other = alternate._read(alternate.Entry);
            }
            catch (Exception)
            {
                continue;
            }

            if (isHeader(other) == ownIsHeader)
            {
                // Two headers, or two payloads: the same file shipped in several banks, not the
                // two halves of one. Nothing to join.
                continue;
            }

            byte[] header = ownIsHeader ? own : other;
            byte[] payload = ownIsHeader ? other : own;

            var combined = new byte[header.Length + payload.Length];
            Buffer.BlockCopy(header, 0, combined, 0, header.Length);
            Buffer.BlockCopy(payload, 0, combined, header.Length, payload.Length);
            return combined;
        }

        return own;
    }

    private bool TryReadTexture(out byte[] bytes)
    {
        bytes = Array.Empty<byte>();

        var parts = new List<BnkFile>(Alternates.Count + 1) { this };
        parts.AddRange(Alternates);

        BnkFile? headerFile = null;
        byte[]? header = null;
        foreach (BnkFile part in parts)
        {
            if (part.Entry.Size < Refii.Textures.TexHeader.Size || part.Entry.Size > 4096)
            {
                continue;
            }

            byte[] candidate;
            try
            {
                candidate = part._read(part.Entry);
            }
            catch (Exception)
            {
                continue;
            }

            if (Refii.Textures.TexHeader.LooksLikeHeader(candidate))
            {
                headerFile = part;
                header = candidate;
                break;
            }
        }

        if (header is null ||
            !Refii.Textures.TexHeader.TryParse(header, out Refii.Textures.TexHeader parsed, out _) ||
            parsed.PayloadSize <= 0)
        {
            return false;
        }

        long payload = parsed.PayloadSize;
        BnkFile? whole = null;
        BnkFile? mip0 = null;
        BnkFile? rest = null;
        long mip0Size = parsed.LevelCount >= 2 && parsed.LevelOffsets[1] > Refii.Textures.TexHeader.Size
            ? parsed.LevelOffsets[1] - Refii.Textures.TexHeader.Size
            : -1;

        foreach (BnkFile part in parts)
        {
            if (part == headerFile)
            {
                continue;
            }

            long size = part.Entry.Size;
            if (size == payload)
            {
                whole ??= part;
            }
            else if (mip0Size > 0 && size == mip0Size)
            {
                mip0 ??= part;
            }
            else if (mip0Size > 0 && size == payload - mip0Size)
            {
                rest ??= part;
            }
        }

        try
        {
            if (whole is not null)
            {
                bytes = Concat(header, whole._read(whole.Entry));
                return true;
            }

            if (mip0 is not null && rest is not null)
            {
                bytes = Concat(header, mip0._read(mip0.Entry), rest._read(rest.Entry));
                return true;
            }
        }
        catch (Exception)
        {
            return false;
        }

        return false;
    }

    private static byte[] Concat(byte[] a, byte[] b)
    {
        var joined = new byte[a.Length + b.Length];
        Buffer.BlockCopy(a, 0, joined, 0, a.Length);
        Buffer.BlockCopy(b, 0, joined, a.Length, b.Length);
        return joined;
    }

    private static byte[] Concat(byte[] a, byte[] b, byte[] c)
    {
        var joined = new byte[a.Length + b.Length + c.Length];
        Buffer.BlockCopy(a, 0, joined, 0, a.Length);
        Buffer.BlockCopy(b, 0, joined, a.Length, b.Length);
        Buffer.BlockCopy(c, 0, joined, a.Length + b.Length, c.Length);
        return joined;
    }

    /// <summary>
    /// A model file header. "DefMeshF" is the build's placeholder for a mesh that failed to
    /// export; the guest tests for it FIRST, warns, and parses it as an ordinary header
    /// (<c>sub_82AB4DC8</c> at 0x82AB4E74), so it is a header here too.
    /// </summary>
    private static bool HasMeshFileMagic(byte[] d) =>
        HasMagic(d, "MeshFile") || HasMagic(d, "DefMeshF");

    private static bool HasMagic(byte[] d, string magic)
    {
        if (d.Length < magic.Length)
        {
            return false;
        }

        for (int i = 0; i < magic.Length; ++i)
        {
            if (d[i] != magic[i])
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasTexHeaderMagic(byte[] d) =>
        d.Length >= 84 && (d[0] & 0x80) != 0 && d[0] == 0xFF;

    public override string ToString() => VirtualPath;
}
