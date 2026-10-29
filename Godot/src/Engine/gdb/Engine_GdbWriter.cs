using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Refii.Gdb;

public sealed class GdbPatchProperty
{
    public uint NameHash { get; set; }
    public GdbFieldType Type { get; set; }
    public uint Value { get; set; }

    public GdbPatchProperty() { }

    public GdbPatchProperty(uint nameHash, GdbFieldType type, uint value)
    {
        NameHash = nameHash;
        Type = type;
        Value = value;
    }
}

public sealed class GdbPatchEntry
{
    public uint Id { get; set; }
    public byte SchemaFlags { get; set; }
    public List<GdbPatchProperty> Properties { get; } = new();

    public GdbPatchEntry() { }

    public GdbPatchEntry(uint id, byte schemaFlags = 0)
    {
        Id = id;
        SchemaFlags = schemaFlags;
    }
}

public sealed class GdbPatchDatabase
{
    public List<GdbPatchEntry> Entries { get; } = new();
    public List<(uint AliasHash, uint TargetEntryId)> Aliases { get; } = new();
    public Dictionary<uint, string> Names { get; } = new();

    /// <summary>
    /// Files <paramref name="text"/> as the name of <paramref name="hash"/> THE WAY THE CONTAINER
    /// CAN HOLD IT. The name table maps FNV-1(text) to text and nothing else — the guest recomputes
    /// the hash and refuses a mismatch — so a record whose authored name is not its id (most of
    /// them: "AbilityHeavyWeaponsBase" is alias 0x064A4B7E for entry 0x19F1B0CD) is named by an
    /// ALIAS PAIR, FNV-1(name) to id, plus the name under its own hash. <c>Names[id] = name</c> is
    /// the AUTHORING form the patch builder and the Studio write and read back; the writer applies
    /// this to every name as it serialises, so the container never carries a misfiled one.
    /// </summary>
    /// <param name="entryIds">The ids this database holds, so an alias is only added for one of them.</param>
    public void FileName(uint hash, string text, ISet<uint>? entryIds = null)
    {
        // The empty string is a name too: the shipped table files it under the FNV-1 basis, and a
        // string-typed property pointing at it reads as "".
        if (text is null)
        {
            return;
        }

        uint own = Refii.Core.Engine_GameHash.Fnv1(text);
        Names[own] = text;
        if (own == hash)
        {
            return;
        }

        bool isEntry = entryIds?.Contains(hash) ?? Entries.Exists(e => e.Id == hash);
        if (isEntry && !Aliases.Contains((own, hash)))
        {
            Aliases.Add((own, hash));
        }
    }
}

/// <summary>
/// Managed binary GDB writer matching Fable 2's native 'GDB\0' database format
/// (decompiled from default.xex GDBFile_Parse 0x82A016C0 and mirrored by src/formats/src/gdb/gdb_database.cpp).
///
/// THE INVERSE OF THE PARSER, PROVED ON EVERY WRITE: the bytes are read back through
/// <see cref="GdbContainerCheck"/> — the guest's read, deltas and all — and compared with what
/// was asked for. A container the guest would refuse never leaves this method; it throws instead.
/// The guest parses the merged file headlessly at boot, so a malformed container is a crash, not
/// a log line.
///
/// Two layouts the guest dictates (IDA, TU1 and disc, 2026-10-22):
/// <list type="bullet">
/// <item>A schema's descriptor k is <c>(type of SORTED slot k) &lt;&lt; 24 | sorted slot of DECLARED
/// property k</c>. The guest enumerates declared property k as hash <c>H[D[k] &amp; 0xFFFFFF]</c>
/// (<c>sub_82A008F8</c>) and value <c>V[D[k] &amp; 0xFFFFFF]</c> (<c>sub_82A007C0</c>); its lookup
/// reads the type of sorted slot s as <c>D[s] &gt;&gt; 24</c> (<c>GDBEntry_FindPropWalkParents</c>
/// 0x821E5AA0). Declared property 0 must be <c>parent</c> when the entry has one: the enumeration
/// skips index 0 of an entry with a parent (<c>sub_82A00350</c>, <c>sub_82A008F8</c>: <c>++a2</c>),
/// so the writer files <c>parent</c> first.</item>
/// <item>The name-table tail is the tool's name registry dumped slot by slot
/// (<see cref="GdbNameRegistry"/>): its first word is the registry's capacity, its offsets array
/// the slot order. The guest re-registers it linearly, so the order is not a lookup — it is what
/// the shipped file states, reproduced exactly.</item>
/// </list>
/// </summary>
public static class GdbWriter
{
    public const int HeaderSize = 0x18;

