using System;
using System.Collections.Generic;
using System.Text;

namespace Refii.Gdb;

/// <summary>
/// THE GUEST'S READ of a <c>.gdb</c> container, as a check. <see cref="GdbView"/> is lenient — it
/// walks the records sequentially and never reads the interpolation deltas, the declaration
/// indices or the name-table tail's offsets — so a container it accepts can still be one the
/// game's parser refuses. This reads the file the way the native parser does
/// (<c>src/vfs/src/gdb/gdb_database.cpp</c>, decompiled from <c>GDBFile_Parse</c> 0x82A016C0):
/// every record is reached THROUGH ITS DELTA, every schema through its record, every name through
/// its hash.
///
/// The invariants, all verified against the shipped <c>globals.gdb</c> (71 847 records, 9 993
/// aliases, 19 595 names; probe 2026-10-22):
/// <list type="bullet">
/// <item>header: magic <c>GDB\0</c>, reserved word 0, payload inside the file;</item>
/// <item>entry ids strictly ascending (the guest binary-searches them);</item>
/// <item>record i at <c>4 * (delta[i] + ((stride * i) >> 10))</c>, <c>stride = (recordsSize << 8) / count</c>,
/// delta an s16 — the formula reproduced every record offset of the shipped file;</item>
/// <item>a schema's hashes sorted (binary-searched by <c>sub_821E5970</c>; 391 shipped schemas
/// repeat a hash, so not strictly); descriptor k's low 24 bits the SORTED SLOT of declared
/// property k, a permutation of <c>0..count-1</c> (<c>sub_82A008F8</c>: hash of declared k is
/// <c>H[D[k] &amp; 0xFFFFFF]</c>; <c>sub_82A007C0</c>: its value <c>V[D[k] &amp; 0xFFFFFF]</c>),
/// its high byte the type of sorted slot k (<c>GDBEntry_FindPropWalkParents</c> 0x821E5AA0:
/// <c>*a3 = SHIBYTE(D[sortedIndex])</c>); and <c>parent</c>, when declared, declared FIRST
/// (<c>sub_82A00350</c> counts <c>n - hasParent</c> and <c>sub_82A008F8</c> skips index 0 of an
/// entry with a parent — all 6 629 shipped schemas with a parent comply);</item>
/// <item>alias pairs strictly ascending by alias, every target an entry id (the by-name lookup
/// <c>sub_822CD598</c> binary-searches them);</item>
/// <item>the name tail, when present: its first word the registry capacity the names were laid
/// out in (a power of two; <c>GDBNames_RegisterTable</c> grows the guest's registry to it), each
/// pair's hash the FNV-1 of its text, and the offsets array EXACTLY the registry's slot order
/// for the pairs inserted in pair order (<see cref="GdbNameRegistry"/>; the shipped 19 595
/// offsets are reproduced with 0 mismatches). The guest walks that array linearly, so the order
/// is the tool's, not a lookup's — it is enforced because the writer reproduces it and the
/// shipped file states it.</item>
/// </list>
/// The schema flag byte (0 or 1 in the shipped file) has no reader in the binary: every reader
/// of the schema word takes <c>&gt;&gt; 8</c> or <c>&amp; 0xFFFFFF00</c> (probed 2026-10-22, every
/// function that touches an entry); it is carried through unchanged.
/// </summary>
public static class GdbContainerCheck
{
    /// <summary>The usual first word of the name tail: a registry of <see cref="GdbNameRegistry.InitialCapacity"/> slots.</summary>
    public const uint NameTableVersion = GdbNameRegistry.InitialCapacity;

