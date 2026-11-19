using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Refii.Gdb;

public readonly struct GdbCursor
{
    public GdbCursor(GdbView view, int record)
    {
        View = view;
        Record = record;
    }

    public GdbView View { get; }

    public int Record { get; }

    public bool Valid => View is not null;
}

public sealed class GdbMulti
{
    private readonly Tables _tables;

    private readonly List<GdbView> _views;

    private readonly Dictionary<uint, GdbCursor> _index;

    /// <summary>
    /// Every layer of a record that more than one view states, base first. A record with one layer
    /// is not here: <see cref="_index"/> already says all there is to say about it, and a list per
    /// record was two thirds of the index's bytes.
    /// </summary>
    private readonly Dictionary<uint, List<GdbCursor>> _layers;
    private readonly Dictionary<uint, uint> _aliasesByTarget;

    public IReadOnlyList<GdbView> Views => _views;

    /// <summary>Every database built, weakly, so the heap census can count the copies still alive.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<GdbMulti, object?> Live = new();

    /// <summary>
    /// The views and the indexes over them. Nothing changes them once built, so databases over the
    /// same views in the same order hold one set: the item, clothing, weapon, entity and game
    /// databases are all the same files, and a set each was 30 MB each. Weakly held, so the set
    /// lives exactly as long as a database that uses it. Each build still gets its own
    /// <see cref="GdbMulti"/>, for the callers that tell one database from another by reference.
    /// </summary>
    private static readonly List<WeakReference<Tables>> SharedTables = new();

    private GdbMulti(Tables tables)
    {
        _tables = tables;
        _views = tables.Views;
        _index = tables.Index;
        _layers = tables.Layers;
        _aliasesByTarget = tables.AliasesByTarget;
        Live.AddOrUpdate(this, null);
    }

    /// <summary>
    /// The databases alive (or not yet collected) and an estimate of their bytes: each view's blob
    /// and offsets and each set of tables, counted once however many databases share them, with
    /// about 40 bytes per index entry and 120 per record stated by more than one view.
    /// </summary>
    internal static (long Databases, long Bytes) LiveCensus()
    {
        long databases = 0, bytes = 0;
        var views = new HashSet<GdbView>(ReferenceEqualityComparer.Instance);
        var tables = new HashSet<Tables>(ReferenceEqualityComparer.Instance);
        foreach (KeyValuePair<GdbMulti, object?> entry in Live)
        {
            GdbMulti multi = entry.Key;
            ++databases;
            if (tables.Add(multi._tables))
            {
                bytes += (long)multi._index.Count * 40 + (long)multi._layers.Count * 120;
            }

            foreach (GdbView view in multi._views)
            {
                if (views.Add(view))
                {
                    bytes += view.HeldBytes;
                }
            }
        }

        return (databases, bytes);
    }

    public int RecordCount => _index.Count;

    public static GdbMulti Build(IEnumerable<byte[]?> gdbs)
    {
        var views = new List<GdbView>();

        foreach (byte[]? bytes in gdbs)
        {
            AddView(views, bytes);
        }

        return new GdbMulti(Tables.For(views));
    }

    /// <summary>Builds a multi-view database directly from already-parsed <see cref="GdbView"/> instances.</summary>
    public static GdbMulti Build(IReadOnlyList<GdbView> views) =>
        new(Tables.For(new List<GdbView>(views)));

    private static void AddView(List<GdbView> views, byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0 || !GdbView.TryParseShared(bytes, out GdbView view, out _))
        {
            return;
        }

