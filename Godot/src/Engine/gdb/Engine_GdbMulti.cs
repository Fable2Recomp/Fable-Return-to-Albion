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

                multi._index.TryAdd(hash, new GdbCursor(view, i));
            }
        }

        return multi;
    }

    public bool TryLookup(uint hash, out GdbCursor cursor)
    {
        cursor = default;
        return hash != 0 && hash != GdbHash.Null && _index.TryGetValue(hash, out cursor);
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
}
