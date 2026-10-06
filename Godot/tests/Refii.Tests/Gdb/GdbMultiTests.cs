namespace Refii.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using GdUnit4;
using static GdUnit4.Assertions;
using Godot;
using Refii.Gdb;

[TestSuite]
public class GdbMultiTests
{
    private const uint ToyGunId = 0x2FDDF213;
    private const uint ParentArchetypeId = 0xEF0BF7F4;

    private const uint HashParent = GdbView.HashParent;
    private const uint HashBulletCapacity = 0xAABED971;
    private const uint HashRange = 0xF21E235C;
    private const uint HashReloadOnlyIfEmpty = 0x30AB25EC;

    [TestCase]
    public void TestPatchViewOverwritesBaseViewInLookup()
    {
        // Base GDB: ToyGun with capacity = 1
        var baseDb = new GdbPatchDatabase();
        var baseToy = new GdbPatchEntry(ToyGunId, 0x01);
        baseToy.Properties.Add(new GdbPatchProperty(HashBulletCapacity, GdbFieldType.Int, 1));
        baseDb.Entries.Add(baseToy);
        byte[] baseBytes = GdbWriter.Serialize(baseDb);

        // Patch GDB: ToyGun with capacity = 10
        var patchDb = new GdbPatchDatabase();
        var patchToy = new GdbPatchEntry(ToyGunId, 0x01);
        patchToy.Properties.Add(new GdbPatchProperty(HashBulletCapacity, GdbFieldType.Int, 10));
        patchDb.Entries.Add(patchToy);
        byte[] patchBytes = GdbWriter.Serialize(patchDb);

        // Multi built with base first, patch second
        var multi = GdbMulti.Build(new byte[]?[] { baseBytes, patchBytes });

        AssertThat(multi.Views.Count).IsEqual(2);
        AssertThat(multi.TryLookup(ToyGunId, out GdbCursor cursor)).IsTrue();

        // The active cursor should come from the patch view (view index 1)
        AssertThat(multi.ViewIndexOf(ToyGunId)).IsEqual(1);

        // Value of capacity on active cursor must be 10:
        AssertThat(cursor.View.TryFindLocalRaw(cursor.Record, HashBulletCapacity, -1, out uint capRaw, out _)).IsTrue();
        AssertThat(capRaw).IsEqual(10u);
    }

    [TestCase]
    public void TestUnderlyingCursorAndParentTraversalInheritance()
    {
        // Base GDB contains:
        // 1. Parent Archetype with Range = 12.0f and ReloadOnlyIfEmpty = true
        // 2. Toy Gun with BulletCapacity = 1 and parent = ParentArchetypeId
        var baseDb = new GdbPatchDatabase();

        var parentEntry = new GdbPatchEntry(ParentArchetypeId, 0x01);
        parentEntry.Properties.Add(new GdbPatchProperty(HashRange, GdbFieldType.Float, 0x41400000)); // 12.0f
        parentEntry.Properties.Add(new GdbPatchProperty(HashReloadOnlyIfEmpty, GdbFieldType.Bool, 1));
        baseDb.Entries.Add(parentEntry);

        var baseToy = new GdbPatchEntry(ToyGunId, 0x01);
        baseToy.Properties.Add(new GdbPatchProperty(HashBulletCapacity, GdbFieldType.Int, 1));
        baseToy.Properties.Add(new GdbPatchProperty(HashParent, GdbFieldType.Reference, ParentArchetypeId));
        baseDb.Entries.Add(baseToy);

        byte[] baseBytes = GdbWriter.Serialize(baseDb);

        // Sparse Patch GDB contains:
        // Toy Gun with ONLY BulletCapacity = 15 (sparse patch, Range and ReloadOnlyIfEmpty are omitted)
        var patchDb = new GdbPatchDatabase();
        var patchToy = new GdbPatchEntry(ToyGunId, 0x01);
        patchToy.Properties.Add(new GdbPatchProperty(HashBulletCapacity, GdbFieldType.Int, 15));
        patchToy.Properties.Add(new GdbPatchProperty(HashParent, GdbFieldType.Reference, ParentArchetypeId));
        patchDb.Entries.Add(patchToy);

        byte[] patchBytes = GdbWriter.Serialize(patchDb);

        var multi = GdbMulti.Build(new byte[]?[] { baseBytes, patchBytes });

        AssertThat(multi.TryLookup(ToyGunId, out GdbCursor toyCursor)).IsTrue();

        // 1. Modded BulletCapacity should resolve to 15:
        AssertThat(multi.TryFindInherited(toyCursor, HashBulletCapacity, -1, out _, out uint cap, out _)).IsTrue();
        AssertThat(cap).IsEqual(15u);

        // 2. Unmodified Range should resolve from the parent in base GDB:
        AssertThat(multi.TryFindInherited(toyCursor, HashRange, -1, out _, out uint rangeRaw, out _)).IsTrue();
        AssertThat(rangeRaw).IsEqual(0x41400000u);

        // 3. InheritedFields should emit both BulletCapacity and Range:
        var fields = multi.InheritedFields(toyCursor).ToList();
        var capField = fields.Find(f => f.Hash == HashBulletCapacity);
        AssertThat(capField.Hash).IsEqual(HashBulletCapacity);
        AssertThat(capField.Raw).IsEqual(15u);

        var rangeField = fields.Find(f => f.Hash == HashRange);
        AssertThat(rangeField.Hash).IsEqual(HashRange);
        AssertThat(rangeField.Raw).IsEqual(0x41400000u);
    }

