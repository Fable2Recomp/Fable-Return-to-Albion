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
}

/// <summary>
/// Managed binary GDB writer matching Fable 2's native 'GDB\0' database format
/// (decompiled from default.xex GDBFile_Parse 0x82A016C0 and mirrored by src/vfs/src/gdb/gdb_database.cpp).
/// </summary>
public static class GdbWriter
{
    public const int HeaderSize = 0x18;
    public const uint NameTableVersion = 0x00010000;

    public static byte[] Serialize(GdbPatchDatabase database)
    {
        if (database is null)
        {
            throw new ArgumentNullException(nameof(database));
        }

        // 1. Sort entries by ID ascending, verifying uniqueness:
        var order = new List<int>(database.Entries.Count);
        for (int i = 0; i < database.Entries.Count; i++)
        {
            order.Add(i);
        }

        order.Sort((a, b) => database.Entries[a].Id.CompareTo(database.Entries[b].Id));

        for (int i = 1; i < order.Count; i++)
        {
            if (database.Entries[order[i - 1]].Id == database.Entries[order[i]].Id)
            {
                throw new InvalidOperationException(
                    $"Duplicate GDB entry id 0x{database.Entries[order[i]].Id:X8}");
            }
        }

        var records = new List<byte>();
        var schemas = new List<byte>();
        var recordOffsets = new int[order.Count];
        var schemaOffsets = new Dictionary<string, uint>();

        // 2. Build schemas and records:
        for (int i = 0; i < order.Count; i++)
        {
            GdbPatchEntry entry = database.Entries[order[i]];
            if (entry.Properties.Count >= (1 << 24))
            {
                throw new InvalidOperationException(
                    $"Entry 0x{entry.Id:X8} has too many properties ({entry.Properties.Count})");
            }

            // Sorted indices by name_hash ascending (stable sort to preserve declaration order on duplicates):
            var sortedIndices = new List<int>(entry.Properties.Count);
            for (int p = 0; p < entry.Properties.Count; p++)
            {
                sortedIndices.Add(p);
            }

            sortedIndices.Sort((a, b) =>
                entry.Properties[a].NameHash.CompareTo(entry.Properties[b].NameHash));

            // Generate deduplication key for schema:
            var keyBuilder = new StringBuilder();
            keyBuilder.Append((char)entry.SchemaFlags);
            foreach (int index in sortedIndices)
            {
                GdbPatchProperty prop = entry.Properties[index];
                keyBuilder.Append(prop.NameHash.ToString("X8"));
                keyBuilder.Append(':');
                keyBuilder.Append((byte)prop.Type);
                keyBuilder.Append(':');
                keyBuilder.Append(index.ToString("X4"));
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

                // Descriptors: (typeByte << 24 | declarationOrder)
                foreach (int index in sortedIndices)
                {
                    uint descriptor = ((uint)(byte)entry.Properties[index].Type << 24) | (uint)index;
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
                    $"Record layout exceeds s16 interpolation delta range for entry 0x{database.Entries[order[i]].Id:X8} (delta={delta})");
            }

            deltas[i] = (short)delta;
        }

        // 4. Aliases:
        var aliases = new List<(uint AliasHash, uint TargetEntryId)>(database.Aliases);
        aliases.Sort((a, b) => a.AliasHash.CompareTo(b.AliasHash));

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
            WriteBe32(stream, database.Entries[order[i]].Id);
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

        // 6. Name table tail:
        var pairs = new List<byte>();
        var pairOffsets = new List<uint>();

        foreach (var (hash, name) in database.Names)
        {
            pairOffsets.Add((uint)pairs.Count);
            AppendBe32(pairs, hash);
            byte[] textBytes = Encoding.UTF8.GetBytes(name);
            pairs.AddRange(textBytes);
            pairs.Add(0); // null terminator
        }

        WriteBe32(stream, NameTableVersion);
        WriteBe32(stream, (uint)pairs.Count);
        WriteBe32(stream, (uint)pairOffsets.Count);
        if (pairs.Count > 0)
        {
            stream.Write(pairs.ToArray(), 0, pairs.Count);
            foreach (uint offset in pairOffsets)
            {
                WriteBe32(stream, offset);
            }
        }

        return stream.ToArray();
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
