using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Refii.AndroidRuntime;

/// <summary>Compiled translation units kept between phone builds, found by what they were built from.</summary>
/// <remarks>
/// <para>An update rarely changes much of the game code: of 23 consecutive APK bundles, one touched a
/// header the generated units include, yet every changed bundle compiled all 383 units again. A killed
/// build lost its finished units the same way. An object here is named by the recipe, the unit's name
/// and its source bytes, and is reused only while every file the compiler reported reading (clang -MD)
/// still has the bytes it had. An interrupted build's objects count, so a new build resumes it.</para>
/// <para>Bundle files are compared by the hashes in the validated bundle.json, generated files by
/// hashing them once. A file the compiler read outside the bundle and generated folders makes its unit
/// uncacheable rather than guessed at. A new file an include could now find first, which no recorded hash
/// would show, also counts as a change (<see cref="SameNames"/>).</para>
/// <para>Each store is atomic and keeps the object's hash, so a torn or damaged object is a miss. The
/// directory is app-private, as the module is, and holds only what the last success used plus what later
/// failures left: about 100 MB of objects and 10 MB of records for Fable 2.</para>
/// </remarks>
internal sealed class AndroidCompileCache
{
    /// <summary>The bundle's files in a dependency record's names.</summary>
    private const string BundlePrefix = "bundle/";
    /// <summary>The generator's files in a dependency record's names.</summary>
    private const string GeneratedPrefix = "generated/";

    private readonly string _root, _bundle, _generated, _recipe;
    private readonly Dictionary<string, string> _files;
    private readonly ILookup<string, string> _byFileName;
    private readonly IReadOnlyCollection<string> _includes;
    private readonly Dictionary<string, string?> _hashes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _pchRecords = new(StringComparer.Ordinal);
    private readonly HashSet<string> _used = new(StringComparer.Ordinal);
    private readonly SortedSet<string> _bundleInputs = new(StringComparer.Ordinal);
    private HashSet<string> _pchNames = new(StringComparer.Ordinal);
    private string? _pchId;

    /// <summary>Whether every unit of this build has a complete record, so <see cref="BundleInputs"/> is the module's whole input.</summary>
    public bool Complete { get; private set; } = true;

    /// <summary>The bundle files this build's units read, by bundle-relative path.</summary>
    public IReadOnlyCollection<string> BundleInputs => _bundleInputs;

    /// <summary>Opens the cache for one build.</summary>
    /// <param name="root">The app-private cache directory; created when absent.</param>
    /// <param name="bundleDirectory">The extracted and validated compiler bundle.</param>
    /// <param name="generated">This attempt's generated source directory.</param>
    /// <param name="files">The validated bundle's file hashes.</param>
    /// <param name="includes">This build's bundle-relative include directories.</param>
    /// <param name="recipe">What else decides an object's bytes (<see cref="AndroidPhoneCompiler.UnitRecipe"/>).</param>
    public AndroidCompileCache(string root, string bundleDirectory, string generated, Dictionary<string, string> files,
        IReadOnlyCollection<string> includes, string recipe)
    {
        _includes = includes;
        _root = root;
        _bundle = Path.TrimEndingDirectorySeparator(Path.GetFullPath(bundleDirectory));
        _generated = Path.TrimEndingDirectorySeparator(Path.GetFullPath(generated));
        _files = files;
        _byFileName = FileNameIndex(files);
        _recipe = recipe;
        Directory.CreateDirectory(root);
    }

    /// <summary>The bundle's paths by file name, ordered, for the same-name check.</summary>
    /// <param name="files">A bundle's file hashes.</param>
    public static ILookup<string, string> FileNameIndex(Dictionary<string, string> files) =>
        files.Keys.OrderBy(path => path, StringComparer.Ordinal).ToLookup(path => path[(path.LastIndexOf('/') + 1)..], StringComparer.Ordinal);

