using System;
using System.Collections.Generic;
using System.Text;

namespace Refii.Gdb;

public enum GdbFieldType : byte
{
    Bool = 0,

    Int = 1,

    Float = 3,

    String = 4,

    Enum = 5,

    Reference = 6,

    ResourceReference = 7,
}

public readonly struct GdbField
{
    public GdbField(uint hash, GdbFieldType type, uint raw, string name, string text)
    {
        Hash = hash;
        Type = type;
        Raw = raw;
        Name = name;
        Text = text;
    }

    public uint Hash { get; }

    public GdbFieldType Type { get; }

    public uint Raw { get; }

    public string Name { get; }

    public string Text { get; }

    public float AsFloat => BitConverter.Int32BitsToSingle(unchecked((int)Raw));

    public bool AsBool => Raw != 0;

    public bool IsReference =>
        Type is GdbFieldType.Reference or GdbFieldType.ResourceReference;

    public string Describe() => Type switch
    {
        GdbFieldType.Bool => AsBool ? "true" : "false",
        GdbFieldType.Int => Raw.ToString(),
        GdbFieldType.Float => AsFloat.ToString("0.######"),
        GdbFieldType.String => Text.Length != 0 ? $"\"{Text}\"" : $"<string 0x{Raw:X8}>",
        GdbFieldType.Enum => Text.Length != 0 ? Text : Raw.ToString(),
        _ when IsReference => Text.Length != 0
            ? $"→ {Text} (0x{Raw:X8})"
            : $"→ 0x{Raw:X8}",
        _ => $"0x{Raw:X8}",
    };
}

public sealed class GdbView
{
    public const int HeaderSize = 0x18;

    public const uint HashParent = 0x5F6317D5;
    public const uint HashVecX = 0x050C5D47;
    public const uint HashVecY = 0x050C5D46;
    public const uint HashVecZ = 0x050C5D45;

    private readonly byte[] _bytes;
    private readonly int[] _recordOffsets;

    private GdbView(byte[] bytes, int[] recordOffsets, int schemaBase, int hashBase,
        int offsetBase, int aliasBase, int aliasCount)
    {
        _bytes = bytes;
        _recordOffsets = recordOffsets;
        SchemaBase = schemaBase;
        HashBase = hashBase;
        OffsetBase = offsetBase;
        AliasBase = aliasBase;
        AliasCount = aliasCount;
    }

    /// <summary>Where the alias pairs start, and how many there are. See <see cref="TryLookup"/>.</summary>
    public int AliasBase { get; }

    public int AliasCount { get; }

    public int RecordCount => _recordOffsets.Length;

    public int SchemaBase { get; }

    public int HashBase { get; }

    public int OffsetBase { get; }

    public int BodyEnd => SchemaBase;

    public IReadOnlyDictionary<uint, string> Names { get; private set; } =
        new Dictionary<uint, string>();

    public IReadOnlyDictionary<uint, string> RecordNames { get; private set; } =
        new Dictionary<uint, string>();

    /// <summary>
    /// <see cref="TryParse"/>, but a blob is parsed ONCE and the view is reused.
    ///
    /// A parse walks every record header and allocates an index array, and the time-of-day path
    /// builds an extractor three times over — once to list the day-set times, then once for each
    /// end of the bracket it blends between. With the sky, lighting and cloud extracts on top, one
    /// re-evaluation re-parsed the whole chain about nine times to read numbers that had not moved.
    ///
    /// KEYED ON THE BLOB'S IDENTITY, NOT ITS CONTENT — no hashing, so the lookup is O(1) and cannot
    /// be fooled by two banks holding equal bytes. A ConditionalWeakTable rather than a Dictionary
    /// so a cached view dies with the blob that produced it; a level change drops its banks and the
    /// cache empties itself, where a strong map would pin every region ever visited.
    ///
    /// Callers that genuinely want a fresh parse (a re-read after a bank remount) should call
    /// <see cref="TryParse"/> directly.
    /// </summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<byte[], GdbView>
        SharedViews = new();

    public static bool TryParseShared(byte[] bytes, out GdbView view, out string error)
    {
        error = string.Empty;

        if (bytes is not null && SharedViews.TryGetValue(bytes, out GdbView? cached))
        {
            view = cached;
            return true;
        }

        if (!TryParse(bytes!, out view, out error))
        {
            return false;
        }

        SharedViews.AddOrUpdate(bytes!, view);
        return true;
    }

