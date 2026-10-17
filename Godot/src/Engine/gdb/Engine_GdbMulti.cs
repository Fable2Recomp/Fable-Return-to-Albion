using System;
using System.Collections.Generic;

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
    private readonly List<GdbView> _views = new();

    private readonly Dictionary<uint, GdbCursor> _index = new();
    private readonly Dictionary<uint, List<GdbCursor>> _cursorsByHash = new();
    private readonly Dictionary<uint, uint> _aliasesByTarget = new();

    public IReadOnlyList<GdbView> Views => _views;

    public int RecordCount => _index.Count;

    public static GdbMulti Build(IEnumerable<byte[]?> gdbs)
    {
        var multi = new GdbMulti();

        foreach (byte[]? bytes in gdbs)
        {
            if (bytes is not null && bytes.Length != 0 &&
                GdbView.TryParse(bytes, out GdbView view, out _))
            {
                multi._views.Add(view);
            }
        }

        foreach (GdbView view in multi._views)
        {
            for (int i = 0; i < view.RecordCount; i++)
            {
                uint hash = view.RecordHash(i);
                if (hash == 0 || hash == GdbHash.Null)
                {
                    continue;
                }

                var cursor = new GdbCursor(view, i);
                multi._index[hash] = cursor;

                if (!multi._cursorsByHash.TryGetValue(hash, out var list))
                {
                    list = new List<GdbCursor>();
                    multi._cursorsByHash[hash] = list;
                }
                list.Add(cursor);
            }
        }

        // THE NAME ALIASES, second so a real record hash always wins. A record's hash is NOT the
        // hash of its name — the GDB carries a pair table mapping FNV-1(name) to the entry hash,
        // and `TryLookup(Fnv1("AbilityPhysiqueBase"))` finds nothing without it: every by-name
        // lookup in the game (abilities, item records, level records) rides this table.
        foreach (GdbView view in multi._views)
        {
            foreach ((uint alias, uint target) in view.AliasPairs())
            {
                if (alias != 0 && alias != GdbHash.Null &&
                    multi._index.TryGetValue(target, out GdbCursor cursor))
                {
                    multi._index.TryAdd(alias, cursor);
                    multi._aliasesByTarget.TryAdd(target, alias);
                }
            }
        }

        return multi;
    }

    /// <summary>
    /// Builds a new <see cref="GdbMulti"/> by reusing the already-parsed <see cref="GdbView"/> instances
    /// from <paramref name="baseMulti"/> and layering one or more <c>.gdb.tu</c> overlay blobs on top.
    /// Avoids re-parsing the large base GDB files on disk.
    /// </summary>
    public static GdbMulti BuildWithOverlays(GdbMulti baseMulti, IEnumerable<byte[]?> overlayGdbs)
    {
        var multi = new GdbMulti();
        if (baseMulti is not null)
        {
            multi._views.AddRange(baseMulti._views);
        }

        if (overlayGdbs is not null)
        {
            foreach (byte[]? bytes in overlayGdbs)
            {
                if (bytes is not null && bytes.Length != 0 &&
                    GdbView.TryParse(bytes, out GdbView view, out _))
                {
                    multi._views.Add(view);
                }
            }
        }

        foreach (GdbView view in multi._views)
        {
            for (int i = 0; i < view.RecordCount; i++)
            {
                uint hash = view.RecordHash(i);
                if (hash == 0 || hash == GdbHash.Null)
                {
                    continue;
                }

                var cursor = new GdbCursor(view, i);
                multi._index[hash] = cursor;

                if (!multi._cursorsByHash.TryGetValue(hash, out var list))
                {
                    list = new List<GdbCursor>();
                    multi._cursorsByHash[hash] = list;
                }
                list.Add(cursor);
            }
        }

        foreach (GdbView view in multi._views)
        {
            foreach ((uint alias, uint target) in view.AliasPairs())
            {
                if (alias != 0 && alias != GdbHash.Null &&
                    multi._index.TryGetValue(target, out GdbCursor cursor))
                {
                    multi._index.TryAdd(alias, cursor);
                    multi._aliasesByTarget.TryAdd(target, alias);
                }
            }
        }

        return multi;
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

        var visited = new HashSet<uint>();
        uint currentHash = start.View.RecordHash(start.Record);
        if (currentHash == 0 && start.View.TryFindLocalRaw(start.Record, GdbView.HashParent, 6, out uint startParent, out _))
        {
            currentHash = startParent;
        }

        while (currentHash != 0 && currentHash != GdbHash.Null && visited.Add(currentHash))
        {
            // Gather all cursors for this record hash (highest priority / TU patch first, base GDB last):
            List<GdbCursor>? layers = null;
            if (_cursorsByHash.TryGetValue(currentHash, out var list) && list.Count > 0)
            {
                layers = list;
            }

            uint nextParent = 0;

            if (layers is not null)
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
        if (startHash != 0 && _cursorsByHash.TryGetValue(startHash, out var cursors) && cursors.Count > 1)
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
            foreach ((uint hash, GdbFieldType type, uint raw) in at.View.RawFields(at.Record))
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
