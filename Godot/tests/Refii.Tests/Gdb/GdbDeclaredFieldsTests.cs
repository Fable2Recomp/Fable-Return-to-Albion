namespace Refii.Tests;

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GdUnit4;
using Refii.Core;
using Refii.Gdb;
using static GdUnit4.Assertions;

/// <summary>
/// Verifies that <see cref="GdbView.DeclaredFields"/>, <see cref="GdbView.Fields"/>,
/// <see cref="GdbPatchBuilder"/>, and <see cref="GdbMulti.InheritedFields"/> preserve declaration
/// property slot order, repeated-hash array occurrences, and <c>SchemaFlags</c>.
/// Pure .NET: no Godot type reaches the test host.
/// </summary>
[TestSuite]
public class GdbDeclaredFieldsTests
{
    [TestCase]
    public void DeclaredFieldsReadsExactDeclarationOrderWhileRawFieldsRemainsSorted()
    {
        var db = new GdbPatchDatabase();
        const uint recordId = 0x986AF6DDu;
        var entry = new GdbPatchEntry(recordId, 0);

        // Intentionally non-ascending hashes (simulating DecalTextures[0..4]).
        (uint Hash, GdbFieldType Type, uint Raw)[] expected =
        {
            (0x50000004u, GdbFieldType.ResourceReference, 0x11110000u),
            (0x20000001u, GdbFieldType.ResourceReference, 0x11110001u),
            (0x80000007u, GdbFieldType.ResourceReference, 0x11110002u),
            (0x10000002u, GdbFieldType.ResourceReference, 0x11110003u),
            (0x40000009u, GdbFieldType.ResourceReference, 0x9BFA77BDu),
        };
        foreach (var (h, t, v) in expected)
        {
            entry.Properties.Add(new GdbPatchProperty(h, t, v));
        }

        db.Entries.Add(entry);
        byte[] bytes = GdbWriter.Serialize(db);

        AssertThat(GdbView.TryParse(bytes, out GdbView view, out string err)).IsTrue();
        AssertThat(err).IsEqual(string.Empty);
        AssertThat(view.TryLookup(recordId, out int idx)).IsTrue();

        // RawFields keeps sorted-hash order (required by GdbRawFieldsTests).
        var rawHashes = view.RawFields(idx).Select(f => f.Hash).ToArray();
        AssertThat(rawHashes.SequenceEqual(rawHashes.OrderBy(h => h))).IsTrue();
        AssertThat(rawHashes.SequenceEqual(expected.Select(e => e.Hash))).IsFalse();

        // DeclaredFields and Fields(idx) preserve declaration slot order (slot 4 is 0x9BFA77BD).
        var declared = view.DeclaredFields(idx).ToArray();
        AssertThat(declared.SequenceEqual(expected)).IsTrue();
        AssertThat(declared[4].Raw).IsEqual(0x9BFA77BDu);

        var named = view.Fields(idx);
        AssertThat(named.Select(f => (f.Hash, f.Type, f.Raw)).SequenceEqual(expected)).IsTrue();

        // Bounds & reset checks.
        AssertThat(view.DeclaredFields(-1).Any()).IsFalse();
        AssertThat(view.DeclaredFields(view.RecordCount).Count()).IsEqual(0);
        GdbView.DeclaredFieldList.Enumerator enumerator = view.DeclaredFields(idx).GetEnumerator();
        AssertThat(enumerator.MoveNext()).IsTrue();
        AssertThat(enumerator.Current.Hash).IsEqual(expected[0].Hash);
        enumerator.Reset();
        AssertThat(enumerator.MoveNext()).IsTrue();
        AssertThat(enumerator.Current.Hash).IsEqual(expected[0].Hash);
        enumerator.Dispose();

        // Non-generic enumerator works.
        int count = 0;
        foreach (object _ in (IEnumerable)view.DeclaredFields(idx))
        {
            count++;
        }

        AssertThat(count).IsEqual(expected.Length);
    }

    [TestCase]
    public void DeclaredFieldsForeachAllocatesNothing()
    {
        var db = new GdbPatchDatabase();
        for (uint r = 0; r < 16; r++)
        {
            var e = new GdbPatchEntry(0x1000u + (r * 0x100u), 1);
            for (uint f = 0; f < 6; f++)
            {
                e.Properties.Add(new GdbPatchProperty(0x9000u - (f * 0x77u), GdbFieldType.Int, r + f));
            }

            db.Entries.Add(e);
        }

        AssertThat(GdbView.TryParse(GdbWriter.Serialize(db), out GdbView view, out _)).IsTrue();

        static uint Sum(GdbView v)
        {
            uint s = 0;
            for (int i = 0; i < v.RecordCount; i++)
            {
                foreach ((uint h, GdbFieldType _, uint raw) in v.DeclaredFields(i))
                {
                    s += h ^ raw;
                }
            }

            return s;
        }

        Sum(view);
        long before = GC.GetAllocatedBytesForCurrentThread();
        uint sum = Sum(view);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        AssertThat(sum).IsNotEqual(0u);
        AssertThat(allocated).IsEqual(0L);
    }

