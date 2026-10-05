namespace Refii.Tests;

using System;
using System.IO;
using GdUnit4;
using static GdUnit4.Assertions;
using Godot;
using Refii.Gdb;

[TestSuite]
public class GdbLeafPromotionTests
{
    private const uint ToyGunId = 0x2FDDF213;
    private const uint StandardFlintlockId = 0x6F548E0E;
    private const uint ParentArchetypeId = 0xEF0BF7F4;

    private const uint HashParent = GdbView.HashParent;
    private const uint HashBulletCapacity = 0xAABED971;
    private const uint HashRange = 0xF21E235C;
    private const uint HashReloadOnlyIfEmpty = 0x30AB25EC;

    [TestCase]
    public void TestSparseDeltaOmission()
    {
        // Setup a sparse mod patch where only BulletCapacity was authored:
        var sparseDb = new GdbPatchDatabase();
        var leafEntry = new GdbPatchEntry(ToyGunId, 0x01);
        leafEntry.Properties.Add(new GdbPatchProperty(HashBulletCapacity, GdbFieldType.Int, 10));
        leafEntry.Properties.Add(new GdbPatchProperty(HashParent, GdbFieldType.Reference, ParentArchetypeId));
        sparseDb.Entries.Add(leafEntry);

        sparseDb.Names[HashBulletCapacity] = "BulletCapacity";

        // Assert that the sparse patch database contains ONLY BulletCapacity and Parent
        // and does NOT contain Range, TargetAssistRange, or other baseline properties:
        AssertThat(leafEntry.Properties.Count).IsEqual(2);
        AssertThat(leafEntry.Properties.Exists(p => p.NameHash == HashRange)).IsFalse();

        // Round-trip serialize sparse patch
        byte[] sparseBytes = GdbWriter.Serialize(sparseDb);
        bool parsed = GdbView.TryParse(sparseBytes, out GdbView sparseView, out _);
        AssertThat(parsed).IsTrue();
        AssertThat(sparseView.TryLookup(ToyGunId, out int recIdx)).IsTrue();

        var fields = sparseView.Fields(recIdx);
        AssertThat(fields.Count).IsEqual(2);
        AssertThat(fields.Exists(f => f.Hash == HashBulletCapacity)).IsTrue();
        AssertThat(fields.Exists(f => f.Hash == HashRange)).IsFalse();
    }

    [TestCase]
    public void TestSiblingIsolation()
    {
        // Create base database containing Parent Archetype, Toy Gun, and Standard Flintlock:
        var baseDb = new GdbPatchDatabase();

        // Parent archetype:
        var parentEntry = new GdbPatchEntry(ParentArchetypeId, 0x01);
        parentEntry.Properties.Add(new GdbPatchProperty(HashRange, GdbFieldType.Float, 0x41400000)); // 12.0f
        parentEntry.Properties.Add(new GdbPatchProperty(HashBulletCapacity, GdbFieldType.Int, 1));
        parentEntry.Properties.Add(new GdbPatchProperty(HashReloadOnlyIfEmpty, GdbFieldType.Bool, 1));
        baseDb.Entries.Add(parentEntry);

        // Toy Gun leaf:
        var toyGunEntry = new GdbPatchEntry(ToyGunId, 0x01);
        toyGunEntry.Properties.Add(new GdbPatchProperty(HashParent, GdbFieldType.Reference, ParentArchetypeId));
        baseDb.Entries.Add(toyGunEntry);

        // Standard Flintlock sibling leaf:
        var flintlockEntry = new GdbPatchEntry(StandardFlintlockId, 0x01);
        flintlockEntry.Properties.Add(new GdbPatchProperty(HashParent, GdbFieldType.Reference, ParentArchetypeId));
        baseDb.Entries.Add(flintlockEntry);

        byte[] baseBytes = GdbWriter.Serialize(baseDb);
        GdbMulti baseMulti = GdbMulti.Build(new byte[]?[] { baseBytes });

        // Create a sparse patch that modifies ToyGun BulletCapacity to 10:
        var modPatch = new GdbPatchDatabase();
        var modToyGun = new GdbPatchEntry(ToyGunId, 0x01);
        modToyGun.Properties.Add(new GdbPatchProperty(HashBulletCapacity, GdbFieldType.Int, 10));
        modToyGun.Properties.Add(new GdbPatchProperty(HashParent, GdbFieldType.Reference, ParentArchetypeId));
        modPatch.Entries.Add(modToyGun);

        // Synthesize complete database for runtime:
        GdbPatchDatabase completeDb = GdbPatchBuilder.SynthesizeCompleteDatabase(modPatch, baseMulti);

        // Verify Toy Gun was synthesized with modified BulletCapacity:
        var completeToyGun = completeDb.Entries.Find(e => e.Id == ToyGunId);
        AssertThat(completeToyGun).IsNotNull();
        var toyCap = completeToyGun!.Properties.Find(p => p.NameHash == HashBulletCapacity);
        AssertThat(toyCap).IsNotNull();
        AssertThat(toyCap!.Value).IsEqual(10u);

        // Verify Flintlock was NOT touched in the patch or complete database:
        var completeFlintlock = completeDb.Entries.Find(e => e.Id == StandardFlintlockId);
        AssertThat(completeFlintlock).IsNull(); // Flintlock was not in modPatch, so not in synthesized patch

        // In the baseMulti view, Flintlock still resolves capacity as 1:
        AssertThat(baseMulti.TryLookup(StandardFlintlockId, out GdbCursor flintCursor)).IsTrue();
        AssertThat(baseMulti.TryFindInherited(flintCursor, HashBulletCapacity, -1, out _, out uint flintCap, out _)).IsTrue();
        AssertThat(flintCap).IsEqual(1u);
    }

