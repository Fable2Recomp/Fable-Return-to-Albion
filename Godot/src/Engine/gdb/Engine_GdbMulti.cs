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

                multi._index[hash] = new GdbCursor(view, i);
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

        GdbCursor current = start;
        var visited = new HashSet<uint>();

        for (int depth = 0; depth < 64 && current.Valid; depth++)
        {
            if (current.View.TryFindLocalRaw(
                    current.Record, fieldHash, expectedType, out value, out type))
            {
                owner = current;
                return true;
            }

            if (!current.View.TryFindLocalRaw(
                    current.Record, GdbView.HashParent, 6, out uint parent, out _) ||
                parent == 0 || parent == GdbHash.Null ||
                !visited.Add(parent) ||
                !TryLookup(parent, out current))
            {
                return false;
            }
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
                for (int i = chain.Count - 1; i > 0; i--)
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