    [TestCase]
    public void ApplyOverlayPreservesDeclaredOrderRepeatedHashesAndZeroSchemaFlags()
    {
        const uint recId = 0x44556677u;
        const uint newTuOnlyId = 0x778899AAu;
        const uint arrayHash = 0x33330001u;
        const uint firstHash = 0xF0000001u;
        const uint middleHash = 0x10000002u;
        const uint lastHash = 0x90000003u;

        var baseDb = new GdbPatchDatabase();
        var baseEntry = new GdbPatchEntry(recId, 0);
        baseEntry.Properties.Add(new GdbPatchProperty(firstHash, GdbFieldType.Int, 1));
        baseEntry.Properties.Add(new GdbPatchProperty(arrayHash, GdbFieldType.Int, 10));
        baseEntry.Properties.Add(new GdbPatchProperty(middleHash, GdbFieldType.Int, 2));
        baseEntry.Properties.Add(new GdbPatchProperty(arrayHash, GdbFieldType.Int, 20));
        baseEntry.Properties.Add(new GdbPatchProperty(arrayHash, GdbFieldType.Int, 30));
        baseEntry.Properties.Add(new GdbPatchProperty(lastHash, GdbFieldType.Int, 3));
        baseDb.Entries.Add(baseEntry);
        byte[] baseBytes = GdbWriter.Serialize(baseDb);

        // Build an overlay copying base via LoadOrCreatePatchDatabase, modifying slot 2 of array,
        // and appending a new record whose SchemaFlags is deliberately 0.
        GdbPatchDatabase tuDb = GdbPatchBuilder.LoadOrCreatePatchDatabase(baseBytes);
        tuDb.Entries[0].Properties[3].Value = 25;
        var added = new GdbPatchEntry(newTuOnlyId, 0);
        added.Properties.Add(new GdbPatchProperty(lastHash, GdbFieldType.Bool, 1));
        added.Properties.Add(new GdbPatchProperty(firstHash, GdbFieldType.Int, 77));
        tuDb.Entries.Add(added);

        byte[] mergedBytes = GdbPatchBuilder.ApplyOverlayToBaseGdb(baseBytes, GdbWriter.Serialize(tuDb));
        AssertThat(GdbView.TryParse(mergedBytes, out GdbView mergedView, out _)).IsTrue();

        AssertThat(mergedView.TryLookup(recId, out int mergedIdx)).IsTrue();
        AssertThat(mergedView.RecordSchemaFlags(mergedIdx)).IsEqual((byte)0);

        var fields = mergedView.DeclaredFields(mergedIdx).ToArray();
        AssertThat(fields.Length).IsEqual(6);
        AssertThat(fields[0]).IsEqual((firstHash, GdbFieldType.Int, 1u));
        AssertThat(fields[1]).IsEqual((arrayHash, GdbFieldType.Int, 10u));
        AssertThat(fields[2]).IsEqual((middleHash, GdbFieldType.Int, 2u));
        AssertThat(fields[3]).IsEqual((arrayHash, GdbFieldType.Int, 25u));
        AssertThat(fields[4]).IsEqual((arrayHash, GdbFieldType.Int, 30u));
        AssertThat(fields[5]).IsEqual((lastHash, GdbFieldType.Int, 3u));

        AssertThat(mergedView.TryLookup(newTuOnlyId, out int addedIdx)).IsTrue();
        AssertThat(mergedView.RecordSchemaFlags(addedIdx)).IsEqual((byte)0);
        var addedFields = mergedView.DeclaredFields(addedIdx).ToArray();
        AssertThat(addedFields[0].Hash).IsEqual(lastHash);
        AssertThat(addedFields[1].Hash).IsEqual(firstHash);
    }

