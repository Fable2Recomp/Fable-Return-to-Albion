using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Refii.Bnk;

public sealed class BnkFileSystem : IDisposable
{
    public const int MaxNestingDepth = 4;

    /// <summary>
    /// One directory. 134,000 of these hold 280,000 files in Fable 2, so a tree of objects per
    /// entry was tens of MB the GC walks: sorted arrays, and none for a kind a directory lacks.
    /// </summary>
    private sealed class Node
    {
        private static readonly SortedList<string, Node> NoDirectories = new(0);
        private static readonly SortedList<string, BnkFile> NoFiles = new(0);
        private SortedList<string, Node>? _directories;
        private SortedList<string, BnkFile>? _files;

        public SortedList<string, Node> Directories => _directories ?? NoDirectories;

        public SortedList<string, BnkFile> Files => _files ?? NoFiles;

        public SortedList<string, Node> WritableDirectories =>
            _directories ??= new SortedList<string, Node>(StringComparer.OrdinalIgnoreCase);

        public SortedList<string, BnkFile> WritableFiles =>
            _files ??= new SortedList<string, BnkFile>(StringComparer.OrdinalIgnoreCase);
    }

    private readonly Node _root = new();
    private readonly List<BnkArchive> _archives = new();
    private readonly List<string> _failures = new();
    private readonly Dictionary<string, BnkContainer> _containers =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The <c>.dat</c> files on disk that nested header banks read their data from.</summary>
    private readonly Dictionary<string, BnkMappedRange> _dataFiles = new(StringComparer.Ordinal);
    private readonly List<FileStream> _dataFileStreams = new();

    // Android keeps a smaller budget for its smaller managed heap; see BnkNestedCachePolicy.
    private static readonly long NestedCacheBudget = BnkNestedCachePolicy.DefaultBytes(Refii.Core.Engine_Platform.IsAndroid);

    /// <summary>
    /// What the nested-bank cache is actually doing: managed bytes resident, containers loaded,
    /// and containers evicted.
    /// </summary>
    /// <remarks>
    /// A container stored UNCOMPRESSED in a mapped outer bank — every region bank in levels.bnk
    /// and streaming.bnk, every texture bank — is a <see cref="BnkMappedRange"/> over the outer
    /// file: no managed copy, nothing on the large-object heap, and residency is the page
    /// cache's. Those count in <see cref="NestedMappedBytes"/>, stay open once parsed (the parse
    /// is a table, not the bytes) and are never evicted.
    ///
    /// Only a container stored compressed in its outer bank (globals_models.bnk, the script
    /// banks) is inflated to an array, and the budget below bounds those. Each is a
    /// multi-megabyte LOH object and a miss re-reads the whole container, so a budget under the
    /// working set trades resident bytes for garbage.
    /// </remarks>
    public static long NestedResidentBytes;

    /// <summary>Bytes of nested containers served from the outer banks' memory maps.</summary>
    public static long NestedMappedBytes;

    public static long NestedLoads;

    public static long NestedEvictions;

    private sealed class NestedSlot
    {
        public required string Key;
        public required BnkEntryReader ReadOuter;
        public required BnkEntry Container;
        /// <summary>The container's bytes in the outer bank's map; null when it must be copied.</summary>
        public BnkMappedRange? Map;
        /// <summary>Gives a separate-data container its <c>.dat</c> each time it is opened.</summary>
        public Action<BnkArchive>? AttachData;
        public readonly object LoadLock = new();
        public volatile BnkArchive? Archive;
        public long Bytes;
        public LinkedListNode<NestedSlot>? Node;
    }

    private readonly object _nestedLock = new();
    private readonly LinkedList<NestedSlot> _nestedLoaded = new();
    private long _nestedLoadedBytes;