    /// <summary>
    /// The first word of the name-table tail: the registry capacity the names were laid out in
    /// (<c>GDBNames_RegisterTable</c> grows the guest's registry to it). 0x10000 until the registry
    /// has to grow; <see cref="GdbNameRegistry.Layout"/> states the capacity actually used.
    /// </summary>
    public const uint NameTableVersion = GdbNameRegistry.InitialCapacity;

    public static byte[] Serialize(GdbPatchDatabase database)
    {
        if (database is null)
        {
            throw new ArgumentNullException(nameof(database));
        }

        // 1. The entries as the container holds them: id order, ids unique, and `parent` declared
        // first wherever an entry has one (the guest's enumeration skips declared index 0 of an
        // entry with a parent — sub_82A00350 / sub_82A008F8 — so nothing else may sit there).
        var canonical = new GdbPatchDatabase();
        foreach (GdbPatchEntry entry in database.Entries)
        {
            canonical.Entries.Add(ParentFirst(entry));
        }

        canonical.Aliases.AddRange(database.Aliases);

        var order = new List<int>(canonical.Entries.Count);
        for (int i = 0; i < canonical.Entries.Count; i++)
        {
            order.Add(i);
        }

        order.Sort((a, b) => canonical.Entries[a].Id.CompareTo(canonical.Entries[b].Id));

        for (int i = 1; i < order.Count; i++)
        {
            if (canonical.Entries[order[i - 1]].Id == canonical.Entries[order[i]].Id)
            {
                throw new InvalidOperationException(
                    $"Duplicate GDB entry id 0x{canonical.Entries[order[i]].Id:X8}");
            }
        }

        var records = new List<byte>();
        var schemas = new List<byte>();
        var recordOffsets = new int[order.Count];
        var schemaOffsets = new Dictionary<string, uint>();

        // 2. Build schemas and records:
        for (int i = 0; i < order.Count; i++)
        {
            GdbPatchEntry entry = canonical.Entries[order[i]];
            if (entry.Properties.Count >= (1 << 24))
            {
                throw new InvalidOperationException(
                    $"Entry 0x{entry.Id:X8} has too many properties ({entry.Properties.Count})");
            }

            // Sorted slot s holds declared property sortedIndices[s]; slotOf[k] is the sorted
            // slot of declared property k. A repeated hash stays in declaration order (the
            // comparison falls back to the index — List.Sort is not stable): 391 schemas of the
            // shipped globals.gdb have one, and every shipped schema re-emits byte for byte
            // under this rule (probe 2026-10-22).
            var sortedIndices = new List<int>(entry.Properties.Count);
            for (int p = 0; p < entry.Properties.Count; p++)
            {
                sortedIndices.Add(p);
            }

            sortedIndices.Sort((a, b) =>
            {
                int byHash = entry.Properties[a].NameHash.CompareTo(entry.Properties[b].NameHash);
                return byHash != 0 ? byHash : a.CompareTo(b);
            });

            var slotOf = new int[entry.Properties.Count];
            for (int s = 0; s < sortedIndices.Count; s++)
            {
                slotOf[sortedIndices[s]] = s;
            }

            // Generate deduplication key for schema:
            var keyBuilder = new StringBuilder();
            keyBuilder.Append((char)entry.SchemaFlags);
            for (int s = 0; s < sortedIndices.Count; s++)
            {
                GdbPatchProperty prop = entry.Properties[sortedIndices[s]];
                keyBuilder.Append(prop.NameHash.ToString("X8"));
                keyBuilder.Append(':');
                keyBuilder.Append((byte)prop.Type);
                keyBuilder.Append(':');
                keyBuilder.Append(slotOf[s].ToString("X4"));
                keyBuilder.Append(';');
            }

            string schemaKey = keyBuilder.ToString();
            if (!schemaOffsets.TryGetValue(schemaKey, out uint schemaOffset))
            {
                schemaOffset = (uint)schemas.Count;
                schemaOffsets[schemaKey] = schemaOffset;

                // uint32 (propCount << 8 | schemaFlags)
                uint headerWord = ((uint)entry.Properties.Count << 8) | entry.SchemaFlags;
                AppendBe32(schemas, headerWord);

                // Sorted property name hashes:
                foreach (int index in sortedIndices)
                {
                    AppendBe32(schemas, entry.Properties[index].NameHash);
                }

                // Descriptor k: (type of sorted slot k) << 24 | sorted slot of declared property k.
                for (int s = 0; s < sortedIndices.Count; s++)
                {
                    uint descriptor = ((uint)(byte)entry.Properties[sortedIndices[s]].Type << 24) | (uint)slotOf[s];
                    AppendBe32(schemas, descriptor);
                }
            }

            recordOffsets[i] = records.Count;

            // Record body: schemaOffset + values in sorted-hash order:
            AppendBe32(records, schemaOffset);
            foreach (int index in sortedIndices)
            {
                AppendBe32(records, entry.Properties[index].Value);
            }
        }

        // 3. Compute interpolation stride and s16 deltas:
        int entryCount = order.Count;
        int recordsSize = records.Count;
        long stride = entryCount != 0 ? (((long)recordsSize << 8) / entryCount) : 0;
        var deltas = new short[entryCount];

        for (int i = 0; i < entryCount; i++)
        {
            long expectedOffset = (stride * i) >> 10;
            long delta = (recordOffsets[i] / 4) - expectedOffset;
            if (delta < short.MinValue || delta > short.MaxValue)
            {
                throw new InvalidOperationException(
                    $"Record layout exceeds s16 interpolation delta range for entry 0x{canonical.Entries[order[i]].Id:X8} (delta={delta})");
            }

            deltas[i] = (short)delta;
        }

        // 4. Names, filed as the container can hold them. The name table maps FNV-1(text) to text
        // and the guest recomputes the hash, so a record's authored name — which is an ALIAS for
        // most records, not the id — is filed under its own hash with an alias pair to the entry
        // (GdbPatchDatabase.FileName). A caller that wrote `Names[id] = name` meant exactly that,
        // and gets it: RecordName and NameOf resolve through the alias table.
        var ids = new HashSet<uint>(entryCount);
        foreach (GdbPatchEntry entry in canonical.Entries)
        {
            ids.Add(entry.Id);
        }

        foreach (var (hash, name) in database.Names)
        {
            canonical.FileName(hash, name, ids);
        }

        // Aliases, strictly ascending: the guest binary-searches them, so one alias naming two
        // entries is ambiguous and refused; the same pair stated twice is one pair. Every target
        // must be an entry of this container.
        var aliases = new List<(uint AliasHash, uint TargetEntryId)>(new HashSet<(uint, uint)>(canonical.Aliases));
        aliases.Sort((a, b) => a.AliasHash.CompareTo(b.AliasHash));

        for (int i = 0; i < aliases.Count; i++)
        {
            if (i != 0 && aliases[i].AliasHash == aliases[i - 1].AliasHash)
            {
                throw new InvalidOperationException(
                    $"Alias 0x{aliases[i].AliasHash:X8} names two entries (0x{aliases[i - 1].TargetEntryId:X8} and 0x{aliases[i].TargetEntryId:X8})");
            }

            if (!ids.Contains(aliases[i].TargetEntryId))
            {
                throw new InvalidOperationException(
                    $"Alias 0x{aliases[i].AliasHash:X8} names entry 0x{aliases[i].TargetEntryId:X8}, which this database does not hold");
            }
        }


        // 5. Emit header and payload:
        using var stream = new MemoryStream();

        // Magic 'GDB\0'
        stream.WriteByte((byte)'G');
        stream.WriteByte((byte)'D');
        stream.WriteByte((byte)'B');
        stream.WriteByte(0);

        WriteBe32(stream, (uint)entryCount);
        WriteBe32(stream, (uint)recordsSize);
        WriteBe32(stream, (uint)schemas.Count);
        WriteBe32(stream, (uint)aliases.Count);
        WriteBe32(stream, 0); // reserved

        // Records & schemas:
        stream.Write(records.ToArray(), 0, records.Count);
        stream.Write(schemas.ToArray(), 0, schemas.Count);

        // Entry IDs:
        for (int i = 0; i < entryCount; i++)
        {
            WriteBe32(stream, canonical.Entries[order[i]].Id);
        }

        // Deltas:
        for (int i = 0; i < entryCount; i++)
        {
            WriteBe16(stream, (ushort)deltas[i]);
        }

        // Padding if odd number of entries:
        if ((entryCount & 1) != 0)
        {
            WriteBe16(stream, 0);
        }

        // Aliases:
        foreach (var (aliasHash, targetEntryId) in aliases)
        {
            WriteBe32(stream, aliasHash);
            WriteBe32(stream, targetEntryId);
        }

        // 6. Name table tail: the pairs, then the registry's capacity and slot order
        // (GdbNameRegistry) — the offsets array is the tool's registry dumped slot by slot.
        var pairs = new List<byte>();
        var pairOffsets = new List<uint>();
        var pairHashes = new List<uint>();

        foreach (var (hash, name) in canonical.Names)
        {
            pairOffsets.Add((uint)pairs.Count);
            pairHashes.Add(hash);
            AppendBe32(pairs, hash);
            byte[] textBytes = Encoding.UTF8.GetBytes(name);
            pairs.AddRange(textBytes);
            pairs.Add(0); // null terminator
        }

        (uint capacity, int[] slotOrder) = GdbNameRegistry.Layout(pairHashes);
        WriteBe32(stream, capacity);
        WriteBe32(stream, (uint)pairs.Count);
        WriteBe32(stream, (uint)pairOffsets.Count);
        if (pairs.Count > 0)
        {
            stream.Write(pairs.ToArray(), 0, pairs.Count);
            foreach (int pair in slotOrder)
            {
                WriteBe32(stream, pairOffsets[pair]);
            }
        }

        byte[] written = stream.ToArray();

        // THE ROUND TRIP. Read back as the guest reads, then compare with what was intended.
        if (!GdbContainerCheck.TryVerify(written, out GdbPatchDatabase readBack, out string error))
        {
            throw new InvalidOperationException($"GDB writer produced a container the guest would refuse: {error}");
        }

        if (!GdbContainerCheck.SameContent(canonical, readBack, out string difference))
        {
            throw new InvalidOperationException($"GDB writer did not round-trip: {difference}");
        }

        return written;
    }

    /// <summary>
    /// <paramref name="entry"/> with its <c>parent</c> property declared first, the other
    /// properties in their given order; the same object when nothing moves.
    /// </summary>
    private static GdbPatchEntry ParentFirst(GdbPatchEntry entry)
    {
        int at = entry.Properties.FindIndex(p => p.NameHash == GdbView.HashParent);
        if (at <= 0)
        {
            return entry;
        }

        var moved = new GdbPatchEntry(entry.Id, entry.SchemaFlags);
        moved.Properties.Add(entry.Properties[at]);
        for (int i = 0; i < entry.Properties.Count; i++)
        {
            if (i != at)
            {
                moved.Properties.Add(entry.Properties[i]);
            }
        }

        return moved;
    }

    private static void AppendBe32(List<byte> list, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
        for (int i = 0; i < 4; i++)
        {
            list.Add(buffer[i]);
        }
    }

    private static void WriteBe32(Stream stream, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteBe16(Stream stream, ushort value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(buffer, value);
        stream.Write(buffer);
    }
}