    /// <summary>The files an include of one of these inputs could have found: same file name, below an
    /// include directory or below a folder holding an input (an include searches its own file's folder first).</summary>
    /// <param name="bundlePaths">Bundle-relative input paths.</param>
    /// <param name="index">The bundle's <see cref="FileNameIndex"/>.</param>
    /// <param name="includes">The bundle-relative include directories.</param>
    /// <returns>A hash that changes when such a file appears or goes. Unrelated files in the bundle, such as
    /// a third-party tree's own allocator.h, are not reachable and leave it alone.</returns>
    public static string SameNames(IEnumerable<string> bundlePaths, ILookup<string, string> index, IEnumerable<string> includes)
    {
        var inputs = bundlePaths.ToList();
        var folders = new HashSet<string>(includes, StringComparer.Ordinal);
        foreach (string path in inputs) folders.Add(path[..Math.Max(0, path.LastIndexOf('/'))]);
        bool Reachable(string path)
        {
            for (int end = path.LastIndexOf('/'); end > 0; end = path.LastIndexOf('/', end - 1))
                if (folders.Contains(path[..end])) return true;
            return false;
        }
        var text = new StringBuilder();
        foreach (string name in inputs.Select(path => path[(path.LastIndexOf('/') + 1)..]).Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal))
        {
            text.Append(name);
            foreach (string path in index[name].Where(Reachable)) text.Append('\0').Append(path);
            text.Append('\n');
        }
        return HashText(text.ToString());
    }

    /// <summary>Hex SHA-256 of a text.</summary>
    public static string HashText(string text) =>
        Convert.ToHexString(Refii.Core.Engine_Hash.Sha256(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    /// <summary>The object for a unit whose recorded inputs are all unchanged, or null.</summary>
    /// <param name="source">The unit's source path.</param>
    /// <returns>A verified object path that later builds may keep using.</returns>
    public string? Find(string source)
    {
        try
        {
            string? name = NameOf(source);
            string? id = name == null ? null : UnitId(name);
            if (id == null) return null;
            string record = Path.Combine(_root, id + ".deps");
            string objectPath = Path.Combine(_root, id + ".o");
            if (!File.Exists(record) || !File.Exists(objectPath)) return null;
            var inputs = new List<string>();
            string objectHash = "";
            foreach (string line in File.ReadLines(record))
            {
                if (line.StartsWith("object ", StringComparison.Ordinal)) objectHash = line[7..];
                else if (line.StartsWith("pch ", StringComparison.Ordinal))
                {
                    if (!PchRecordHolds(line[4..], inputs)) return null;
                }
                else if (!Holds(line, inputs)) return null;
            }
            if (objectHash.Length == 0 || HashFile(objectPath) != objectHash) return null;
            _used.Add(id);
            _bundleInputs.UnionWith(inputs);
            AddOwnInput(name!);
            return objectPath;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException) { return null; }
    }

    /// <summary>Records the precompiled header's inputs, which its units read without opening them again.</summary>
    /// <param name="dependencies">The header's clang -MD output.</param>
    /// <param name="output">The precompiled header, which is not itself an input.</param>
    public void StorePch(string dependencies, string output)
    {
        _pchNames = new HashSet<string>(StringComparer.Ordinal);
        _pchId = null;
        if (!TryNames(dependencies, output, null, out var names))
        {
            Complete = false;
            return;
        }
        var text = new StringBuilder();
        AppendRecord(text, names);
        string content = text.ToString();
        string id = HashText(content)[..32];
        WriteAtomic(Path.Combine(_root, "pch-" + id + ".deps"), content);
        _pchNames = names.ToHashSet(StringComparer.Ordinal);
        _pchId = id;
        _pchRecords[id] = true;
        // A later build asks through PchRecordHolds, which adds these.
        _bundleInputs.UnionWith(names.Where(name => name.StartsWith(BundlePrefix, StringComparison.Ordinal)).Select(name => name[BundlePrefix.Length..]));
    }

    /// <summary>Moves a freshly compiled object into the cache, when its inputs can all be named.</summary>
    /// <param name="source">The unit's source path.</param>
    /// <param name="built">The compiled object in the attempt.</param>
    /// <param name="dependencies">Its clang -MD output.</param>
    /// <param name="usesPch">Whether it was compiled with the precompiled header.</param>
    /// <returns>Where the object now is, which is <paramref name="built"/> when it could not be cached.</returns>
    public string Store(string source, string built, string dependencies, bool usesPch)
    {
        try
        {
            string? name = NameOf(source);
            string? id = name == null ? null : UnitId(name);
            if (id == null || !TryNames(dependencies, built, name, out var names) || (usesPch && _pchId == null))
            {
                Complete = false;
                return built;
            }
            var own = usesPch ? names.Where(input => !_pchNames.Contains(input)).ToList() : names;
            var text = new StringBuilder();
            text.Append("object ").Append(HashFile(built)).Append('\n');
            if (usesPch) text.Append("pch ").Append(_pchId).Append('\n');
            AppendRecord(text, own);
            string target = Path.Combine(_root, id + ".o");
            // The object first, so a record never names a missing or older one: Find checks the hash.
            File.Move(built, target, true);
            WriteAtomic(Path.Combine(_root, id + ".deps"), text.ToString());
            _used.Add(id);
            _bundleInputs.UnionWith(own.Where(input => input.StartsWith(BundlePrefix, StringComparison.Ordinal)).Select(input => input[BundlePrefix.Length..]));
            AddOwnInput(name!);
            return target;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Out of space or similar: this build still links what it compiled, and the next compiles it again.
            Complete = false;
            return File.Exists(built) ? built : throw new IOException("A compiled unit could not be kept", error);
        }
    }

    /// <summary>Removes everything this build did not use, once it has succeeded.</summary>
    public void KeepOnlyUsed()
    {
        var pchRecords = new HashSet<string>(StringComparer.Ordinal);
        foreach (string id in _used)
        {
            string record = Path.Combine(_root, id + ".deps");
            if (!File.Exists(record)) continue;
            foreach (string line in File.ReadLines(record))
                if (line.StartsWith("pch ", StringComparison.Ordinal)) pchRecords.Add("pch-" + line[4..] + ".deps");
        }
        foreach (string file in Directory.EnumerateFiles(_root).ToList())
        {
            string name = Path.GetFileName(file);
            if (name == AndroidPhoneCompiler.PartitionFile) continue;
            bool kept = name.StartsWith("pch-", StringComparison.Ordinal) ? pchRecords.Contains(name)
                : _used.Contains(Path.GetFileNameWithoutExtension(name));
            if (!kept) File.Delete(file);
        }
    }

    /// <summary>The paths in a clang -MD file, without its target.</summary>
    /// <param name="text">The make rule clang wrote.</param>
    /// <remarks>Make's escapes: a backslash before a space or '#', '$$' for '$', and backslash-newline to continue a line.</remarks>
    public static List<string> ParseDependencies(string text)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        void Flush() { if (word.Length > 0) { words.Add(word.ToString()); word.Clear(); } }
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\\' && i + 1 < text.Length)
            {
                char next = text[i + 1];
                if (next == '\n') { Flush(); i++; continue; }
                if (next == '\r' && i + 2 < text.Length && text[i + 2] == '\n') { Flush(); i += 2; continue; }
                if (next is ' ' or '#') { word.Append(next); i++; continue; }
                word.Append(c);
            }
            else if (c == '$' && i + 1 < text.Length && text[i + 1] == '$') { word.Append('$'); i++; }
            else if (char.IsWhiteSpace(c)) Flush();
            else word.Append(c);
        }
        Flush();
        // The target ends in a colon; a path holding one is not something this build writes.
        int target = words.FindIndex(entry => entry.EndsWith(':'));
        return target < 0 ? new List<string>() : words.Skip(target + 1).ToList();
    }

    /// <summary>A unit's cache name: recipe, name and source bytes. Null when the source cannot be hashed.</summary>
    private string? UnitId(string name) =>
        CurrentHash(name) is { } hash ? HashText(_recipe + "\0" + name + "\0" + hash)[..32] : null;

    /// <summary>Adds a bundle source to the module's inputs, which its own record may not list.</summary>
    private void AddOwnInput(string name)
    {
        if (name.StartsWith(BundlePrefix, StringComparison.Ordinal)) _bundleInputs.Add(name[BundlePrefix.Length..]);
    }

    /// <summary>A path's record name, or null outside the bundle and generated folders.</summary>
    private string? NameOf(string path)
    {
        string full = Path.GetFullPath(path);
        if (full.StartsWith(_bundle + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return BundlePrefix + full[(_bundle.Length + 1)..].Replace('\\', '/');
        if (full.StartsWith(_generated + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return GeneratedPrefix + full[(_generated.Length + 1)..].Replace('\\', '/');
        return null;
    }

    /// <summary>Today's hash of a named input, or null when it is gone or unverified.</summary>
    private string? CurrentHash(string name)
    {
        if (name.StartsWith(BundlePrefix, StringComparison.Ordinal))
            return _files.TryGetValue(name[BundlePrefix.Length..], out string? recorded) ? recorded : null;
        if (_hashes.TryGetValue(name, out string? known)) return known;
        string path = Path.Combine(_generated, name[GeneratedPrefix.Length..]);
        string? hash = File.Exists(path) ? HashFile(path) : null;
        _hashes[name] = hash;
        return hash;
    }

    /// <summary>Whether one line of a record still holds; a "names" line is the same-name check over its inputs so far.</summary>
    private bool Holds(string line, List<string> inputs)
    {
        if (line.StartsWith("names ", StringComparison.Ordinal))
            return SameNames(inputs, _byFileName, _includes) == line[6..];
        int space = line.IndexOf(' ');
        if (space <= 0) throw new FormatException("Malformed compile cache record");
        string name = line[(space + 1)..];
        if (CurrentHash(name) != line[..space]) return false;
        if (name.StartsWith(BundlePrefix, StringComparison.Ordinal)) inputs.Add(name[BundlePrefix.Length..]);
        return true;
    }

    /// <summary>Whether a shared precompiled-header record still holds, checking it once per build.</summary>
    private bool PchRecordHolds(string id, List<string> inputs)
    {
        if (_pchRecords.TryGetValue(id, out bool known)) return known;
        string record = Path.Combine(_root, "pch-" + id + ".deps");
        if (!File.Exists(record)) return false;
        var own = new List<string>();
        bool holds = File.ReadLines(record).All(line => Holds(line, own));
        _pchRecords[id] = holds;
        if (holds) _bundleInputs.UnionWith(own);
        return holds;
    }

    /// <summary>A dependency file's inputs by name, or false when one lies outside what a record can name.</summary>
    /// <param name="dependencies">The clang -MD output path.</param>
    /// <param name="output">The file the rule builds, which is ignored if it is listed.</param>
    /// <param name="source">The unit's own name, added when the rule does not list it; null for none.</param>
    /// <param name="names">Sorted distinct input names.</param>
    private bool TryNames(string dependencies, string output, string? source, out List<string> names)
    {
        names = new List<string>();
        if (!File.Exists(dependencies)) return false;
        var set = new SortedSet<string>(StringComparer.Ordinal);
        if (source != null) set.Add(source);
        string outputFull = Path.GetFullPath(output);
        foreach (string path in ParseDependencies(File.ReadAllText(dependencies)))
        {
            string full = Path.GetFullPath(path);
            // Clang lists the precompiled header it reads; StorePch records what that was built from.
            if (full == outputFull || full.EndsWith(".pch", StringComparison.Ordinal)) continue;
            string? name = NameOf(full);
            if (name == null || CurrentHash(name) == null) return false;
            set.Add(name);
        }
        names = set.ToList();
        return true;
    }

    /// <summary>Appends sorted "hash name" lines and the same-name check over their bundle files.</summary>
    private void AppendRecord(StringBuilder text, IReadOnlyCollection<string> names)
    {
        foreach (string name in names) text.Append(CurrentHash(name)).Append(' ').Append(name).Append('\n');
        text.Append("names ").Append(SameNames(names.Where(name => name.StartsWith(BundlePrefix, StringComparison.Ordinal))
            .Select(name => name[BundlePrefix.Length..]), _byFileName, _includes)).Append('\n');
    }

    /// <summary>Publishes a small file in one step, so a killed build leaves the old one or none.</summary>
    private static void WriteAtomic(string path, string text)
    {
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, text);
        File.Move(temporary, path, true);
    }

    /// <summary>Hex SHA-256 of a file.</summary>
    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(Refii.Core.Engine_Hash.Sha256(stream)).ToLowerInvariant();
    }
}

/// <summary>What a built module needs from the APK's own native libraries, read from ELF dynamic symbol tables.</summary>
/// <remarks>A module links them dynamically and their headers are already among its compile inputs, so
/// a rebuilt library need not rebuild the module unless it stopped exporting something the module binds
/// (loaded RTLD_NOW, a missing symbol would stop the game at load). This reads only the section headers,
/// .dynsym and its strings: a 120 MB library costs a few megabytes. Anything unreadable is null, and
/// the caller builds again.</remarks>
internal static class AndroidModuleImports
{
    /// <summary>One library's dynamic symbols.</summary>
    /// <param name="Defined">Names it exports.</param>
    /// <param name="Undefined">Names it needs from elsewhere.</param>
    internal sealed record Symbols(HashSet<string> Defined, List<string> Undefined);

    /// <summary>Reads an ELF64 little-endian file's dynamic symbols.</summary>
    /// <param name="path">The library or module.</param>
    /// <returns>Its symbols, or null when it has no readable .dynsym.</returns>
    public static Symbols? Read(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            byte[] head = new byte[64];
            if (file.Read(head, 0, 64) != 64 || head[0] != 0x7f || head[1] != 'E' || head[2] != 'L' || head[3] != 'F' || head[4] != 2 || head[5] != 1) return null;
            long sections = BitConverter.ToInt64(head, 40);
            int entrySize = BitConverter.ToUInt16(head, 58), count = BitConverter.ToUInt16(head, 60);
            if (sections <= 0 || entrySize < 64 || count == 0 || sections + (long)entrySize * count > file.Length) return null;
            byte[] table = ReadAt(file, sections, entrySize * count);
            for (int i = 0; i < count; i++)
            {
                int at = i * entrySize;
                if (BitConverter.ToUInt32(table, at + 4) != 11) continue; // SHT_DYNSYM
                long symbolsAt = BitConverter.ToInt64(table, at + 24), symbolsSize = BitConverter.ToInt64(table, at + 32);
                int link = BitConverter.ToInt32(table, at + 40);
                if (link <= 0 || link >= count) return null;
                long namesAt = BitConverter.ToInt64(table, link * entrySize + 24), namesSize = BitConverter.ToInt64(table, link * entrySize + 32);
                if (symbolsSize % 24 != 0 || symbolsAt < 0 || namesAt < 0 || symbolsAt + symbolsSize > file.Length || namesAt + namesSize > file.Length) return null;
                byte[] symbols = ReadAt(file, symbolsAt, checked((int)symbolsSize));
                byte[] names = ReadAt(file, namesAt, checked((int)namesSize));
                var result = new Symbols(new HashSet<string>(StringComparer.Ordinal), new List<string>());
                for (int entry = 24; entry + 24 <= symbols.Length; entry += 24)
                {
                    uint nameAt = BitConverter.ToUInt32(symbols, entry);
                    if (nameAt == 0 || nameAt >= names.Length) continue;
                    int end = Array.IndexOf(names, (byte)0, (int)nameAt);
                    if (end < 0) return null;
                    string name = Encoding.UTF8.GetString(names, (int)nameAt, end - (int)nameAt);
                    if (BitConverter.ToUInt16(symbols, entry + 6) == 0) result.Undefined.Add(name);
                    else if ((symbols[entry + 4] >> 4) != 0) result.Defined.Add(name); // not STB_LOCAL
                }
                return result;
            }
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or OverflowException or ArgumentException) { return null; }
    }

    /// <summary>The names a module binds in these libraries: what it needs that they export today.</summary>
    /// <param name="module">The linked module.</param>
    /// <param name="libraries">The APK's native libraries it can bind.</param>
    /// <returns>The names, or null when a file could not be read.</returns>
    public static List<string>? Bound(string module, IEnumerable<string> libraries)
    {
        var symbols = Read(module);
        var exported = Exports(libraries);
        return symbols == null || exported == null ? null
            : symbols.Undefined.Where(exported.Contains).Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal).ToList();
    }

    /// <summary>Everything these libraries export, or null when one could not be read.</summary>
    /// <param name="libraries">Paths of ELF libraries.</param>
    public static HashSet<string>? Exports(IEnumerable<string> libraries)
    {
        var all = new HashSet<string>(StringComparer.Ordinal);
        foreach (string library in libraries)
        {
            if (Read(library) is not { } symbols) return null;
            all.UnionWith(symbols.Defined);
        }
        return all;
    }

    /// <summary>Reads an exact byte range.</summary>
    private static byte[] ReadAt(FileStream file, long offset, int length)
    {
        file.Position = offset;
        byte[] bytes = new byte[length];
        file.ReadExactly(bytes);
        return bytes;
    }
}