    [TestCase]
    public void TestCompleteNativeSynthesisInheritsParentFields()
    {
        // Build base database with a parent holding Range and BulletCapacity:
        var baseDb = new GdbPatchDatabase();
        var parentEntry = new GdbPatchEntry(ParentArchetypeId, 0x01);
        parentEntry.Properties.Add(new GdbPatchProperty(HashRange, GdbFieldType.Float, 0x41400000)); // 12.0f
        parentEntry.Properties.Add(new GdbPatchProperty(HashBulletCapacity, GdbFieldType.Int, 1));
        baseDb.Entries.Add(parentEntry);

        var toyGunEntry = new GdbPatchEntry(ToyGunId, 0x01);
        toyGunEntry.Properties.Add(new GdbPatchProperty(HashParent, GdbFieldType.Reference, ParentArchetypeId));
        baseDb.Entries.Add(toyGunEntry);

        byte[] baseBytes = GdbWriter.Serialize(baseDb);
        GdbMulti baseMulti = GdbMulti.Build(new byte[]?[] { baseBytes });

        // Build sparse patch:
        var sparsePatch = new GdbPatchDatabase();
        var modEntry = new GdbPatchEntry(ToyGunId, 0x01);
        modEntry.Properties.Add(new GdbPatchProperty(HashBulletCapacity, GdbFieldType.Int, 25));
        modEntry.Properties.Add(new GdbPatchProperty(HashParent, GdbFieldType.Reference, ParentArchetypeId));
        sparsePatch.Entries.Add(modEntry);

        // Synthesize complete native database:
        GdbPatchDatabase completeDb = GdbPatchBuilder.SynthesizeCompleteDatabase(sparsePatch, baseMulti);
        var synthesizedEntry = completeDb.Entries.Find(e => e.Id == ToyGunId);
        AssertThat(synthesizedEntry).IsNotNull();

        // The synthesized entry MUST have:
        // 1. Modified BulletCapacity = 25
        // 2. Inherited Range = 12.0f (0x41400000)
        // 3. Parent reference = ParentArchetypeId
        var capProp = synthesizedEntry!.Properties.Find(p => p.NameHash == HashBulletCapacity);
        AssertThat(capProp).IsNotNull();
        AssertThat(capProp!.Value).IsEqual(25u);

        var rangeProp = synthesizedEntry.Properties.Find(p => p.NameHash == HashRange);
        AssertThat(rangeProp).IsNotNull();
        AssertThat(rangeProp!.Value).IsEqual(0x41400000u);

        var parentProp = synthesizedEntry.Properties.Find(p => p.NameHash == HashParent);
        AssertThat(parentProp).IsNotNull();
        AssertThat(parentProp!.Value).IsEqual(ParentArchetypeId);
    }
}