    [TestCase]
    public void SynthesizeCompleteDatabasePreservesLocalDeclaredOrderAndRepeatedHashes()
    {
        const uint parentId = 0xAA000001u;
        const uint childId = 0xAA000002u;
        const uint inheritedOnly = 0x11112222u;
        const uint localHighHash = 0xF0001000u;
        const uint localLowHash = 0x05001000u;
        const uint arrayHash = 0x55551000u;

        var baseDb = new GdbPatchDatabase();
        var parent = new GdbPatchEntry(parentId, 0x01);
        parent.Properties.Add(new GdbPatchProperty(inheritedOnly, GdbFieldType.Int, 999));
        parent.Properties.Add(new GdbPatchProperty(localHighHash, GdbFieldType.Int, 50));
        baseDb.Entries.Add(parent);

        var child = new GdbPatchEntry(childId, 0x01);
        child.Properties.Add(new GdbPatchProperty(GdbView.HashParent, GdbFieldType.Reference, parentId));
        child.Properties.Add(new GdbPatchProperty(localHighHash, GdbFieldType.Int, 75));
        child.Properties.Add(new GdbPatchProperty(arrayHash, GdbFieldType.Int, 100));
        child.Properties.Add(new GdbPatchProperty(localLowHash, GdbFieldType.Int, 88));
        child.Properties.Add(new GdbPatchProperty(arrayHash, GdbFieldType.Int, 200));
        baseDb.Entries.Add(child);

        GdbMulti baseMulti = GdbMulti.Build(new[] { GdbWriter.Serialize(baseDb) });

        var sparseDb = new GdbPatchDatabase();
        var sparseChild = new GdbPatchEntry(childId, 0x01);
        sparseChild.Properties.Add(new GdbPatchProperty(localLowHash, GdbFieldType.Int, 99));
        sparseDb.Entries.Add(sparseChild);

        GdbPatchDatabase fullDb = GdbPatchBuilder.SynthesizeCompleteDatabase(sparseDb, baseMulti);
        byte[] fullBytes = GdbWriter.Serialize(fullDb);
        AssertThat(GdbView.TryParse(fullBytes, out GdbView view, out _)).IsTrue();
        AssertThat(view.TryLookup(childId, out int idx)).IsTrue();

        var fields = view.DeclaredFields(idx).ToArray();
        AssertThat(fields.Length).IsEqual(6);
        AssertThat(fields[0]).IsEqual((GdbView.HashParent, GdbFieldType.Reference, parentId));
        AssertThat(fields[1]).IsEqual((localHighHash, GdbFieldType.Int, 75u));
        AssertThat(fields[2]).IsEqual((arrayHash, GdbFieldType.Int, 100u));
        AssertThat(fields[3]).IsEqual((localLowHash, GdbFieldType.Int, 99u));
        AssertThat(fields[4]).IsEqual((arrayHash, GdbFieldType.Int, 200u));
        AssertThat(fields[5]).IsEqual((inheritedOnly, GdbFieldType.Int, 999u));
    }

    [TestCase]
    public void ShippedGlobalsDecalTexturesSlot4PreservedAcrossRoundTrip()
    {
        byte[]? bytes = GdbContainerRoundTripTests.ReadShippedGlobals();
        const string devGlobals = @"C:\Users\Miki\Documents\fable\Fable Return to Albion\assets\Fable2\game\data\globals\globals.gdb";
        if (bytes is null && File.Exists(devGlobals))
        {
            bytes = File.ReadAllBytes(devGlobals);
        }

        if (bytes is null)
        {
            Console.WriteLine("ShippedGlobalsDecalTexturesSlot4PreservedAcrossRoundTrip: globals.gdb not present; skipped");
            return;
        }

        const uint decalTexturesId = 0x986AF6DDu;
        const uint expectedBloodMedium = 0x9BFA77BDu; // FXTD_Blood_Decal_Medium_01.tex

        AssertThat(GdbView.TryParse(bytes, out GdbView stockView, out _)).IsTrue();
        AssertThat(stockView.TryLookup(decalTexturesId, out int stockIdx)).IsTrue();
        AssertThat(stockView.RecordSchemaFlags(stockIdx)).IsEqual((byte)0);

        var stockDeclared = stockView.DeclaredFields(stockIdx).ToArray();
        AssertThat(stockDeclared.Length).IsEqual(8);
        AssertThat(stockDeclared[4].Raw).IsEqual(expectedBloodMedium);

        // Simulate NewGameInventory / TU copy of DecalTextures + overlay merge onto stock globals.
        var tuDb = new GdbPatchDatabase();
        var copied = new GdbPatchEntry(decalTexturesId, stockView.RecordSchemaFlags(stockIdx));
        foreach (var (h, t, raw) in stockView.DeclaredFields(stockIdx))
        {
            copied.Properties.Add(new GdbPatchProperty(h, t, raw));
        }

        tuDb.Entries.Add(copied);
        byte[] tuBytes = GdbWriter.Serialize(tuDb);
        AssertThat(GdbView.TryParse(tuBytes, out GdbView tuView, out _)).IsTrue();
        AssertThat(tuView.TryLookup(decalTexturesId, out int tuIdx)).IsTrue();
        AssertThat(tuView.RecordSchemaFlags(tuIdx)).IsEqual((byte)0);
        AssertThat(tuView.DeclaredFields(tuIdx).ToArray()[4].Raw).IsEqual(expectedBloodMedium);
    }
}