    /// <summary>
    /// Parses <paramref name="bytes"/> as the guest would, reporting the first invariant broken.
    /// On success <paramref name="parsed"/> holds every entry (properties in declaration order),
    /// alias and name the container states.
    /// </summary>
    public static bool TryVerify(byte[] bytes, out GdbPatchDatabase parsed, out string error)
    {
        parsed = new GdbPatchDatabase();
        error = string.Empty;

        if (bytes is null || bytes.Length < GdbView.HeaderSize)
        {
            error = "too small for a GDB header";
            return false;
        }

        if (bytes[0] != 'G' || bytes[1] != 'D' || bytes[2] != 'B' || bytes[3] != 0)
        {
            error = "missing GDB\\0 magic";
            return false;
        }

        long count = ReadU32(bytes, 4);
        long recordsSize = ReadU32(bytes, 8);
        long schemasSize = ReadU32(bytes, 12);
        long aliasCount = ReadU32(bytes, 16);
        uint reserved = ReadU32(bytes, 20);
        if (reserved != 0)
        {
            error = $"reserved header word is 0x{reserved:X8}, the guest refuses anything but 0";
            return false;
        }

        long payload = recordsSize + schemasSize + (2 * ((3 * count) + (count & 1) + (4 * aliasCount)));
        if (GdbView.HeaderSize + payload > bytes.Length)
        {
            error = $"payload of {payload} bytes runs past the file ({bytes.Length} bytes)";
            return false;
        }

        long records = GdbView.HeaderSize;
        long schemas = records + recordsSize;
        long ids = schemas + schemasSize;
        long deltas = ids + (4 * count);
        long aliases = GdbView.HeaderSize + payload - (8 * aliasCount);
        long stride = count != 0 ? (recordsSize << 8) / count : 0;

        uint previousId = 0;
        for (long i = 0; i < count; i++)
        {
            uint id = ReadU32(bytes, ids + (4 * i));
            if (i != 0 && id <= previousId)
            {
                error = $"entry ids are not strictly ascending at index {i} (0x{previousId:X8} then 0x{id:X8})";
                return false;
            }

            previousId = id;

            short delta = (short)ReadU16(bytes, deltas + (2 * i));
            long recordOffset = 4 * (delta + ((stride * i) >> 10));
            if (recordOffset < 0 || recordOffset + 4 > recordsSize)
            {
                error = $"entry 0x{id:X8}: delta {delta} puts the record at {recordOffset}, outside the records block";
                return false;
            }

            uint schemaOffset = ReadU32(bytes, records + recordOffset);
            if ((long)schemaOffset + 4 > schemasSize)
            {
                error = $"entry 0x{id:X8}: schema offset {schemaOffset} is outside the schemas block";
                return false;
            }

            long schema = schemas + schemaOffset;
            uint word = ReadU32(bytes, schema);
            long propertyCount = word >> 8;
            byte flags = (byte)(word & 0xFF);
            if (schemaOffset + 4 + (8 * propertyCount) > schemasSize)
            {
                error = $"entry 0x{id:X8}: schema of {propertyCount} properties overruns the schemas block";
                return false;
            }

            if (recordOffset + 4 + (4 * propertyCount) > recordsSize)
            {
                error = $"entry 0x{id:X8}: record of {propertyCount} values overruns the records block";
                return false;
            }

            var entry = new GdbPatchEntry(id, flags);
            uint previousHash = 0;
            bool hasParent = false;
            for (long sorted = 0; sorted < propertyCount; sorted++)
            {
                uint hash = ReadU32(bytes, schema + 4 + (4 * sorted));
                // Sorted, not strictly: 391 schemas of the shipped globals.gdb repeat a hash
                // (entry 0x000CAC77's has 0x3A6CF617 twice), which the guest evidently takes.
                if (sorted != 0 && hash < previousHash)
                {
                    error = $"entry 0x{id:X8}: schema hashes are not sorted (0x{previousHash:X8} then 0x{hash:X8})";
                    return false;
                }

                previousHash = hash;
                hasParent |= hash == GdbView.HashParent;
            }

            // Declared property k lives at sorted slot D[k] & 0xFFFFFF (sub_82A008F8 / sub_82A007C0);
            // the type of sorted slot s is D[s] >> 24 (GDBEntry_FindPropWalkParents).
            var slotTaken = new bool[propertyCount];
            for (long k = 0; k < propertyCount; k++)
            {
                uint descriptor = ReadU32(bytes, schema + 4 + (4 * propertyCount) + (4 * k));
                long slot = descriptor & 0xFFFFFF;
                if (slot >= propertyCount || slotTaken[slot])
                {
                    error = $"entry 0x{id:X8}: declared property {k} names sorted slot {slot}, which is out of range or repeated";
                    return false;
                }

                slotTaken[slot] = true;
                uint hash = ReadU32(bytes, schema + 4 + (4 * slot));
                uint slotDescriptor = ReadU32(bytes, schema + 4 + (4 * propertyCount) + (4 * slot));
                uint value = ReadU32(bytes, records + recordOffset + 4 + (4 * slot));
                entry.Properties.Add(new GdbPatchProperty(hash, (GdbFieldType)(byte)(slotDescriptor >> 24), value));
            }

            // The guest's enumeration (sub_82A00350, sub_82A008F8) skips declared index 0 of an
            // entry that has a parent: `parent` must be what sits there.
            if (hasParent && entry.Properties[0].NameHash != GdbView.HashParent)
            {
                error = $"entry 0x{id:X8}: has a parent but declares 0x{entry.Properties[0].NameHash:X8} first; the guest's enumeration skips declared index 0 as the parent";
                return false;
            }

            parsed.Entries.Add(entry);
        }

        var idSet = new HashSet<uint>(parsed.Entries.Count);
        foreach (GdbPatchEntry entry in parsed.Entries)
        {
            idSet.Add(entry.Id);
        }

        uint previousAlias = 0;
        for (long i = 0; i < aliasCount; i++)
        {
            uint alias = ReadU32(bytes, aliases + (8 * i));
            uint target = ReadU32(bytes, aliases + (8 * i) + 4);
            if (i != 0 && alias <= previousAlias)
            {
                error = $"aliases are not strictly ascending at index {i} (0x{previousAlias:X8} then 0x{alias:X8})";
                return false;
            }

            previousAlias = alias;
            if (!idSet.Contains(target))
            {
                error = $"alias 0x{alias:X8} names entry 0x{target:X8}, which the container does not hold";
                return false;
            }

            parsed.Aliases.Add((alias, target));
        }

        long tail = GdbView.HeaderSize + payload;
        long tailSize = bytes.Length - tail;
        if (tailSize >= 12)
        {
            uint capacity = ReadU32(bytes, tail);
            if (capacity == 0 || (capacity & (capacity - 1)) != 0)
            {
                error = $"name table capacity 0x{capacity:X8} is not a power of two; the guest's registry masks with capacity - 1";
                return false;
            }

            long pairsSize = ReadU32(bytes, tail + 4);
            long nameCount = ReadU32(bytes, tail + 8);
            if (12 + pairsSize + (4 * nameCount) > tailSize)
            {
                error = $"name table of {pairsSize} pair bytes and {nameCount} offsets runs past the file";
                return false;
            }

            long pairs = tail + 12;
            long position = 0;
            var pairStarts = new List<uint>((int)nameCount);
            var pairHashes = new List<uint>((int)nameCount);
            for (long i = 0; i < nameCount; i++)
            {
                if (position + 5 > pairsSize)
                {
                    error = $"name {i} starts past the end of the name pairs";
                    return false;
                }

                uint hash = ReadU32(bytes, pairs + position);
                long textStart = pairs + position + 4;
                long terminator = textStart;
                while (terminator < pairs + pairsSize && bytes[terminator] != 0)
                {
                    terminator++;
                }

                if (terminator >= pairs + pairsSize)
                {
                    error = $"name {i} (0x{hash:X8}) is not NUL-terminated inside the name pairs";
                    return false;
                }

                string text = Encoding.UTF8.GetString(bytes, (int)textStart, (int)(terminator - textStart));
                uint expected = Refii.Core.Engine_GameHash.Fnv1(text);
                if (expected != hash)
                {
                    error = $"name '{text}' is filed under 0x{hash:X8} but hashes to 0x{expected:X8}; the guest refuses the table";
                    return false;
                }

                if (parsed.Names.ContainsKey(hash))
                {
                    error = $"name 0x{hash:X8} ('{text}') is filed twice";
                    return false;
                }

                pairStarts.Add((uint)position);
                pairHashes.Add(hash);
                parsed.Names[hash] = text;
                position = terminator + 1 - pairs;
            }

            // THE OFFSETS ARRAY is the registry the names were inserted into, dumped slot by slot
            // (GDBNames_RegisterTable reads the capacity, then walks the offsets linearly). The
            // capacity and the order are what GdbNameRegistry gives for the pairs in pair order.
            (uint expectedCapacity, int[] slotOrder) = GdbNameRegistry.Layout(pairHashes);
            if (capacity != expectedCapacity)
            {
                error = $"name table capacity 0x{capacity:X8} stated, but {nameCount} names lay out in a registry of 0x{expectedCapacity:X8}";
                return false;
            }

            for (long i = 0; i < nameCount; i++)
            {
                uint offset = ReadU32(bytes, pairs + pairsSize + (4 * i));
                uint expected = pairStarts[slotOrder[i]];
                if (offset != expected)
                {
                    error = $"name offset {i} is {offset}; the registry's slot order puts pair {slotOrder[i]} ({expected}) there";
                    return false;
                }
            }
        }
        else if (tailSize != 0)
        {
            error = $"{tailSize} byte(s) after the payload that are not a name table";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Whether <paramref name="written"/> states exactly what <paramref name="intended"/> holds:
    /// the same entries with the same flags, properties (hash, type, value — declaration order
    /// included), aliases and names. The writer's round-trip test.
    /// </summary>
    public static bool SameContent(GdbPatchDatabase intended, GdbPatchDatabase written, out string difference)
    {
        difference = string.Empty;

        if (intended.Entries.Count != written.Entries.Count)
        {
            difference = $"{intended.Entries.Count} entries intended, {written.Entries.Count} written";
            return false;
        }

        var byId = new Dictionary<uint, GdbPatchEntry>(written.Entries.Count);
        foreach (GdbPatchEntry entry in written.Entries)
        {
            byId[entry.Id] = entry;
        }

        foreach (GdbPatchEntry entry in intended.Entries)
        {
            if (!byId.TryGetValue(entry.Id, out GdbPatchEntry? other))
            {
                difference = $"entry 0x{entry.Id:X8} is missing";
                return false;
            }

            if (other.SchemaFlags != entry.SchemaFlags)
            {
                difference = $"entry 0x{entry.Id:X8}: flags {entry.SchemaFlags} intended, {other.SchemaFlags} written";
                return false;
            }

            if (other.Properties.Count != entry.Properties.Count)
            {
                difference = $"entry 0x{entry.Id:X8}: {entry.Properties.Count} properties intended, {other.Properties.Count} written";
                return false;
            }

            for (int i = 0; i < entry.Properties.Count; i++)
            {
                GdbPatchProperty a = entry.Properties[i];
                GdbPatchProperty b = other.Properties[i];
                if (a.NameHash != b.NameHash || a.Type != b.Type || a.Value != b.Value)
                {
                    difference = $"entry 0x{entry.Id:X8}, property {i}: " +
                        $"0x{a.NameHash:X8}/{a.Type}/0x{a.Value:X8} intended, 0x{b.NameHash:X8}/{b.Type}/0x{b.Value:X8} written";
                    return false;
                }
            }
        }

        var aliases = new HashSet<(uint, uint)>(written.Aliases);
        if (aliases.Count != new HashSet<(uint, uint)>(intended.Aliases).Count)
        {
            difference = $"{intended.Aliases.Count} aliases intended, {written.Aliases.Count} written";
            return false;
        }

        foreach ((uint alias, uint target) in intended.Aliases)
        {
            if (!aliases.Contains((alias, target)))
            {
                difference = $"alias 0x{alias:X8} -> 0x{target:X8} is missing";
                return false;
            }
        }

        if (intended.Names.Count != written.Names.Count)
        {
            difference = $"{intended.Names.Count} names intended, {written.Names.Count} written";
            return false;
        }

        foreach ((uint hash, string name) in intended.Names)
        {
            if (!written.Names.TryGetValue(hash, out string? text) || !string.Equals(text, name, StringComparison.Ordinal))
            {
                difference = $"name 0x{hash:X8} ('{name}') is missing or differs";
                return false;
            }
        }

        return true;
    }

    private static uint ReadU32(byte[] data, long at) =>
        ((uint)data[at] << 24) | ((uint)data[at + 1] << 16) | ((uint)data[at + 2] << 8) | data[at + 3];

    private static ushort ReadU16(byte[] data, long at) =>
        (ushort)((data[at] << 8) | data[at + 1]);
}