    private BnkArchive NestedArchive(NestedSlot slot)
    {
        BnkArchive? archive = slot.Archive;
        if (archive is null)
        {
            lock (slot.LoadLock)
            {
                archive = slot.Archive;
                if (archive is null && slot.Map is { } map)
                {
                    if (!BnkArchive.TryOpenMapped(slot.Key, map, out archive, out string error) ||
                        archive is null)
                    {
                        throw new InvalidDataException(error);
                    }

                    slot.AttachData?.Invoke(archive);
                    slot.Archive = archive;
                    System.Threading.Interlocked.Increment(ref NestedLoads);
                    System.Threading.Interlocked.Add(ref NestedMappedBytes, map.Length);

                    // A mapped table costs nothing to keep, but its .dat may have been copied out
                    // of a compressed outer entry: that copy is held, and counted, like any other.
                    if (archive.AttachedHeapBytes > 0)
                    {
                        slot.Bytes = archive.AttachedHeapBytes;
                        Retain(slot);
                    }
                }
                else if (archive is null)
                {
                    byte[] container = slot.ReadOuter.Read(slot.Container);
                    if (!BnkArchive.TryOpenBytes(
                            slot.Key, container, out archive, out string error) ||
                        archive is null)
                    {
                        throw new InvalidDataException(error);
                    }

                    slot.AttachData?.Invoke(archive);

                    // THE DATA COUNTS, NOT JUST THE TABLE. A separate-data bank's table is a few KB
                    // and its .dat, copied out of the outer bank, can be hundreds of MB; counting
                    // only the table let Fable 3's boot texture sweep hold every level and
                    // streaming bank it touched — 10.9 GB before the budget saw 1 GB.
                    slot.Bytes = container.LongLength + archive.AttachedHeapBytes;
                    slot.Archive = archive;
                    System.Threading.Interlocked.Increment(ref NestedLoads);
                    Retain(slot);
                }
            }
        }

        Touch(slot);
        return archive;
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

    /// <summary>The <c>.dat</c> files on disk that nested header banks read their data from.</summary>
    public IEnumerable<string> DataFilePaths => _dataFiles.Keys;

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
        IReadOnlyList<(string Name, string AssetRoot)>? overlays = null,
        string? updateRoot = null)
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
                filesystem.Index(archive, BnkEntryReader.Of(archive), 0, expandNested, gameDirectory);
            }
            else
            {
                filesystem._failures.Add($"{Path.GetFileName(bank)}: {error}");
            }
        }

        // THE TITLE UPDATE'S BANK OVER THE GAME'S. The guest layers tu1_data.bnk over its data
        // registry; what this mount serves in its place has to be the same files, so the bank is
        // indexed after the game's banks and before the loose files and mods that shadow both.
        filesystem.IndexUpdateRoot(updateRoot, expandNested);

        if (looseExtensions is null)
        {
            filesystem.IndexAllLoose(gameDirectory);
        }
        else
        {
            filesystem.IndexLoose(gameDirectory, looseExtensions);
        }

        Refii.Core.Engine_TitleGame.Active.IndexLooseData(filesystem, gameDirectory);

        // MODS LAST, so a mod can override anything the game shipped — the title's loose data
        // indexed just above included.
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
    /// The banks under the update root, each file winning over the game's copy. Nothing under
    /// it is a mod: the files carry no source and the mods index does not list them.
    /// </summary>
    private void IndexUpdateRoot(string? updateRoot, bool expandNested)
    {
        if (string.IsNullOrEmpty(updateRoot) || !Directory.Exists(updateRoot))
        {
            return;
        }

        IEnumerable<string> banks;
        try
        {
            banks = EnumerateByExtension(updateRoot, ".bnk").ToList();
        }
        catch (Exception exception)
        {
            _failures.Add($"{updateRoot}: {exception.Message}");
            return;
        }

        foreach (string bank in banks.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            if (BnkArchive.TryOpen(bank, out BnkArchive? archive, out string error) &&
                archive is not null)
            {
                _archives.Add(archive);
                Index(archive, BnkEntryReader.Of(archive), 0, expandNested, updateRoot, preferNew: true);
            }
            else
            {
                _failures.Add($"{Path.GetFileName(bank)}: {error}");
            }
        }
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

        if (File.Exists(assetRoot) && Refii.Core.Engine_Title.IsModPackagePath(assetRoot))
        {
            if (Refii.Mods.F2ModPackage.TryOpen(assetRoot, out Refii.Mods.F2ModPackage? package, out string pkgError) && package is not null)
            {
                foreach (Refii.Mods.F2ModEntry entry in package.Entries)
                {
                    // native/ is compiled into the game module by the launcher, not served.
                    if (IsModBookkeeping(Path.GetFileName(entry.Path)) ||
                        Refii.Mods.ModNative.IsNativePath(entry.Path) ||
                        Refii.Mods.ModLua.IsModLuaPath(entry.Path))
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
                Index(archive, BnkEntryReader.Of(archive), 0, expandNested, assetRoot, name);
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
        node.WritableFiles[split < 0 ? normalized : normalized[(split + 1)..]] = promoted;
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
                IsBankData(path) ||
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
    /// The one culture whose files also answer to their locale-less spelling: the culture the
    /// guest runs in (<see cref="Refii.Text.Engine_GameCulture.Current"/>) — the install's own
    /// until the guest states one, so a disc that ships only es-es aliases es-es.
    /// </summary>
    public static string ActiveCulture => Refii.Text.Engine_GameCulture.Current;

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
        ".babel.csv", ".gdb.csv", ".tex.png", ".mdl.glb", ".mdl.gltf", ".save.tu",
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

    /// <summary>True when <paramref name="segment"/> is a culture folder: a code of the install's
    /// <c>cultures.txt</c>, or one of <see cref="KnownCultures"/>.</summary>
    private static bool IsAnyCultureFolder(string segment) =>
        KnownCultures.Contains(segment) || Refii.Text.Engine_GameCulture.IsCultureCode(segment);

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

    /// <summary>
    /// A separate-data bank's <c>.dat</c> is the bank's bytes, not a file of its own: the guest
    /// reaches it only through the header's bank, so the mount does the same.
    /// </summary>
    private static bool IsBankData(string path) =>
        path.EndsWith(".bnk.dat", StringComparison.OrdinalIgnoreCase);

    /// <param name="dataRoot">The directory the archive's entry names are relative to, where a
    /// nested header bank's <c>.dat</c> lies when no entry of <paramref name="archive"/> holds it.</param>
    private void Index(
        BnkArchive archive,
        BnkEntryReader read,
        int depth,
        bool expandNested,
        string dataRoot,
        string sourceMod = "",
        bool? preferNew = null)
    {
        Dictionary<string, BnkEntry>? bankData = null;
        foreach (BnkEntry entry in archive.Entries)
        {
            if (IsBankData(entry.RelativePath))
            {
                bankData ??= new Dictionary<string, BnkEntry>(StringComparer.OrdinalIgnoreCase);
                bankData[Normalize(entry.RelativePath)] = entry;
            }
        }

        foreach (BnkEntry entry in archive.Entries)
        {
            string normalized = Normalize(entry.RelativePath);
            if (normalized.Length == 0 || IsBankData(normalized))
            {
                continue;
            }

            bool isNestedBank = normalized.EndsWith(".bnk", StringComparison.OrdinalIgnoreCase);
            if (isNestedBank && expandNested && depth < MaxNestingDepth)
            {
                BnkEntry? data = null;
                bankData?.TryGetValue(normalized + ".dat", out data);
                if (TryExpandNested(archive, entry, read, depth, normalized, data, dataRoot, sourceMod))
                {
                    continue;
                }
            }

            Insert(
                normalized,
                new BnkFile(archive.ArchivePath, entry, normalized, read, sourceMod: sourceMod),
                preferNew: preferNew ?? sourceMod.Length != 0);
        }
    }

    private bool TryExpandNested(
        BnkArchive archive,
        BnkEntry entry,
        BnkEntryReader read,
        int depth,
        string normalized,
        BnkEntry? dataEntry,
        string dataRoot,
        string sourceMod = "")
    {
        BnkArchive? nested = null;
        BnkMappedRange? map = null;
        if (archive.TryMapEntry(entry, out map) && map is not null)
        {
            if (!BnkArchive.TryOpenMapped(normalized, map, out nested, out string mapError) ||
                nested is null)
            {
                _failures.Add($"{normalized}: {mapError}");
                return false;
            }
        }
        else
        {
            map = null;
            byte[] bytes;
            try
            {
                bytes = read.Read(entry);
            }
            catch (Exception exception)
            {
                _failures.Add($"{normalized}: {exception.Message}");
                return false;
            }

            if (!BnkArchive.TryOpenBytes(normalized, bytes, out nested, out string error) ||
                nested is null)
            {
                _failures.Add($"{normalized}: {error}");
                return false;
            }
        }

        Action<BnkArchive>? attach = null;
        if (nested.DataIsSeparate)
        {
            attach = ResolveBankData(archive, read, normalized, dataEntry, dataRoot);
            if (attach is null)
            {
                nested.Dispose();
                _failures.Add($"{normalized}: its data {normalized}.dat is in no mounted bank and not on disk");
                return false;
            }

            attach(nested);
        }

        var slot = new NestedSlot
        {
            Key = normalized, ReadOuter = read, Container = entry, Map = map, AttachData = attach,
        };
        var nestedRead = new BnkEntryReader(
            target => NestedArchive(slot).ReadEntryBytes(target),
            (target, destination, offset) => NestedArchive(slot).ReadEntryInto(target, destination, offset),
            (target, count) => NestedArchive(slot).ReadEntryHead(target, count),
            (target, destination) => NestedArchive(slot).ReadEntryRange(target, destination));

        try
        {
            ++NestedArchiveCount;
            IReadOnlyList<BnkEntry> nestedEntries = nested.Entries;
            _containers[normalized] = new BnkContainer(normalized, archive.ArchivePath, () =>
            {
                var contents = new List<BnkFile>(nestedEntries.Count);
                foreach (BnkEntry nestedEntry in nestedEntries)
                {
                    contents.Add(new BnkFile(
                        normalized,
                        nestedEntry,
                        Normalize(nestedEntry.RelativePath),
                        nestedRead,
                        sourceMod: sourceMod));
                }

                return contents;
            });
            Index(nested, nestedRead, depth + 1, true, dataRoot, sourceMod);
        }
        finally
        {
            // A MAPPED CONTAINER IS KEPT OPEN FROM HERE. Its table is the one just parsed, whose
            // entries the files above already hold, and reading it copies nothing but the entry
            // asked for. Closed, its first read parsed the table again: a second set of entries
            // and path strings for every region bank the game touched, kept for the session,
            // and the parse paid on that first read.
            if (map is null)
            {
                nested.Dispose();
            }
        }

        if (map is not null)
        {
            slot.Archive = nested;
            System.Threading.Interlocked.Increment(ref NestedLoads);
            System.Threading.Interlocked.Add(ref NestedMappedBytes, map.Length);
        }

        Insert(
            normalized,
            new BnkFile(archive.ArchivePath, entry, normalized, read, sourceMod: sourceMod),
            preferNew: sourceMod.Length != 0);
        return true;
    }

    /// <summary>
    /// Where a nested header bank's data is: the sibling <c>&lt;bank&gt;.dat</c> entry of the
    /// bank that holds the header (every nested bank in levels.bnk and streaming.bnk), else the
    /// file at the header's own path under the data root (globals_textures.bnk's header is in
    /// levels.bnk, its data on the disc). Null when neither exists.
    /// </summary>
    private Action<BnkArchive>? ResolveBankData(
        BnkArchive outer,
        BnkEntryReader readOuter,
        string normalized,
        BnkEntry? dataEntry,
        string dataRoot)
    {
        if (dataEntry is not null)
        {
            if (outer.TryMapEntry(dataEntry, out BnkMappedRange? map) && map is not null)
            {
                return nested => nested.AttachData(map.OpenStream(), map, ownsRange: false);
            }

            return nested => nested.AttachData(new MemoryStream(readOuter.Read(dataEntry), false), null, ownsRange: false);
        }

        // Under the data root first; then beside the bank on disk that holds the header. Fable 3's
        // nested names are relative to the directory levels.bnk is in (`art\gui\gui_textures.bnk`
        // in data/levels.bnk, its bytes at data/art/gui/gui_textures.bnk.dat), so a mount rooted
        // above data/ finds the payload only there.
        string diskPath = dataRoot.Length == 0 ? string.Empty : Path.Combine(dataRoot, normalized + ".dat");
        if (diskPath.Length == 0 || !File.Exists(diskPath))
        {
            string? outerDirectory = Path.IsPathRooted(outer.ArchivePath) ? Path.GetDirectoryName(outer.ArchivePath) : null;
            diskPath = string.IsNullOrEmpty(outerDirectory) ? string.Empty : Path.Combine(outerDirectory, normalized + ".dat");
            if (diskPath.Length == 0 || !File.Exists(diskPath))
            {
                return null;
            }
        }

        if (!_dataFiles.TryGetValue(diskPath, out BnkMappedRange? file))
        {
            var stream = new FileStream(
                diskPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (!BnkMappedRange.TryMapFile(stream, out file) || file is null)
            {
                stream.Dispose();
                return null;
            }

            _dataFileStreams.Add(stream);
            _dataFiles[diskPath] = file;
        }

        BnkMappedRange whole = file;
        return nested => nested.AttachData(whole.OpenStream(), whole, ownsRange: false);
    }

    /// <summary>The directory the last <see cref="Insert"/> walked to: a bank lists a folder's files together.</summary>
    private string _lastDirectory = string.Empty;

    private Node? _lastDirectoryNode;

    private Node DirectoryForInsert(string directory)
    {
        Node node = _root;
        foreach (string part in directory.Split('/'))
        {
            if (!node.Directories.TryGetValue(part, out Node? child))
            {
                child = new Node();
                node.WritableDirectories[part] = child;
            }

            node = child;
        }

        _lastDirectory = directory;
        _lastDirectoryNode = node;
        return node;
    }

    private void Insert(string normalized, BnkFile file, bool preferNew = false)
    {
        int split = normalized.LastIndexOf('/');
        Node node = _root;
        string name = normalized;
        if (split >= 0)
        {
            ReadOnlySpan<char> directory = normalized.AsSpan(0, split);
            node = _lastDirectoryNode is not null && directory.SequenceEqual(_lastDirectory)
                ? _lastDirectoryNode
                : DirectoryForInsert(directory.ToString());
            name = normalized[(split + 1)..];
        }

        if (!node.Files.TryGetValue(name, out BnkFile? existing))
        {
            ++FileCount;
            node.WritableFiles[name] = file;
            return;
        }

        ++ShadowedFileCount;
        // The same name in several banks needs its path only once.
        file.SharePath(existing.VirtualPath);

        if (preferNew || file.Size > existing.Size)
        {
            node.WritableFiles[name] = file;
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

        foreach (BnkMappedRange file in _dataFiles.Values)
        {
            file.Dispose();
        }

        foreach (FileStream stream in _dataFileStreams)
        {
            stream.Dispose();
        }

        _dataFiles.Clear();
        _dataFileStreams.Clear();
    }
}

public sealed class BnkContainer
{
    private readonly Lazy<IReadOnlyList<BnkFile>> _files;

    internal BnkContainer(string virtualPath, string hostArchivePath, Func<IReadOnlyList<BnkFile>> files)
    {
        VirtualPath = virtualPath;
        HostArchivePath = hostArchivePath;
        _files = new Lazy<IReadOnlyList<BnkFile>>(files);
    }

    public string VirtualPath { get; }

    public string HostArchivePath { get; }

    /// <summary>The bank's own entries, made when first asked: the index already holds its winners.</summary>
    public IReadOnlyList<BnkFile> Files => _files.Value;

    public string Name => Path.GetFileName(VirtualPath);

    public override string ToString() => $"{VirtualPath} ({Files.Count} entries)";
}

/// <summary>
/// How a bank file reaches its entry. <see cref="ReadInto"/> and <see cref="ReadHead"/> exist for
/// entries in a bank; a loose or virtual file has only <see cref="Read"/>.
/// </summary>
/// <summary>The entry's first <c>destination.Length</c> bytes into <paramref name="destination"/>; see <see cref="BnkArchive.ReadEntryRange"/>.</summary>
internal delegate void BnkRangeReader(BnkEntry entry, Memory<byte> destination);

internal sealed class BnkEntryReader(
    Func<BnkEntry, byte[]> read,
    Action<BnkEntry, byte[], int>? readInto = null,
    Func<BnkEntry, int, byte[]>? readHead = null,
    BnkRangeReader? readRange = null)
{
    public readonly Func<BnkEntry, byte[]> Read = read;
    public readonly Action<BnkEntry, byte[], int>? ReadInto = readInto;
    public readonly Func<BnkEntry, int, byte[]>? ReadHead = readHead;
    public readonly BnkRangeReader? ReadRange = readRange;

    public static BnkEntryReader Of(BnkArchive archive) =>
        new(archive.ReadEntryBytes, archive.ReadEntryInto, archive.ReadEntryHead, archive.ReadEntryRange);
}

public sealed class BnkFile
{
    private readonly BnkEntryReader _read;

    internal BnkFile(
        string sourceArchivePath,
        BnkEntry entry,
        string virtualPath,
        Func<BnkEntry, byte[]> read,
        bool isLoose = false,
        string sourceMod = "")
        : this(sourceArchivePath, entry, virtualPath, new BnkEntryReader(read), isLoose, sourceMod)
    {
    }

    internal BnkFile(
        string sourceArchivePath,
        BnkEntry entry,
        string virtualPath,
        BnkEntryReader read,
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

    public string VirtualPath { get; private set; }

    internal void SharePath(string path)
    {
        if (string.Equals(VirtualPath, path, StringComparison.Ordinal))
        {
            VirtualPath = path;
            Entry.SharePath(path);
        }
    }

    public uint Size => Entry.Size;

    // Lazy: only the few files shadowed by another bank need a list; an eager empty list per
    // file doubles the object count the GC walks.
    private List<BnkFile>? _alternates;

    internal List<BnkFile> Alternates => _alternates ??= new();

    /// <summary>Whether this name is served by more than one bank entry; see ReadAllBytes.</summary>
    public bool HasAlternates => _alternates is { Count: > 0 };

    /// <summary>Cached combined length once <see cref="ReadAllBytes"/> has joined alternates.</summary>
    public long CachedResolvedSize { get; private set; } = -1;

    /// <summary>Everything this file shadows, nearest first. The master list reads it.</summary>
    public IReadOnlyList<BnkFile> Shadowed => (IReadOnlyList<BnkFile>?)_alternates ?? System.Array.Empty<BnkFile>();

    internal byte[] ReadOwn() => _read.Read(Entry);

    /// <summary>
    /// The file's bytes, header entry + payload entry rejoined. A .mdl/.tex ships as two entries
    /// under one name (header in `*_headers.bnk`, payload in a models/textures bank); the guest
    /// byte-ranges one logical file, LOD i at 104 + preceding LOD sizes (`sub_82AB4868`).
    /// The header is identified by magic, not by which entry won <see cref="BnkFileSystem.Insert"/>.
    /// </summary>
    public byte[] ReadAllBytes()
    {
        byte[] result = ReadAllBytesCore();
        CachedResolvedSize = result.LongLength;
        return result;
    }

    /// <summary>
    /// The first <c>destination.Length</c> bytes of what <see cref="ReadAllBytes"/> returns, written
    /// into <paramref name="destination"/>; answers how many, fewer when the file is shorter. A file
    /// that is one bank entry is read straight into it with no array of its own; a joined model or
    /// texture, or a file with no range reader, is read as before and copied.
    /// </summary>
    public int ReadInto(Memory<byte> destination)
    {
        if ((IsOverride || !HasAlternates) && _read.ReadRange is { } readRange)
        {
            int count = (int)Math.Min((uint)destination.Length, Entry.Size);
            readRange(Entry, destination[..count]);
            return count;
        }

        byte[] bytes = CachedResolvedSize >= 0 && destination.Length < CachedResolvedSize
            ? ReadHead(destination.Length)
            : ReadAllBytes();
        int written = Math.Min(bytes.Length, destination.Length);
        bytes.AsSpan(0, written).CopyTo(destination.Span);
        return written;
    }

    /// <summary>
    /// At least the first <paramref name="count"/> bytes of what <see cref="ReadAllBytes"/> returns
    /// (fewer only when the file is shorter), reading no more of the bank than that needs. Only the
    /// rejoined forms of a model and a texture are worked out here; anything else with alternates
    /// is read whole.
    /// </summary>
    public byte[] ReadHead(int count)
    {
        if (IsOverride || !HasAlternates)
        {
            return _read.ReadHead?.Invoke(Entry, count) ?? ReadOwn();
        }

        if (VirtualPath.EndsWith(".tex", StringComparison.OrdinalIgnoreCase) &&
            TryPlanTexture(out byte[] texHeader, out BnkFile? whole, out BnkFile? mip0, out _))
        {
            if (texHeader.Length >= count)
            {
                return texHeader;
            }

            // The header is shorter than asked, so the payload's start follows it.
            BnkFile next = whole ?? mip0!;
            int more = count - texHeader.Length;
            if (next._read.ReadHead is { } readNext && next.Entry.Size >= more)
            {
                byte[] head = new byte[count];
                texHeader.CopyTo(head, 0);
                readNext(next.Entry, more).CopyTo(head, texHeader.Length);
                return head;
            }

            return ReadAllBytes();
        }

        if (!VirtualPath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase) || _read.ReadHead is null)
        {
            return ReadAllBytes();
        }

        // THE JOIN'S FRONT IS ITS HEADER: this entry when it is one, else the first alternate that
        // is, else this entry alone. See ReadAllBytesCore.
        BnkFile front = this;
        if (!HasMeshFileMagic(_read.ReadHead(Entry, MeshMagicLength)))
        {
            foreach (BnkFile alternate in Shadowed)
            {
                if (alternate._read.ReadHead is not { } readAlternate)
                {
                    return ReadAllBytes();
                }

                try
                {
                    if (HasMeshFileMagic(readAlternate(alternate.Entry, MeshMagicLength)))
                    {
                        front = alternate;
                        break;
                    }
                }
                catch (Exception)
                {
                }
            }
        }

        // A front shorter than asked is continued by the payload, so the join has to be made.
        return front.Entry.Size < count ? ReadAllBytes() : front._read.ReadHead!(front.Entry, count);
    }

    private byte[] ReadAllBytesCore()
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

        if (HasAlternates &&
            VirtualPath.EndsWith(".tex", StringComparison.OrdinalIgnoreCase) &&
            TryReadTexture(out byte[] texture))
        {
            return texture;
        }

        if (!HasAlternates)
        {
            return _read.Read(Entry);
        }

        if (VirtualPath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase) && TryReadModel(out byte[] model))
        {
            return model;
        }

        byte[] own = _read.Read(Entry);

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
                other = alternate._read.Read(alternate.Entry);
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
        if (!TryPlanTexture(out byte[] header, out BnkFile? whole, out BnkFile? mip0, out BnkFile? rest))
        {
            return false;
        }

        try
        {
            bytes = whole is not null ? Join(header, whole, null) : Join(header, mip0!, rest);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Which entries make up this texture: its header's bytes, and either the one entry holding the
    /// whole payload or the two holding mip 0 and the rest. False when they do not all exist.
    /// </summary>
    private bool TryPlanTexture(out byte[] headerBytes, out BnkFile? whole, out BnkFile? mip0, out BnkFile? rest)
    {
        headerBytes = Array.Empty<byte>();
        whole = null;
        mip0 = null;
        rest = null;

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
                candidate = part._read.Read(part.Entry);
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
        long mip0Size = parsed.LevelCount >= 2 && parsed.LevelOffsets[1] > parsed.HeaderSize
            ? parsed.LevelOffsets[1] - parsed.HeaderSize
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

        headerBytes = header;
        return whole is not null || (mip0 is not null && rest is not null);
    }

    /// <summary>
    /// The header and payload of a model, read straight into the one array they end up in. False
    /// when that cannot be done, and the general path below then decides what the read returns.
    /// </summary>
    private bool TryReadModel(out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        try
        {
            if (_read.ReadHead is null)
            {
                return false;
            }

            bool ownIsHeader = HasMeshFileMagic(_read.ReadHead(Entry, MeshMagicLength));
            foreach (BnkFile alternate in Shadowed)
            {
                if (alternate._read.ReadHead is null)
                {
                    return false;
                }

                byte[] head;
                try
                {
                    head = alternate._read.ReadHead(alternate.Entry, MeshMagicLength);
                }
                catch (Exception)
                {
                    continue;
                }

                if (HasMeshFileMagic(head) != ownIsHeader)
                {
                    bytes = ownIsHeader ? Join(null, this, alternate) : Join(null, alternate, this);
                    return true;
                }
            }
        }
        catch (Exception)
        {
        }

        return false;
    }

    /// <summary>
    /// <paramref name="prefix"/>, then <paramref name="first"/>'s entry, then
    /// <paramref name="second"/>'s, in one array. Entries in a bank are read directly into it.
    /// </summary>
    private static byte[] Join(byte[]? prefix, BnkFile first, BnkFile? second)
    {
        int prefixLength = prefix?.Length ?? 0;
        if (first._read.ReadInto is not { } readFirst ||
            (second is not null && second._read.ReadInto is null))
        {
            byte[] a = first._read.Read(first.Entry);
            byte[] b = second?._read.Read(second.Entry) ?? Array.Empty<byte>();
            var copied = new byte[prefixLength + a.Length + b.Length];
            prefix?.CopyTo(copied, 0);
            Buffer.BlockCopy(a, 0, copied, prefixLength, a.Length);
            Buffer.BlockCopy(b, 0, copied, prefixLength + a.Length, b.Length);
            return copied;
        }

        int firstSize = checked((int)first.Entry.Size);
        int total = checked(prefixLength + firstSize + (int)(second?.Entry.Size ?? 0));

        // The prefix and the entries' reads write every byte.
        byte[] joined = GC.AllocateUninitializedArray<byte>(total);
        prefix?.CopyTo(joined, 0);
        readFirst(first.Entry, joined, prefixLength);
        second?._read.ReadInto!(second.Entry, joined, prefixLength + firstSize);
        return joined;
    }

    private const int MeshMagicLength = 8;

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
        (d.Length >= 84 && (d[0] & 0x80) != 0 && d[0] == 0xFF) || Refii.Textures.TexHeader.IsFable3Header(d);

    public override string ToString() => VirtualPath;
}