    [TestCase]
    public void TestNonExistentPropertyDoesNotStackOverflow()
    {
        var baseDb = new GdbPatchDatabase();
        var baseToy = new GdbPatchEntry(ToyGunId, 0x01);
        baseToy.Properties.Add(new GdbPatchProperty(HashBulletCapacity, GdbFieldType.Int, 1));
        baseToy.Properties.Add(new GdbPatchProperty(HashParent, GdbFieldType.Reference, ParentArchetypeId));
        baseDb.Entries.Add(baseToy);

        var patchDb = new GdbPatchDatabase();
        var patchToy = new GdbPatchEntry(ToyGunId, 0x01);
        patchToy.Properties.Add(new GdbPatchProperty(HashBulletCapacity, GdbFieldType.Int, 15));
        patchDb.Entries.Add(patchToy);

        var multi = GdbMulti.Build(new byte[]?[] { GdbWriter.Serialize(baseDb), GdbWriter.Serialize(patchDb) });
        AssertThat(multi.TryLookup(ToyGunId, out GdbCursor cursor)).IsTrue();

        // Querying non-existent property MUST return false without infinite recursion or stack overflow:
        bool found = multi.TryFindInherited(cursor, 0x99999999, -1, out _, out _, out _);
        AssertThat(found).IsFalse();
    }

    [TestCase]
    public void TestSparsePatchWithoutParentInheritsFromBaseRecord()
    {
        var baseDb = new GdbPatchDatabase();
        var parentEntry = new GdbPatchEntry(ParentArchetypeId, 0x01);
        parentEntry.Properties.Add(new GdbPatchProperty(HashRange, GdbFieldType.Float, 0x41400000));
        baseDb.Entries.Add(parentEntry);

        var baseToy = new GdbPatchEntry(ToyGunId, 0x01);
        baseToy.Properties.Add(new GdbPatchProperty(HashBulletCapacity, GdbFieldType.Int, 1));
        baseToy.Properties.Add(new GdbPatchProperty(HashParent, GdbFieldType.Reference, ParentArchetypeId));
        baseDb.Entries.Add(baseToy);

        // Patch contains ONLY BulletCapacity, NO parent property!
        var patchDb = new GdbPatchDatabase();
        var patchToy = new GdbPatchEntry(ToyGunId, 0x01);
        patchToy.Properties.Add(new GdbPatchProperty(HashBulletCapacity, GdbFieldType.Int, 99));
        patchDb.Entries.Add(patchToy);

        var multi = GdbMulti.Build(new byte[]?[] { GdbWriter.Serialize(baseDb), GdbWriter.Serialize(patchDb) });
        AssertThat(multi.TryLookup(ToyGunId, out GdbCursor cursor)).IsTrue();

        // BulletCapacity is 99 from patch:
        AssertThat(multi.TryFindInherited(cursor, HashBulletCapacity, -1, out _, out uint cap, out _)).IsTrue();
        AssertThat(cap).IsEqual(99u);

        // Range inherits from ParentArchetype via base record's parent:
        AssertThat(multi.TryFindInherited(cursor, HashRange, -1, out _, out uint range, out _)).IsTrue();
        AssertThat(range).IsEqual(0x41400000u);
    }
}
