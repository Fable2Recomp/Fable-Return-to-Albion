namespace Refii.Tests;

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Gdb;

/// <summary>
/// <see cref="GdbView.RawFields"/> as a struct enumerator, against the iterator it replaced: the same
/// fields in the same order on good records and damaged ones, nothing on a bad index or schema, and
/// still usable from LINQ. Pure .NET: no Godot type reaches the test host.
/// </summary>
[TestSuite]
public class GdbRawFieldsTests
{
    private static readonly MethodInfo SchemaHeader =
        typeof(GdbView).GetMethod("TryReadSchemaHeader", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static bool TryReadSchemaHeader(GdbView view, byte[] bytes, int record, out int schemaOffset, out int fieldCount)
    {
        object[] args = { bytes, record, view.SchemaBase, view.HashBase, 0, 0 };
        bool read = (bool)SchemaHeader.Invoke(null, args)!;
        schemaOffset = (int)args[4];
        fieldCount = (int)args[5];
        return read;
    }

    private static uint ReadU32(byte[] data, int at) =>
        ((uint)data[at] << 24) | ((uint)data[at + 1] << 16) | ((uint)data[at + 2] << 8) | data[at + 3];

    private static void WriteU32(byte[] data, int at, uint value)
    {
        data[at] = (byte)(value >> 24);
        data[at + 1] = (byte)(value >> 16);
        data[at + 2] = (byte)(value >> 8);
        data[at + 3] = (byte)value;
    }

    /// <summary>The iterator as it stood before the struct enumerator, unchanged but for reaching the
    /// view's private fields from outside.</summary>
    private static IEnumerable<(uint Hash, GdbFieldType Type, uint Raw)> OldRawFields(GdbView view, byte[] bytes, int index)
    {
        if (index < 0 || index >= view.RecordCount)
        {
            yield break;
        }

        int record = view.RecordOffset(index);
        if (!TryReadSchemaHeader(view, bytes, record, out int schemaOffset, out int fieldCount))
        {
            yield break;
        }

        int hashes = schemaOffset + 4;
        int descriptors = hashes + (fieldCount * 4);

        for (int i = 0; i < fieldCount; i++)
        {
            int slot = record + 4 + (i * 4);
            if (slot + 4 > view.BodyEnd)
            {
                yield break;
            }

            yield return (
                ReadU32(bytes, hashes + (i * 4)),
                (GdbFieldType)(byte)(ReadU32(bytes, descriptors + (i * 4)) >> 24),
                ReadU32(bytes, slot));
        }
    }

    private static List<(uint Hash, GdbFieldType Type, uint Raw)> Walked(GdbView view, int index)
    {
        var fields = new List<(uint Hash, GdbFieldType Type, uint Raw)>();
        foreach ((uint hash, GdbFieldType type, uint raw) in view.RawFields(index))
        {
            fields.Add((hash, type, raw));
        }

        return fields;
    }

    /// <summary>Records of no, one and many fields, of every plain type, some sharing a schema.</summary>
    private static byte[] Blob()
    {
        var db = new GdbPatchDatabase();
        GdbFieldType[] types =
        {
            GdbFieldType.Bool, GdbFieldType.Int, GdbFieldType.Float, GdbFieldType.Bool, GdbFieldType.Int,
        };
        for (uint r = 0; r < 40; r++)
        {
            var entry = new GdbPatchEntry(0x1000 + (r * 0x1111), 1);
            int fields = r < 2 ? 0 : (int)(r % 7);
            for (uint f = 0; f < fields; f++)
            {
                entry.Properties.Add(new GdbPatchProperty(
                    0x200 + (f * 0x33), types[(int)(f % types.Length)], (r << 16) | (f + 1)));
            }

            db.Entries.Add(entry);
        }

        return GdbWriter.Serialize(db);
    }

    private static GdbView Parsed(byte[] bytes)
    {
        AssertThat(GdbView.TryParse(bytes, out GdbView view, out string error)).IsTrue();
        AssertThat(error).IsEqual(string.Empty);
        return view;
    }

    private static string Text(IEnumerable<(uint Hash, GdbFieldType Type, uint Raw)> fields) =>
        string.Join(",", fields.Select(f => $"{f.Hash:x8}/{f.Type}/{f.Raw:x8}"));

    private static void AssertSameAsOld(GdbView view, byte[] bytes, int index)
    {
        string old = Text(OldRawFields(view, bytes, index));
        AssertThat(Text(Walked(view, index))).IsEqual(old);
        AssertThat(Text(view.RawFields(index))).IsEqual(old);
    }

    [TestCase]
    public void EveryRecordGivesWhatTheOldIteratorGave()
    {
        byte[] bytes = Blob();
        GdbView view = Parsed(bytes);
        AssertThat(view.RecordCount).IsEqual(40);
        int fields = 0;
        for (int i = 0; i < view.RecordCount; i++)
        {
            AssertSameAsOld(view, bytes, i);
            fields += Walked(view, i).Count;
        }

        // Non-trivial: the comparison above is not all-empty against all-empty.
        AssertThat(fields).IsGreater(100);
    }

    [TestCase]
    public void TheValuesAreTheOnesWritten()
    {
        byte[] bytes = Blob();
        GdbView view = Parsed(bytes);
        AssertThat(view.TryLookup(0x1000 + (6 * 0x1111), out int index)).IsTrue();
        List<(uint Hash, GdbFieldType Type, uint Raw)> fields = Walked(view, index);
        AssertThat(fields.Count).IsEqual(6);
        AssertThat(fields.Select(f => f.Hash).OrderBy(h => h).SequenceEqual(fields.Select(f => f.Hash))).IsTrue();
        foreach ((uint hash, GdbFieldType type, uint raw) in fields)
        {
            uint f = (hash - 0x200) / 0x33;
            AssertThat(raw).IsEqual((6u << 16) | (f + 1));
            AssertThat(view.TryFindLocalRaw(index, hash, (int)type, out uint found, out _)).IsTrue();
            AssertThat(found).IsEqual(raw);
        }
    }

    /// <summary>The shipped data, when it is here: every record of the real database.</summary>
    [TestCase]
    public void ShippedGlobalsGiveWhatTheOldIteratorGave()
    {
        byte[]? bytes = GdbContainerRoundTripTests.ReadShippedGlobals();
        if (bytes is null)
        {
            Console.WriteLine("ShippedGlobalsGiveWhatTheOldIteratorGave: packaged globals.gdb not present; skipped");
            return;
        }

        GdbView view = Parsed(bytes);
        for (int i = 0; i < view.RecordCount; i++)
        {
            if (!view.RawFields(i).SequenceEqual(OldRawFields(view, bytes, i)))
            {
                AssertThat($"record {i} differs").IsEqual(string.Empty);
            }
        }
    }

    [TestCase]
    public void AnIndexOutOfRangeHasNoFields()
    {
        byte[] bytes = Blob();
        GdbView view = Parsed(bytes);
        foreach (int index in new[] { -1, view.RecordCount, view.RecordCount + 1, int.MinValue, int.MaxValue })
        {
            AssertThat(Walked(view, index).Count).IsEqual(0);
            AssertThat(view.RawFields(index).Any()).IsFalse();
            AssertThat(view.RawFields(index).Count()).IsEqual(0);
            AssertThat(view.RawFields(index).ToList().Count).IsEqual(0);
        }
    }

    /// <summary>TryParse checked the schemas when it ran, but the blob is the bank's own array and a
    /// record is read again on every walk.</summary>
    [TestCase]
    public void AnUnreadableSchemaHasNoFields()
    {
        byte[] bytes = Blob();
        GdbView view = Parsed(bytes);
        AssertThat(view.TryLookup(0x1000 + (6 * 0x1111), out int index)).IsTrue();
        AssertThat(Walked(view, index).Count).IsEqual(6);
        int record = view.RecordOffset(index);
        uint pointer = ReadU32(bytes, record);
        int schema = view.SchemaBase + (int)pointer;
        uint header = ReadU32(bytes, schema);

        // The pointer leaves the schema region.
        foreach (uint wrong in new[] { 0xFFFFFFFFu, 0x7FFFFFFFu, (uint)(view.HashBase - view.SchemaBase - 3) })
        {
            WriteU32(bytes, record, wrong);
            AssertThat(Walked(view, index).Count).IsEqual(0);
            AssertThat(view.RawFields(index).Any()).IsFalse();
            AssertSameAsOld(view, bytes, index);
        }

        WriteU32(bytes, record, pointer);
        AssertThat(Walked(view, index).Count).IsEqual(6);

        // The header claims more fields than the region holds, on both of its readings.
        WriteU32(bytes, schema, 0xFFFFFFFFu);
        AssertThat(Walked(view, index).Count).IsEqual(0);
        AssertThat(view.RawFields(index).ToList().Count).IsEqual(0);
        AssertSameAsOld(view, bytes, index);

        WriteU32(bytes, schema, header);
        AssertThat(Walked(view, index).Count).IsEqual(6);
    }

    /// <summary>A record whose schema has more fields than the record region has room for ends with
    /// the region, without reading past it.</summary>
    [TestCase]
    public void ARecordRunningPastTheBodyStopsAtItsEnd()
    {
        byte[] bytes = Blob();
        GdbView view = Parsed(bytes);
        int last = view.RecordCount - 1;
        int bigger = Enumerable.Range(0, view.RecordCount).MaxBy(i => Walked(view, i).Count);
        int roomInLast = (view.BodyEnd - view.RecordOffset(last) - 4) / 4;
        int fieldsInBigger = Walked(view, bigger).Count;
        WriteU32(bytes, view.RecordOffset(last), ReadU32(bytes, view.RecordOffset(bigger)));

        AssertThat(roomInLast).IsLess(fieldsInBigger);
        AssertThat(Walked(view, last).Count).IsEqual(roomInLast);
        AssertSameAsOld(view, bytes, last);
    }

    /// <summary>Random damage to the records and schemas: both forms still read the same things.</summary>
    [TestCase]
    public void RandomDamageReadsTheSameAsTheOldIterator()
    {
        byte[] bytes = Blob();
        GdbView view = Parsed(bytes);
        var random = new Random(11);
        int damagedRegion = view.HashBase;
        for (int round = 0; round < 4000; round++)
        {
            int at = random.Next(damagedRegion);
            byte was = bytes[at];
            byte was2 = bytes[Math.Min(at + 1, damagedRegion - 1)];
            bytes[at] = (byte)random.Next(256);
            bytes[Math.Min(at + 1, damagedRegion - 1)] = (byte)random.Next(256);
            for (int i = 0; i < view.RecordCount; i++)
            {
                AssertSameAsOld(view, bytes, i);
            }

            bytes[at] = was;
            bytes[Math.Min(at + 1, damagedRegion - 1)] = was2;
        }
    }

    [TestCase]
    public void LinqSeesTheFields()
    {
        byte[] bytes = Blob();
        GdbView view = Parsed(bytes);
        AssertThat(view.TryLookup(0x1000 + (6 * 0x1111), out int index)).IsTrue();
        List<(uint Hash, GdbFieldType Type, uint Raw)> walked = Walked(view, index);

        GdbView.RawFieldList list = view.RawFields(index);
        AssertThat(list.Count()).IsEqual(6);
        AssertThat(list.Any()).IsTrue();
        AssertThat(list.Any(f => f.Type == GdbFieldType.Float)).IsTrue();
        AssertThat(list.First().Hash).IsEqual(walked[0].Hash);
        AssertThat(list.Last().Hash).IsEqual(walked[5].Hash);
        AssertThat(list.Skip(2).First().Raw).IsEqual(walked[2].Raw);
        AssertThat(list.Select(f => f.Raw).SequenceEqual(walked.Select(f => f.Raw))).IsTrue();
        AssertThat(list.Where(f => f.Type == GdbFieldType.Int).Count()).IsEqual(walked.Count(f => f.Type == GdbFieldType.Int));
        AssertThat(list.ToDictionary(f => f.Hash, f => f.Raw)[walked[3].Hash]).IsEqual(walked[3].Raw);
        AssertThat(list.ToArray().Length).IsEqual(6);

        // Enumerated twice, and each enumerator on its own.
        AssertThat(list.ToList().SequenceEqual(list.ToList())).IsTrue();
        using IEnumerator<(uint Hash, GdbFieldType Type, uint Raw)> one = ((IEnumerable<(uint, GdbFieldType, uint)>)list).GetEnumerator();
        using IEnumerator<(uint Hash, GdbFieldType Type, uint Raw)> two = ((IEnumerable<(uint, GdbFieldType, uint)>)list).GetEnumerator();
        AssertThat(one.MoveNext()).IsTrue();
        AssertThat(one.MoveNext()).IsTrue();
        AssertThat(two.MoveNext()).IsTrue();
        AssertThat(one.Current.Hash).IsEqual(walked[1].Hash);
        AssertThat(two.Current.Hash).IsEqual(walked[0].Hash);

        // The non-generic form boxes each field the way the iterator's did.
        var boxed = new List<object>();
        foreach (object field in (IEnumerable)list)
        {
            boxed.Add(field);
        }

        AssertThat(boxed.Count).IsEqual(6);
        AssertThat(((uint Hash, GdbFieldType Type, uint Raw))boxed[4]).IsEqual(walked[4]);

        // Nothing to walk: the default of the struct, as a bad index returns.
        GdbView.RawFieldList none = default;
        AssertThat(none.Any()).IsFalse();
        AssertThat(none.FirstOrDefault()).IsEqual(default((uint, GdbFieldType, uint)));
        AssertThat(none.ToList().Count).IsEqual(0);
    }

    [TestCase]
    public void AnEnumeratorCanBeRewound()
    {
        byte[] bytes = Blob();
        GdbView view = Parsed(bytes);
        AssertThat(view.TryLookup(0x1000 + (6 * 0x1111), out int index)).IsTrue();
        GdbView.RawFieldList.Enumerator walker = view.RawFields(index).GetEnumerator();
        int seen = 0;
        while (walker.MoveNext()) seen++;
        AssertThat(seen).IsEqual(6);
        AssertThat(walker.MoveNext()).IsFalse();
        walker.Reset();
        AssertThat(walker.MoveNext()).IsTrue();
        AssertThat(walker.Current.Hash).IsEqual(Walked(view, index)[0].Hash);
        walker.Dispose();
    }

    /// <summary>The point of the struct: a load's animation scan walks millions of records.</summary>
    [TestCase]
    public void ForeachAllocatesNothing()
    {
        byte[] bytes = Blob();
        GdbView view = Parsed(bytes);
        Sum(view);

        long before = GC.GetAllocatedBytesForCurrentThread();
        uint sum = Sum(view);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        AssertThat(sum).IsNotEqual(0u);
        AssertThat(allocated).IsEqual(0L);
    }

    private static uint Sum(GdbView view)
    {
        uint sum = 0;
        for (int i = 0; i < view.RecordCount; i++)
        {
            foreach ((uint hash, GdbFieldType _, uint raw) in view.RawFields(i))
            {
                sum += hash ^ raw;
            }
        }

        return sum;
    }
}