    public static bool TryParse(byte[] bytes, out GdbView view, out string error)
    {
        view = null!;
        error = string.Empty;

        if (bytes is null || bytes.Length < HeaderSize)
        {
            error = "too small for a .gdb header";
            return false;
        }

        if (bytes[0] != 'G' || bytes[1] != 'D' || bytes[2] != 'B' || bytes[3] != 0)
        {
            error = "missing GDB\\0 magic";
            return false;
        }

        uint count = ReadU32(bytes, 0x04);
        uint sizeA = ReadU32(bytes, 0x08);
        uint sizeB = ReadU32(bytes, 0x0C);
        uint aliasCount = ReadU32(bytes, 0x10);

        // THE ALIAS TABLE, which nothing here read before. Half the names the guest spawns rows
        // with are ALIASES, not entry ids — `AbilityHeavyWeaponsBase` is alias `0x064A4B7E` for
        // entry `0x19F1B0CD` — so a lookup by name found nothing and every ability reported
        // "IS NOT A GDB RECORD".
        //
        // It sits at the END of the payload, and the payload carries a two-byte alignment pad
        // for an odd entry count. Miss that pad and the pairs read two bytes out and decode as
        // plausible-looking rubbish.
        long payload = (long)sizeA + sizeB +
            (2 * ((3 * (long)count) + (count & 1) + (4 * (long)aliasCount)));
        long aliasBase = HeaderSize + payload - (8L * aliasCount);

        long schemaBase = HeaderSize + (long)sizeA;
        long hashBase = schemaBase + sizeB;
        long offsetBase = hashBase + (long)count * 4;

        if (schemaBase > bytes.Length || offsetBase + (long)count * 2 > bytes.Length ||
            aliasBase < 0 || aliasBase + (8L * aliasCount) > bytes.Length)
        {
            error = "declared region sizes run past the end of the file";
            return false;
        }

        var offsets = new int[count];
        int cursor = HeaderSize;
        for (uint i = 0; i < count; i++)
        {
            if (cursor + 4 > schemaBase)
            {
                error = $"record {i} starts past the end of the record region";
                return false;
            }

            offsets[i] = cursor;

            if (!TryReadSchemaHeader(
                    bytes, cursor, (int)schemaBase, (int)hashBase,
                    out _, out int fieldCount))
            {
                error = $"record {i} has an unreadable schema";
                return false;
            }

            long entrySize = 4 + (long)fieldCount * 4;
            if (cursor + entrySize > schemaBase)
            {
                error = $"record {i} overruns the record region";
                return false;
            }

            cursor += (int)entrySize;
        }

        view = new GdbView(
            bytes, offsets, (int)schemaBase, (int)hashBase, (int)offsetBase,
            (int)aliasBase, (int)aliasCount);
        LoadMetadata(bytes, view, count);
        return true;
    }

    public int RecordOffset(int index) => _recordOffsets[index];

    public uint RecordHash(int index) => ReadU32(_bytes, HashBase + index * 4);

    public string RecordName(int index) =>
        RecordNames.TryGetValue(RecordHash(index), out string? name) ? name : string.Empty;

