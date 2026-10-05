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

    /// <summary>
    /// What the nested-bank cache is actually doing: bytes resident, containers loaded from disk,
    /// and containers evicted.
    /// </summary>
    /// <remarks>
    /// Cached containers are multi-megabyte arrays on the large-object heap, so the budget sizes
    /// the heap the collector walks. A miss re-reads the whole container (another LOH
    /// allocation), so a budget below the working set trades resident bytes for garbage.
    /// Evictions near zero mean the budget can come down; evictions climbing with loads mean the
    /// cache is thrashing and the budget should go up.
    /// </remarks>
    public static long NestedResidentBytes;

    public static long NestedLoads;

    public static long NestedEvictions;

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
                    System.Threading.Interlocked.Increment(ref NestedLoads);
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
                ++NestedEvictions;
            }

            NestedResidentBytes = _nestedLoadedBytes;
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

    /// <summary>
    /// Mounts the game's data, then each enabled mod over it.
    ///
    /// <paramref name="overlays"/> is LOWEST PRIORITY FIRST: entries insert last-wins, so the
    /// panel's highest-first list is reversed exactly once, in <c>ModIndex.ResolveOverlays</c>,
    /// and never again here.
    /// </summary>
    public static BnkFileSystem Mount(
        string gameDirectory,
        bool expandNested = true,
        IReadOnlyList<string>? looseExtensions = null,
        IReadOnlyList<(string Name, string AssetRoot)>? overlays = null)
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

        if (looseExtensions is null)
        {
            filesystem.IndexAllLoose(gameDirectory);
        }
        else
        {
            filesystem.IndexLoose(gameDirectory, looseExtensions);
        }

        Refii.FlashGames.Engine_SwfAssets.Index(filesystem, gameDirectory);

        // MODS LAST, so a mod can override anything the game shipped — the pub games' extracted
        // assets added just above included.
        if (overlays is not null)
        {
            foreach ((string name, string assetRoot) in overlays)
            {
                filesystem.IndexOverlay(name, assetRoot, expandNested);
            }
        }

        return filesystem;
    }

    /// <summary>
    /// One mod's assets, over everything already mounted.
    ///
    /// A mod-shipped `.bnk` is parsed and expanded exactly as the game's are, so a mod may be
    /// developed loose and shipped packed with no change to what the mount sees. The mod's own
    /// loose files still shadow its bank — the rule the game's data already follows.
    /// </summary>
    private void IndexOverlay(string name, string assetRoot, bool expandNested)
    {
        if (string.IsNullOrEmpty(assetRoot))
        {
            return;
        }

        if (File.Exists(assetRoot) && assetRoot.EndsWith(".f2mod", StringComparison.OrdinalIgnoreCase))
        {
            if (Refii.Mods.F2ModPackage.TryOpen(assetRoot, out Refii.Mods.F2ModPackage? package, out string pkgError) && package is not null)
            {
                foreach (Refii.Mods.F2ModEntry entry in package.Entries)
                {
                    if (IsModBookkeeping(Path.GetFileName(entry.Path)))
                    {
                        continue;
                    }

                    Refii.Mods.F2ModEntry captured = entry;
                    AddVirtualFile(
                        entry.Path,
                        assetRoot,
                        (long)entry.Size,
                        () => package.ReadEntryBytes(captured),
                        name);
                }
            }
            else
            {
                _failures.Add($"{name}: {pkgError}");
            }
            return;
        }

        if (!Directory.Exists(assetRoot))
        {
            return;
        }

        IEnumerable<string> banks;
        try
        {
            banks = EnumerateByExtension(assetRoot, ".bnk").ToList();
        }
        catch (Exception exception)
        {
            _failures.Add($"{name}: {exception.Message}");
            return;
        }

        foreach (string bank in banks.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            if (BnkArchive.TryOpen(bank, out BnkArchive? archive, out string error) &&
                archive is not null)
            {
                _archives.Add(archive);
                BnkArchive owner = archive;
                Index(archive, entry => owner.ReadEntryBytes(entry), 0, expandNested, name);
            }
            else
            {
                _failures.Add($"{name}/{Path.GetFileName(bank)}: {error}");
            }
        }

        IndexAllLoose(assetRoot, name);
    }

    /// <summary>
    /// Gives one path to one mod, whatever the load order said.
    ///
    /// A pin is the one resolution order cannot express, so it is applied after the fold: the
    /// pinned mod's file is swapped into the winner's slot and the previous winner becomes an
    /// alternate. False when that mod contributed nothing at that path — a stale pin is a no-op,
    /// not an error.
    /// </summary>
    public bool Promote(string virtualPath, string modName)
    {
        if (string.IsNullOrEmpty(modName) || !TryGetFile(virtualPath, out BnkFile winner))
        {
            return false;
        }

        if (string.Equals(winner.SourceMod, modName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        BnkFile? promoted = null;
        foreach (BnkFile alternate in winner.Alternates)
        {
            if (string.Equals(alternate.SourceMod, modName, StringComparison.OrdinalIgnoreCase))
            {
                promoted = alternate;
                break;
            }
        }

        if (promoted is null)
        {
            return false;
        }

        string normalized = Normalize(virtualPath);
        int split = normalized.LastIndexOf('/');
        Node? node = Resolve(split < 0 ? string.Empty : normalized[..split]);
        if (node is null)
        {
            return false;
        }

        var shadowed = new List<BnkFile> { winner };
        foreach (BnkFile alternate in winner.Alternates)
        {
            if (!ReferenceEquals(alternate, promoted))
            {
                shadowed.Add(alternate);
            }
        }

        winner.Alternates.Clear();
        promoted.Alternates.Clear();
        promoted.Alternates.AddRange(shadowed);
        node.Files[split < 0 ? normalized : normalized[(split + 1)..]] = promoted;
        return true;
    }

    /// <summary>
    /// Reads the unpatched original file bytes before any mod or virtual overlays were applied.
    /// Walks the alternates chain to find the deepest original source file.
    /// </summary>
    public byte[]? ReadBaseBytes(string virtualPath)
    {
        string normalized = Normalize(virtualPath);
        if (!TryGetFile(normalized, out BnkFile file))
        {
            return null;
        }

        BnkFile baseFile = file.Alternates.Count > 0 ? file.Alternates[^1] : file;
        try
        {
            return baseFile.ReadAllBytes();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Adds a file the mount serves from memory rather than from disk.
    ///
    /// <paramref name="sourceMod"/> attributes it, so a table merged from several mods' rows is
    /// reported as theirs rather than as the game's own data.
    /// </summary>
    public void AddVirtualFile(
        string virtualPath, string sourcePath, long size, Func<byte[]> read, string sourceMod = "")
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

        Insert(
            normalized,
            new BnkFile(sourcePath, entry, normalized, _ => read(), true, sourceMod),
            preferNew: true);

        foreach (string alias in AliasesFor(normalized))
        {
            var aliasEntry = new BnkEntry
            {
                RelativePath = alias,
                Size = entry.Size,
                StoredSize = entry.StoredSize,
            };
            Insert(
                alias,
                new BnkFile(sourcePath, aliasEntry, alias, _ => read(), true, sourceMod),
                preferNew: true);
        }
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

    /// <summary>
    /// Every loose file on disk, no extension list; skips `.bnk` (indexed by contents) and the
    /// xex/patch. Loose wins over a bank entry of the same name (<c>preferNew</c>).
    /// </summary>
    private void IndexAllLoose(string gameDirectory, string sourceMod = "")
    {
        IEnumerable<string> matches;
        try
        {
            matches = Directory.EnumerateFiles(
                gameDirectory, "*", SearchOption.AllDirectories).ToList();
        }
        catch (Exception exception)
        {
            _failures.Add($"{gameDirectory} (loose): {exception.Message}");
            return;
        }

        foreach (string path in matches.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            if (path.EndsWith(".bnk", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".xex", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".xexp", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // A mod's own bookkeeping is not one of its assets. Only reachable when the mod
            // keeps its files in its root rather than under `files/`.
            if (sourceMod.Length != 0 && IsModBookkeeping(Path.GetFileName(path)))
            {
                continue;
            }

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
                new BnkFile(
                    diskPath, entry, normalized, _ => File.ReadAllBytes(diskPath), true, sourceMod),
                preferNew: true);

            foreach (string alias in AliasesFor(normalized))
            {
                var aliasEntry = new BnkEntry
                {
                    RelativePath = alias,
                    Size = entry.Size,
                    StoredSize = entry.StoredSize,
                };
                Insert(
                    alias,
                    new BnkFile(
                        diskPath, aliasEntry, alias, _ => File.ReadAllBytes(diskPath), true,
                        sourceMod),
                    preferNew: sourceMod.Length != 0);
            }
        }
    }

    /// <summary>
    /// The one culture whose files also answer to their locale-less spelling; `en-uk` is the
    /// title's own fallback culture.
    /// </summary>
    public static string ActiveCulture { get; set; } = "en-uk";

    /// <summary>
    /// The other names a path answers to.
    ///
    /// ONE DEFINITION, because everything that puts a file into the mount needs the same one.
    /// The guest asks by the aliased spelling — `language\text\book.babel`, not
    /// `data\language\en-uk\text\book.babel` — so a file registered under its full path alone
    /// is invisible to it. A merged text book installed without these was served on the full
    /// path and the shipped book on every spelling the guest actually uses.
    ///
    /// `data/` comes off because the archives never had it; the locale comes off because
    /// `GameInit_MountDataFileSystems` mounts `language` over the active culture's directory.
    ///
    /// Aliasing is bidirectional: virtual files and mod overlays authored with or without `data/`,
    /// or with/without a culture folder (like `en-uk`), generate all corresponding variants so
    /// both the host loaders and the guest engine resolve the intended virtual assets.
    /// </summary>
    public static IEnumerable<string> AliasesFor(string normalized)
    {
        if (string.IsNullOrEmpty(normalized))
        {
            yield break;
        }

        const string dataPrefix = "data/";
        string noData = normalized.StartsWith(dataPrefix, StringComparison.OrdinalIgnoreCase)
            ? normalized[dataPrefix.Length..]
            : normalized;

        if (noData.Length == 0)
        {
            yield break;
        }

        var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Alias between data/... and the no-data spelling:
        string withData = dataPrefix + noData;
        results.Add(noData);
        results.Add(withData);

        // Language / culture aliases:
        const string languagePrefix = "language/";
        if (noData.StartsWith(languagePrefix, StringComparison.OrdinalIgnoreCase))
        {
            int localeEnd = noData.IndexOf('/', languagePrefix.Length);
            if (localeEnd > languagePrefix.Length)
            {
                string folder = noData[languagePrefix.Length..localeEnd];
                if (IsCultureFolder(folder))
                {
                    // Has active culture folder (e.g. language/en-uk/text/book.babel)
                    // Produce locale-less spellings (e.g. language/text/book.babel):
                    string rest = noData[(localeEnd + 1)..];
                    string localeLess = languagePrefix + rest;
                    results.Add(localeLess);
                    results.Add(dataPrefix + localeLess);
                }
                else if (!IsAnyCultureFolder(folder))
                {
                    // Locale-less spelling (e.g. language/text/book.babel where folder is "text")
                    // Produce active culture spellings (e.g. language/en-uk/text/book.babel):
                    string rest = noData[languagePrefix.Length..];
                    string withCulture = $"{languagePrefix}{ActiveCulture}/{rest}";
                    results.Add(withCulture);
                    results.Add(dataPrefix + withCulture);
                }
            }
        }

        // Return all aliases except the input path itself:
        foreach (string alias in results)
        {
            if (!alias.Equals(normalized, StringComparison.OrdinalIgnoreCase))
            {
                yield return alias;
            }
        }
    }

    /// <summary>
    /// What a mod ships that is NOT an asset: its own bookkeeping, and the sources the overlay
    /// systems compile.
    ///
    /// A `.gdb.csv`, `.babel.csv`, `.tex.png` or `.mdl.glb` is an INPUT — the mount turns it into
    /// the asset it names and adds that. Indexing the source as well put six phantom files into
    /// the first real run's resolved index, each reported as an addition the mod had made. They
    /// were harmless to the game, which never asks for them, and they were still a lie about what
    /// the mod contains.
    /// </summary>
    private static bool IsModBookkeeping(string leaf)
    {
        if (leaf.Equals(Refii.Mods.ModWorkspace.ModMetadataFilename, StringComparison.OrdinalIgnoreCase) ||
            leaf.Equals(Refii.Mods.ModWorkspace.ModManifestFilename, StringComparison.OrdinalIgnoreCase) ||
            leaf.EndsWith(".import", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (string suffix in ModSourceSuffixes)
        {
            if (leaf.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Authored inputs the overlay systems compile into the assets they name.</summary>
    private static readonly string[] ModSourceSuffixes =
    {
        ".babel.csv", ".tex.png", ".mdl.glb", ".mdl.gltf",
    };

    /// <summary>
    /// All supported culture folder names from <see cref="Refii.Text.TextBank.LanguageOrder"/>.
    /// Used by <see cref="AliasesFor"/> to distinguish culture-scoped directory names from generic subpaths.
    /// </summary>
    private static readonly HashSet<string> KnownCultures = new(
        Refii.Text.TextBank.LanguageOrder, StringComparer.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="segment"/> matches <see cref="ActiveCulture"/>.</summary>
    private static bool IsCultureFolder(string segment) =>
        string.Equals(segment, ActiveCulture, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="segment"/> matches any known game localization culture folder.</summary>
    private static bool IsAnyCultureFolder(string segment) =>
        KnownCultures.Contains(segment);

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
        BnkArchive archive,
        Func<BnkEntry, byte[]> read,
        int depth,
        bool expandNested,
        string sourceMod = "")
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
                if (TryExpandNested(archive, entry, read, depth, normalized, sourceMod))
                {
                    continue;
                }
            }

            Insert(
                normalized,
                new BnkFile(archive.ArchivePath, entry, normalized, read, sourceMod: sourceMod),
                preferNew: sourceMod.Length != 0);
        }
    }

    private bool TryExpandNested(
        BnkArchive archive,
        BnkEntry entry,
        Func<BnkEntry, byte[]> read,
        int depth,
        string normalized,
        string sourceMod = "")
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
                    nestedRead,
                    sourceMod: sourceMod));
            }

            _containers[normalized] = new BnkContainer(normalized, archive.ArchivePath, contents);
            Index(nested, nestedRead, depth + 1, true, sourceMod);
        }

        Insert(
            normalized,
            new BnkFile(archive.ArchivePath, entry, normalized, read, sourceMod: sourceMod),
            preferNew: sourceMod.Length != 0);
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
        bool isLoose = false,
        string sourceMod = "")
    {
        SourceArchivePath = sourceArchivePath;
        Entry = entry;
        VirtualPath = virtualPath;
        _read = read;
        IsLoose = isLoose;
        SourceMod = sourceMod;
    }

    public string SourceArchivePath { get; }

    public bool IsLoose { get; }

    /// <summary>The mod that contributed this file, or empty for the game's own data.</summary>
    public string SourceMod { get; }

    /// <summary>
    /// Whether this file BEAT something rather than being part of the shipped set: a loose file
    /// over a bank entry, or anything a mod contributed. See <see cref="ReadAllBytes"/>.
    /// </summary>
    public bool IsOverride => IsLoose || SourceMod.Length != 0;

    public string SourceArchiveName => Path.GetFileName(SourceArchivePath);

    public BnkEntry Entry { get; }

    public string VirtualPath { get; }

    public uint Size => Entry.Size;

    // Lazy: only the few files shadowed by another bank need a list; an eager empty list per
    // file doubles the object count the GC walks.
    private List<BnkFile>? _alternates;

    internal List<BnkFile> Alternates => _alternates ??= new();

    /// <summary>Whether this name is served by more than one bank entry; see ReadAllBytes.</summary>
    public bool HasAlternates => _alternates is { Count: > 0 };

    /// <summary>Everything this file shadows, nearest first. The master list reads it.</summary>
    public IReadOnlyList<BnkFile> Shadowed => (IReadOnlyList<BnkFile>?)_alternates ?? System.Array.Empty<BnkFile>();

    internal byte[] ReadOwn() => _read(Entry);

    /// <summary>
    /// The file's bytes, header entry + payload entry rejoined. A .mdl/.tex ships as two entries
    /// under one name (header in `*_headers.bnk`, payload in a models/textures bank); the guest
    /// byte-ranges one logical file, LOD i at 104 + preceding LOD sizes (`sub_82AB4868`).
    /// The header is identified by magic, not by which entry won <see cref="BnkFileSystem.Insert"/>.
    /// </summary>
    public byte[] ReadAllBytes()
    {
        // AN OVERRIDE REPLACES; IT NEVER REJOINS. The join below reunites a header entry with a
        // payload entry, which is a BANK-PACKING artefact — one logical file stored as two. A
        // loose file or a mod's file is a whole asset that beat those entries, so joining it to
        // the bank's payload hands back the SHIPPED texture and drops the override, which reads
        // as "my mod does nothing" with no error anywhere.
        if (IsOverride)
        {
            return ReadOwn();
        }

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

        foreach (BnkFile alternate in Shadowed)
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
                // Two headers or two payloads: the same file in several banks, nothing to join.
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

        var parts = new List<BnkFile>(Shadowed.Count + 1) { this };
        parts.AddRange(Shadowed);

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
    /// A model file header. "DefMeshF" is the failed-export placeholder; the guest parses it as
    /// an ordinary header (<c>sub_82AB4DC8</c> at 0x82AB4E74).
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