        // One file listed twice is two layers, and layers are told apart by their view.
        if (!views.Contains(view) || GdbView.TryParse(bytes, out view, out _))
        {
            views.Add(view);
        }
    }

    private sealed class Tables
    {
        public readonly List<GdbView> Views;
        public readonly Dictionary<uint, GdbCursor> Index = new();
        public readonly Dictionary<uint, List<GdbCursor>> Layers = new();
        public readonly Dictionary<uint, uint> AliasesByTarget = new();

        private Tables(List<GdbView> views)
        {
            Views = views;
            BuildIndex();
        }

        public static Tables For(List<GdbView> views)
        {
            lock (SharedTables)
            {
                for (int i = SharedTables.Count - 1; i >= 0; i--)
                {
                    if (!SharedTables[i].TryGetTarget(out Tables? held))
                    {
                        SharedTables.RemoveAt(i);
                    }
                    else if (SameViews(held.Views, views))
                    {
                        return held;
                    }
                }
            }

            var built = new Tables(views);
            lock (SharedTables)
            {
                SharedTables.Add(new WeakReference<Tables>(built));
            }

            return built;
        }

        private static bool SameViews(List<GdbView> a, List<GdbView> b)
        {
            if (a.Count != b.Count)
            {
                return false;
            }

            for (int i = 0; i < a.Count; i++)
            {
                if (!ReferenceEquals(a[i], b[i]))
                {
                    return false;
                }
            }

            return true;
        }

        private void BuildIndex()
        {
            int records = 0, aliases = 0;
            foreach (GdbView view in Views)
            {
                records += view.RecordCount;
                aliases += view.AliasCount;
            }

            Index.EnsureCapacity(records + aliases);
            AliasesByTarget.EnsureCapacity(aliases);

            foreach (GdbView view in Views)
            {
                for (int i = 0; i < view.RecordCount; i++)
                {
                    uint hash = view.RecordHash(i);
                    if (hash == 0 || hash == GdbHash.Null)
                    {
                        continue;
                    }

                    var cursor = new GdbCursor(view, i);
                    ref GdbCursor slot = ref CollectionsMarshal.GetValueRefOrAddDefault(Index, hash, out bool stated);
                    if (stated)
                    {
                        if (!Layers.TryGetValue(hash, out List<GdbCursor>? list))
                        {
                            list = new List<GdbCursor>(2) { slot };
                            Layers[hash] = list;
                        }

                        list.Add(cursor);
                    }

                    slot = cursor;
                }
            }

            // THE NAME ALIASES, second so a real record hash always wins. A record's hash is NOT the
            // hash of its name — the GDB carries a pair table mapping FNV-1(name) to the entry hash,
            // and `TryLookup(Fnv1("AbilityPhysiqueBase"))` finds nothing without it: every by-name
            // lookup in the game (abilities, item records, level records) rides this table.
            foreach (GdbView view in Views)
            {
                for (int i = 0; i < view.AliasCount; i++)
                {
                    view.AliasPair(i, out uint alias, out uint target);
                    if (alias != 0 && alias != GdbHash.Null &&
                        Index.TryGetValue(target, out GdbCursor cursor))
                    {
                        Index.TryAdd(alias, cursor);
                        AliasesByTarget.TryAdd(target, alias);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Builds a new <see cref="GdbMulti"/> by reusing the already-parsed <see cref="GdbView"/> instances
    /// from <paramref name="baseMulti"/> and layering one or more <c>.gdb.tu</c> overlay blobs on top.
    /// Avoids re-parsing the large base GDB files on disk.
    /// </summary>
    public static GdbMulti BuildWithOverlays(GdbMulti baseMulti, IEnumerable<byte[]?> overlayGdbs)
    {
        var views = new List<GdbView>();
        if (baseMulti is not null)
        {
            views.AddRange(baseMulti._views);
        }

        if (overlayGdbs is not null)
        {
            foreach (byte[]? bytes in overlayGdbs)
            {
                AddView(views, bytes);
            }
        }

        return new GdbMulti(Tables.For(views));
    }

    public bool TryLookup(uint hash, out GdbCursor cursor)
    {
        cursor = default;
        return hash != 0 && hash != GdbHash.Null && _index.TryGetValue(hash, out cursor);
    }

    /// <summary>
    /// Returns the index of the <see cref="GdbView"/> that owns the record with the given hash,
    /// or −1 if the record is not found. The index corresponds to the position in
    /// <see cref="Refii.Level.Engine_LevelProps.SupplementalGdbPaths"/> used to build this multi.
    /// </summary>
    public int ViewIndexOf(uint hash)
    {
        if (hash == 0 || hash == GdbHash.Null || !_index.TryGetValue(hash, out GdbCursor cursor))
        {
            return -1;
        }

        for (int i = 0; i < _views.Count; i++)
        {
            if (ReferenceEquals(_views[i], cursor.View))
            {
                return i;
            }
        }

        return -1;
    }

    public bool TryFindInherited(
        GdbCursor start,
        uint fieldHash,
        int expectedType,
        out GdbCursor owner,
        out uint value,
        out GdbFieldType type)
    {
        owner = default;
        value = 0;
        type = default;

        if (!start.Valid)
        {
            return false;
        }

        // If the caller passed a cursor for a record, first check that specific cursor:
        if (start.View.TryFindLocalRaw(start.Record, fieldHash, expectedType, out value, out type))
        {
            owner = start;
            return true;
        }

        // The chains walked here are short, so the loop guard needs no set until one is not.
        Span<uint> visited = stackalloc uint[32];
        int visitedCount = 0;
        HashSet<uint>? visitedMore = null;
        uint currentHash = start.View.RecordHash(start.Record);
        if (currentHash == 0 && start.View.TryFindLocalRaw(start.Record, GdbView.HashParent, 6, out uint startParent, out _))
        {
            currentHash = startParent;
        }

        while (currentHash != 0 && currentHash != GdbHash.Null &&
            FirstVisit(visited, ref visitedCount, ref visitedMore, currentHash))
        {
            uint nextParent = 0;

            // Every layer of this record (highest priority / TU patch first, base GDB last):
            if (_layers.TryGetValue(currentHash, out List<GdbCursor>? layers))
            {
                for (int i = layers.Count - 1; i >= 0; i--)
                {
                    GdbCursor c = layers[i];
                    if (c.View.TryFindLocalRaw(c.Record, fieldHash, expectedType, out value, out type))
                    {
                        owner = c;
                        return true;
                    }

                    if (nextParent == 0)
                    {
                        if (c.View.TryFindLocalRaw(c.Record, GdbView.HashParent, 6, out uint p, out _) &&
                            p != 0 && p != GdbHash.Null)
                        {
                            nextParent = p;
                        }
                    }
                }
            }
            else if (TryLookup(currentHash, out GdbCursor fallback))
            {
                if (fallback.View.TryFindLocalRaw(fallback.Record, fieldHash, expectedType, out value, out type))
                {
                    owner = fallback;
                    return true;
                }

                if (fallback.View.TryFindLocalRaw(fallback.Record, GdbView.HashParent, 6, out uint p, out _) &&
                    p != 0 && p != GdbHash.Null)
                {
                    nextParent = p;
                }
            }

            currentHash = nextParent;
        }

        return false;
    }

    private static bool FirstVisit(Span<uint> visited, ref int count, ref HashSet<uint>? more, uint hash)
    {
        foreach (uint seen in visited[..count])
        {
            if (seen == hash)
            {
                return false;
            }
        }

        if (more is not null)
        {
            return more.Add(hash);
        }

        if (count < visited.Length)
        {
            visited[count++] = hash;
            return true;
        }

        more = new HashSet<uint> { hash };
        return true;
    }

    public string NameOf(uint hash)
    {
        foreach (GdbView view in _views)
        {
            if (view.RecordNames.TryGetValue(hash, out string? authored) &&
                authored.Length != 0)
            {
                return authored;
            }
        }

        foreach (GdbView view in _views)
        {
            if (view.Names.TryGetValue(hash, out string? text) && text.Length != 0)
            {
                return text;
            }
        }

        if (_aliasesByTarget.TryGetValue(hash, out uint alias))
        {
            foreach (GdbView view in _views)
            {
                if (view.Names.TryGetValue(alias, out string? aliasText) && aliasText.Length != 0)
                {
                    return aliasText;
                }
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Every property visible on a record, inherited included (`sub_82A00990`): ancestors first, a
    /// redefined name yielding the most derived value at its first position (`sub_82A00738` at
    /// 0x82A00B90); the `parent` link is excluded.
    /// </summary>
    public IEnumerable<(uint Hash, GdbFieldType Type, uint Raw)> InheritedFields(GdbCursor start)
    {
        if (!start.Valid)
        {
            yield break;
        }

        // Root-most first: the guest fills its array back-to-front (0x82A00A98), then iterates forward.
        var chain = new List<GdbCursor>();
        var seen = new HashSet<uint>();
        GdbCursor current = start;
        for (int depth = 0; depth < 64 && current.Valid; depth++)
        {
            chain.Add(current);
            if (!current.View.TryFindLocalRaw(
                    current.Record, GdbView.HashParent, 6, out uint parent, out _) ||
                parent == 0 || parent == GdbHash.Null ||
                !seen.Add(parent) ||
                !TryLookup(parent, out current))
            {
                break;
            }
        }

        // Also check if start had underlying cursors (e.g. from base databases):
        uint startHash = start.View.RecordHash(start.Record);
        if (startHash != 0 && _layers.TryGetValue(startHash, out var cursors))
        {
            for (int c = cursors.Count - 1; c >= 0; c--)
            {
                GdbCursor underlying = cursors[c];
                if (ReferenceEquals(underlying.View, start.View))
                {
                    continue;
                }

                GdbCursor underCurrent = underlying;
                for (int depth = 0; depth < 64 && underCurrent.Valid; depth++)
                {
                    chain.Add(underCurrent);
                    if (!underCurrent.View.TryFindLocalRaw(
                            underCurrent.Record, GdbView.HashParent, 6, out uint parent, out _) ||
                        parent == 0 || parent == GdbHash.Null ||
                        !seen.Add(parent) ||
                        !TryLookup(parent, out underCurrent))
                    {
                        break;
                    }
                }
            }
        }

        chain.Reverse();

        var emitted = new HashSet<uint>();
        foreach (GdbCursor at in chain)
        {
            foreach ((uint hash, GdbFieldType type, uint raw) in at.View.DeclaredFields(at.Record))
            {
                if (hash == GdbView.HashParent || !emitted.Add(hash))
                {
                    continue;
                }

                // The deepest definition wins.
                (GdbFieldType Type, uint Raw) value = (type, raw);
                for (int i = chain.Count - 1; i >= 0; i--)
                {
                    if (chain[i].View.TryFindLocalRaw(
                            chain[i].Record, hash, -1, out uint over, out GdbFieldType overType))
                    {
                        value = (overType, over);
                        break;
                    }
                }

                yield return (hash, value.Type, value.Raw);
            }
        }
    }
}