    public bool TryLookup(uint hash, out int index)
    {
        int lo = 0;
        int hi = RecordCount;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) / 2);
            uint value = ReadU32(_bytes, HashBase + mid * 4);
            if (value < hash)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        index = lo;
        if (lo < RecordCount && ReadU32(_bytes, HashBase + lo * 4) == hash)
        {
            return true;
        }

        // NOT A RECORD — TRY THE ALIAS TABLE. `AbilityHeavyWeaponsBase` is not an entry id at
        // all; it is an alias for the entry that actually carries `Type`, `NameTextTag` and the
        // rest. Resolved once, never chained: the shipped tables map an alias straight to an
        // entry, and following a result that is itself an alias would invite a cycle.
        return TryResolveAlias(hash, out uint target) && TryLookupEntry(target, out index);
    }

    /// <summary>The entry an alias names, by binary search — the pairs are sorted by alias.</summary>
    public bool TryResolveAlias(uint alias, out uint target)
    {
        target = 0;
        int lo = 0;
        int hi = AliasCount;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) / 2);
            uint value = ReadU32(_bytes, AliasBase + (mid * 8));
            if (value < alias)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        if (lo >= AliasCount || ReadU32(_bytes, AliasBase + (lo * 8)) != alias)
        {
            return false;
        }

        target = ReadU32(_bytes, AliasBase + (lo * 8) + 4);
        return true;
    }

    /// <summary>
    /// Every alias pair as stored — FNV-1(name) to entry hash. The table is the only bridge from
    /// a record's NAME to its entry: the strings for most pairs are absent, so an alias can never
    /// be reconstructed by hashing a name. <see cref="GdbMulti"/> indexes these at build.
    /// </summary>
    public IEnumerable<(uint Alias, uint Target)> AliasPairs()
    {
        for (int i = 0; i < AliasCount; i++)
        {
            int at = AliasBase + (i * 8);
            yield return (ReadU32(_bytes, at), ReadU32(_bytes, at + 4));
        }
    }

    /// <summary>The record lookup alone, with no alias fallback — what breaks the recursion.</summary>
    private bool TryLookupEntry(uint hash, out int index)
    {
        int lo = 0;
        int hi = RecordCount;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) / 2);
            uint value = ReadU32(_bytes, HashBase + mid * 4);
            if (value < hash)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        index = lo;
        return lo < RecordCount && ReadU32(_bytes, HashBase + lo * 4) == hash;
    }

    public List<GdbField> Fields(int index)
    {
        var fields = new List<GdbField>();
        int record = _recordOffsets[index];
        if (!TryReadSchemaHeader(
                _bytes, record, SchemaBase, HashBase, out int schemaOffset, out int fieldCount))
        {
            return fields;
        }

        int hashes = schemaOffset + 4;
        int descriptors = hashes + (fieldCount * 4);

        for (int i = 0; i < fieldCount; i++)
        {
            int slot = record + 4 + (i * 4);
            if (slot + 4 > BodyEnd)
            {
                break;
            }

            uint fieldHash = ReadU32(_bytes, hashes + (i * 4));
            uint descriptor = ReadU32(_bytes, descriptors + (i * 4));
            var type = (GdbFieldType)(byte)(descriptor >> 24);
            uint raw = ReadU32(_bytes, slot);

            Names.TryGetValue(fieldHash, out string? name);
            fields.Add(new GdbField(
                fieldHash, type, raw, name ?? string.Empty, ResolveValueText(type, raw)));
        }

        return fields;
    }

    private string ResolveValueText(GdbFieldType type, uint raw) => type switch
    {
        GdbFieldType.String or GdbFieldType.Enum =>
            Names.TryGetValue(raw, out string? text) ? text : string.Empty,
        GdbFieldType.Reference or GdbFieldType.ResourceReference =>
            RecordNames.TryGetValue(raw, out string? name) ? name : string.Empty,
        _ => string.Empty,
    };

    public int FieldCount(int index) =>
        TryReadSchemaHeader(
            _bytes, _recordOffsets[index], SchemaBase, HashBase, out _, out int count)
            ? count
            : 0;

    public bool TryFindLocal(int index, uint fieldHash, int expectedType, out GdbField found)
    {
        found = default;
        if (!TryFindLocalRaw(index, fieldHash, expectedType, out uint raw, out GdbFieldType type))
        {
            return false;
        }

        Names.TryGetValue(fieldHash, out string? name);
        found = new GdbField(
            fieldHash, type, raw, name ?? string.Empty, ResolveValueText(type, raw));
        return true;
    }

    public bool TryFindLocalRaw(
        int index, uint fieldHash, int expectedType, out uint raw, out GdbFieldType type)
    {
        raw = 0;
        if (!TryFindValueSlot(index, fieldHash, expectedType, out int slot, out type))
        {
            return false;
        }

        raw = ReadU32(_bytes, slot);
        return true;
    }

    /// <summary>
    /// WHERE a record's own value for <paramref name="fieldHash"/> lives, as a byte offset.
    ///
    /// A record is a schema pointer followed by one u32 per property, in the schema's sorted-hash
    /// order — so the value is at <c>record + 4 + i * 4</c> for the i'th hash. Exposing the offset
    /// rather than the value is what lets the overlay writer PATCH a property in place, which it
    /// cannot do from a read alone.
    ///
    /// LOCAL, deliberately: an inherited property lives on the ancestor that declares it, and
    /// writing it here would silently edit the wrong record.
    /// </summary>
    public bool TryFindValueSlot(
        int index, uint fieldHash, int expectedType, out int slot, out GdbFieldType type)
    {
        slot = -1;
        type = default;

        if (index < 0 || index >= RecordCount)
        {
            return false;
        }

        int record = _recordOffsets[index];
        if (!TryReadSchemaHeader(
                _bytes, record, SchemaBase, HashBase, out int schemaOffset, out int fieldCount))
        {
            return false;
        }

        int hashes = schemaOffset + 4;
        int descriptors = hashes + (fieldCount * 4);

        for (int i = 0; i < fieldCount; i++)
        {
            if (ReadU32(_bytes, hashes + (i * 4)) != fieldHash)
            {
                continue;
            }

            var found = (GdbFieldType)(byte)(ReadU32(_bytes, descriptors + (i * 4)) >> 24);
            if (expectedType >= 0 && (int)found != expectedType)
            {
                return false;
            }

            int at = record + 4 + (i * 4);
            if (at + 4 > BodyEnd)
            {
                return false;
            }

            slot = at;
            type = found;
            return true;
        }

        return false;
    }

    public IEnumerable<(uint Hash, GdbFieldType Type, uint Raw)> RawFields(int index)
    {
        if (index < 0 || index >= RecordCount)
        {
            yield break;
        }

        int record = _recordOffsets[index];
        if (!TryReadSchemaHeader(
                _bytes, record, SchemaBase, HashBase, out int schemaOffset, out int fieldCount))
        {
            yield break;
        }

        int hashes = schemaOffset + 4;
        int descriptors = hashes + (fieldCount * 4);

        for (int i = 0; i < fieldCount; i++)
        {
            int slot = record + 4 + (i * 4);
            if (slot + 4 > BodyEnd)
            {
                yield break;
            }

            yield return (
                ReadU32(_bytes, hashes + (i * 4)),
                (GdbFieldType)(byte)(ReadU32(_bytes, descriptors + (i * 4)) >> 24),
                ReadU32(_bytes, slot));
        }
    }

    public byte RecordSchemaFlags(int index)
    {
        if (index < 0 || index >= RecordCount)
        {
            return 0;
        }

        int record = _recordOffsets[index];
        if (!TryReadSchemaHeader(
                _bytes, record, SchemaBase, HashBase, out int schemaOffset, out _))
        {
            return 0;
        }

        return (byte)ReadU32(_bytes, schemaOffset);
    }

    public uint ReadU32At(int at) =>
        at >= 0 && at + 4 <= _bytes.Length ? ReadU32(_bytes, at) : 0u;

    public float ReadF32At(int at) =>
        BitConverter.Int32BitsToSingle(unchecked((int)ReadU32At(at)));

    public bool TryPayloadRange(int index, out int start, out int end)
    {
        start = 0;
        end = 0;

        if (index < 0 || index >= RecordCount)
        {
            return false;
        }

        int record = _recordOffsets[index];
        if (!TryReadSchemaHeader(
                _bytes, record, SchemaBase, HashBase, out _, out int fieldCount))
        {
            return false;
        }

        start = record + 4 + (fieldCount * 4);
        if (start > BodyEnd)
        {
            return false;
        }

        end = index + 1 < RecordCount ? _recordOffsets[index + 1] : BodyEnd;
        end = Math.Min(end, start + 0x200);
        return start + 4 <= end;
    }

    public bool TryFindFieldOwner(
        int index, uint fieldHash, int expectedType, out GdbField found, out int owner)
    {
        found = default;
        owner = -1;

        int current = index;
        for (int depth = 0; depth < 64; depth++)
        {
            if (TryFindLocal(current, fieldHash, expectedType, out found))
            {
                owner = current;
                return true;
            }

            if (!TryFindLocal(current, HashParent, 6, out GdbField parent) || parent.Raw == 0)
            {
                return false;
            }

            if (!TryLookup(parent.Raw, out int parentIndex) || parentIndex == current)
            {
                return false;
            }

            current = parentIndex;
        }

        return false;
    }

    public bool TryReadVec3Record(int index, out float x, out float y, out float z)
    {
        x = 0f;
        y = 0f;
        z = 0f;

        if (!TryFindLocalRaw(index, HashVecX, (int)GdbFieldType.Float, out uint rx, out _) ||
            !TryFindLocalRaw(index, HashVecY, (int)GdbFieldType.Float, out uint ry, out _) ||
            !TryFindLocalRaw(index, HashVecZ, (int)GdbFieldType.Float, out uint rz, out _))
        {
            return false;
        }

        x = BitConverter.Int32BitsToSingle(unchecked((int)rx));
        y = BitConverter.Int32BitsToSingle(unchecked((int)ry));
        z = BitConverter.Int32BitsToSingle(unchecked((int)rz));

        return float.IsFinite(x) && float.IsFinite(y) && float.IsFinite(z);
    }

    public bool TryReadVec3Ref(uint hash, out float x, out float y, out float z)
    {
        x = 0f;
        y = 0f;
        z = 0f;
        return TryLookup(hash, out int index) && TryReadVec3Record(index, out x, out y, out z);
    }

    private static bool TryReadSchemaHeader(
        byte[] bytes, int record, int schemaBase, int hashBase,
        out int schemaOffset, out int fieldCount)
    {
        schemaOffset = 0;
        fieldCount = 0;

        if (record + 4 > schemaBase)
        {
            return false;
        }

        uint relative = ReadU32(bytes, record);
        long offset = schemaBase + (long)relative;
        if (offset + 4 > hashBase)
        {
            return false;
        }

        schemaOffset = (int)offset;
        uint header = ReadU32(bytes, schemaOffset);
        long count = header >> 8;

        if (count > 256)
        {
            int p = schemaOffset;
            long alternative = (bytes[p] | ((long)bytes[p + 1] << 8)) + bytes[p + 2];
            if (schemaOffset + 4 + (count * 8) > hashBase)
            {
                count = alternative;
            }
        }

        if (count < 0 || schemaOffset + 4 + (count * 8) > hashBase)
        {
            return false;
        }

        fieldCount = (int)count;
        return true;
    }

    private static void LoadMetadata(byte[] bytes, GdbView view, uint count)
    {
        var names = new Dictionary<uint, string>();
        var recordNames = new Dictionary<uint, string>();

        try
        {
            uint namePairs = ReadU32(bytes, 0x10);
            long metaEnd = view.OffsetBase + (long)count * 2;
            long nameBase = (metaEnd + 3) & ~3L;
            long dictBase = nameBase + (long)namePairs * 8;

            // The tail's first word is the registry capacity the names were laid out in — a
            // power of two, 0x10000 until the registry has to grow (GdbNameRegistry).
            if (dictBase + 12 > bytes.Length)
            {
                return;
            }

            uint capacity = ReadU32(bytes, (int)dictBase);
            if (capacity == 0 || (capacity & (capacity - 1)) != 0)
            {
                return;
            }

            uint dataBytes = ReadU32(bytes, (int)dictBase + 4);
            uint stringCount = ReadU32(bytes, (int)dictBase + 8);
            long dataStart = dictBase + 12;
            long dataEnd = dataStart + dataBytes;

            if (dataEnd > bytes.Length)
            {
                return;
            }

            long offset = dataStart;
            for (uint i = 0; i < stringCount && offset + 5 <= dataEnd; i++)
            {
                uint hash = ReadU32(bytes, (int)offset);
                offset += 4;

                long terminator = offset;
                while (terminator < dataEnd && bytes[terminator] != 0)
                {
                    terminator++;
                }

                names[hash] = Encoding.UTF8.GetString(
                    bytes, (int)offset, (int)(terminator - offset));
                offset = terminator + 1;
            }

            for (uint i = 0; i < namePairs; i++)
            {
                long pair = nameBase + ((long)i * 8);
                if (pair + 8 > bytes.Length)
                {
                    break;
                }

                uint nameHash = ReadU32(bytes, (int)pair);
                uint recordHash = ReadU32(bytes, (int)pair + 4);
                if (names.TryGetValue(nameHash, out string? text) && text.Length != 0)
                {
                    recordNames[recordHash] = text;
                }
            }
        }
        catch (Exception)
        {
        }
        finally
        {
            view.Names = names;
            view.RecordNames = recordNames;
        }
    }

    private static uint ReadU32(byte[] data, int at) =>
        ((uint)data[at] << 24) | ((uint)data[at + 1] << 16) |
        ((uint)data[at + 2] << 8) | data[at + 3];
}
