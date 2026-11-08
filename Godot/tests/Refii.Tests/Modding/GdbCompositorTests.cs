namespace Refii.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using GdUnit4;
using static GdUnit4.Assertions;
using Godot;
using Refii.Gdb;
using Refii.Mods;

[TestSuite]
public class GdbCompositorTests
{
    private const uint ToyGunId = 0x2FDDF213;
    private const uint HashBulletCapacity = 0xAABED971;
    private const uint HashBasePrice = 0x44C44B51;
    private const uint HashParent = GdbView.HashParent;

    [TestCase]
    public void TestLoadOrderPriorityAndConflictReporting()
    {
        string tempDir = Path.Combine(Refii.Tests.TestTemp.Root, "refii_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            // Mod A (lower priority): BulletCapacity = 5
            string modAPath = Path.Combine(tempDir, "modA.gdb.tu");
            var dbA = new GdbPatchDatabase();
            var entryA = new GdbPatchEntry(ToyGunId, 0x01);
            entryA.Properties.Add(new GdbPatchProperty(HashBulletCapacity, GdbFieldType.Int, 5));
            dbA.Entries.Add(entryA);
            dbA.Names[HashBulletCapacity] = "BulletCapacity";
            File.WriteAllBytes(modAPath, GdbWriter.Serialize(dbA));

            // Mod B (higher priority): BulletCapacity = 10
            string modBPath = Path.Combine(tempDir, "modB.gdb.tu");
            var dbB = new GdbPatchDatabase();
            var entryB = new GdbPatchEntry(ToyGunId, 0x01);
            entryB.Properties.Add(new GdbPatchProperty(HashBulletCapacity, GdbFieldType.Int, 10));
            dbB.Entries.Add(entryB);
            dbB.Names[HashBulletCapacity] = "BulletCapacity";
            File.WriteAllBytes(modBPath, GdbWriter.Serialize(dbB));

            // Mount order: lowest priority first -> highest priority last
            var modFiles = new List<(string ModName, string DiskPath)>
            {
                ("ModA", modAPath),
                ("ModB", modBPath)
            };

            var (mergedDb, conflicts) = GdbPatchCompositor.MergeDatabases("globals/globals.gdb.tu", modFiles);

            // Assert that ModB's value won:
            AssertThat(mergedDb.Entries.Count).IsEqual(1);
            var mergedEntry = mergedDb.Entries[0];
            AssertThat(mergedEntry.Id).IsEqual(ToyGunId);

            var prop = mergedEntry.Properties.Find(p => p.NameHash == HashBulletCapacity);
            AssertThat(prop).IsNotNull();
            AssertThat(prop!.Value).IsEqual(10u);

            // Assert conflict was tracked:
            AssertThat(conflicts.Count).IsEqual(1);
            var conflict = conflicts[0];
            AssertThat(conflict.RecordHash).IsEqual(ToyGunId);
            AssertThat(conflict.PropertyHash).IsEqual(HashBulletCapacity);
            AssertThat(conflict.WinnerMod).IsEqual("ModB");
            AssertThat(conflict.LoserMods.Count).IsEqual(1);
            AssertThat(conflict.LoserMods[0]).IsEqual("ModA");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [TestCase]
    public void TestNonCollidingPropertyComposition()
    {
        string tempDir = Path.Combine(Refii.Tests.TestTemp.Root, "refii_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            // Mod A: Modifies BasePrice = 50
            string modAPath = Path.Combine(tempDir, "modA.gdb.tu");
            var dbA = new GdbPatchDatabase();
            var entryA = new GdbPatchEntry(ToyGunId, 0x01);
            entryA.Properties.Add(new GdbPatchProperty(HashBasePrice, GdbFieldType.Int, 50));
            dbA.Entries.Add(entryA);
            dbA.Names[HashBasePrice] = "BasePrice";
            File.WriteAllBytes(modAPath, GdbWriter.Serialize(dbA));

            // Mod B: Modifies BulletCapacity = 12
            string modBPath = Path.Combine(tempDir, "modB.gdb.tu");
            var dbB = new GdbPatchDatabase();
            var entryB = new GdbPatchEntry(ToyGunId, 0x01);
            entryB.Properties.Add(new GdbPatchProperty(HashBulletCapacity, GdbFieldType.Int, 12));
            dbB.Entries.Add(entryB);
            dbB.Names[HashBulletCapacity] = "BulletCapacity";
            File.WriteAllBytes(modBPath, GdbWriter.Serialize(dbB));

            var modFiles = new List<(string ModName, string DiskPath)>
            {
                ("ModA", modAPath),
                ("ModB", modBPath)
            };

            var (mergedDb, conflicts) = GdbPatchCompositor.MergeDatabases("globals/globals.gdb.tu", modFiles);

            // Assert that BOTH properties are composited on ToyGun:
            AssertThat(mergedDb.Entries.Count).IsEqual(1);
            var mergedEntry = mergedDb.Entries[0];
            AssertThat(mergedEntry.Id).IsEqual(ToyGunId);

            var priceProp = mergedEntry.Properties.Find(p => p.NameHash == HashBasePrice);
            AssertThat(priceProp).IsNotNull();
            AssertThat(priceProp!.Value).IsEqual(50u);

            var capProp = mergedEntry.Properties.Find(p => p.NameHash == HashBulletCapacity);
            AssertThat(capProp).IsNotNull();
            AssertThat(capProp!.Value).IsEqual(12u);

            // Zero conflicts since different properties were touched:
            AssertThat(conflicts.Count).IsEqual(0);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [TestCase]
    public void TestDisjointRecordsMerging()
    {
        string tempDir = Path.Combine(Refii.Tests.TestTemp.Root, "refii_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            uint recordOne = 0x11112222;
            uint recordTwo = 0x33334444;

            // Mod A touches Record One:
            string modAPath = Path.Combine(tempDir, "modA.gdb.tu");
            var dbA = new GdbPatchDatabase();
            var entryA = new GdbPatchEntry(recordOne, 0x01);
            entryA.Properties.Add(new GdbPatchProperty(0xAA11, GdbFieldType.Int, 100));
            dbA.Entries.Add(entryA);
            File.WriteAllBytes(modAPath, GdbWriter.Serialize(dbA));

            // Mod B touches Record Two:
            string modBPath = Path.Combine(tempDir, "modB.gdb.tu");
            var dbB = new GdbPatchDatabase();
            var entryB = new GdbPatchEntry(recordTwo, 0x01);
            entryB.Properties.Add(new GdbPatchProperty(0xBB22, GdbFieldType.Int, 200));
            dbB.Entries.Add(entryB);
            File.WriteAllBytes(modBPath, GdbWriter.Serialize(dbB));

            var modFiles = new List<(string ModName, string DiskPath)>
            {
                ("ModA", modAPath),
                ("ModB", modBPath)
            };

            var (mergedDb, conflicts) = GdbPatchCompositor.MergeDatabases("globals/globals.gdb.tu", modFiles);

            // Both records must exist in merged output:
            AssertThat(mergedDb.Entries.Count).IsEqual(2);
            AssertThat(mergedDb.Entries.Exists(e => e.Id == recordOne)).IsTrue();
            AssertThat(mergedDb.Entries.Exists(e => e.Id == recordTwo)).IsTrue();
            AssertThat(conflicts.Count).IsEqual(0);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [TestCase]
    public void TestPurgeCacheDeletesCompositeDirectory()
    {
        string tempDir = Path.Combine(Refii.Tests.TestTemp.Root, "refii_purge_test_" + Guid.NewGuid().ToString("N"));
        string cacheDir = Path.Combine(tempDir, ".cache", "gdb_composite");
        Directory.CreateDirectory(cacheDir);
        File.WriteAllText(Path.Combine(cacheDir, "test.gdb.tu"), "dummy");

        try
        {
            AssertThat(Directory.Exists(cacheDir)).IsTrue();
            GdbPatchCompositor.PurgeCache(tempDir);
            AssertThat(Directory.Exists(cacheDir)).IsFalse();
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [TestCase]
    public void TestApplyWithZeroOverlaysPurgesCompositeCache()
    {
        string tempDir = Path.Combine(Refii.Tests.TestTemp.Root, "refii_apply_test_" + Guid.NewGuid().ToString("N"));
        string cacheDir = Path.Combine(tempDir, ".cache", "gdb_composite");
        Directory.CreateDirectory(cacheDir);
        File.WriteAllText(Path.Combine(cacheDir, "test.gdb.tu"), "dummy");

        try
        {
            AssertThat(Directory.Exists(cacheDir)).IsTrue();
            using var bnk = new Refii.Bnk.BnkFileSystem();
            var results = GdbPatchCompositor.Apply(bnk, Array.Empty<Refii.Mods.ModOverlay>(), tempDir);
            AssertThat(results.Count).IsEqual(0);
            AssertThat(Directory.Exists(cacheDir)).IsFalse();
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [TestCase]
    public void TestModIndexTallyRecordsWonConflicts()
    {
        var entries = new List<ResolvedEntry>
        {
            new()
            {
                Path = "files/texture.dds",
                Owner = "ModB",
                Kind = ResolvedEntry.KindOverride,
                Shadowed = new List<string> { "ModA" }
            }
        };

        var gdbConflicts = new List<GdbRecordConflict>
        {
            new()
            {
                DatabaseSubPath = "globals/globals.gdb.tu",
                RecordHash = ToyGunId,
                RecordName = "ToyGun",
                PropertyHash = HashBulletCapacity,
                PropertyName = "BulletCapacity",
                WinnerMod = "ModB",
                LoserMods = { "ModA" }
            }
        };

        var rowConflicts = new List<(string Table, RowConflict Conflict)>
        {
            (
                "language/en-uk/text/book.babel",
                new RowConflict
                {
                    Key = "0x1000A001",
                    Winner = "ModB",
                    Losers = new List<string> { "ModA" }
                }
            )
        };

        Dictionary<string, ModTally> tallies = ModIndex.Tally(entries, gdbConflicts, rowConflicts);

        AssertThat(tallies.ContainsKey("ModA")).IsTrue();
        AssertThat(tallies.ContainsKey("ModB")).IsTrue();
        var tallyA = tallies["ModA"];
        var tallyB = tallies["ModB"];

        // ModA lost 3 things: 1 file overwrite, 1 GDB property, 1 Babel row
        AssertThat(tallyA.Lost).IsEqual(3);
        AssertThat(tallyA.ConflictDetails.Count).IsEqual(3);

        // ModB won those 3 things, and lost 0
        AssertThat(tallyB.Lost).IsEqual(0);
        AssertThat(tallyB.Conflicts).IsEqual(3);
        AssertThat(tallyB.WonConflictDetails.Count).IsEqual(3);

        // Verify detail strings
        AssertThat(tallyB.WonConflictDetails.Exists(d => d.Contains("globals/globals.gdb.tu"))).IsTrue();
        AssertThat(tallyB.WonConflictDetails.Exists(d => d.Contains("0x1000A001"))).IsTrue();
        AssertThat(tallyA.ConflictDetails.Exists(d => d.Contains("0x1000A001"))).IsTrue();
    }

    [TestCase]
    public void TestSynthesizeCompleteDatabasePropagatesBasePropertiesAndNames()
    {
        uint swordId = 0xA2AE58D0;
        // A property's hash is the FNV-1 of its name; the written container names it that way.
        uint hashWeaponType = Refii.Core.Engine_GameHash.Fnv1("WeaponType");

        var baseDb = new GdbPatchDatabase();
        var baseEntry = new GdbPatchEntry(swordId, 0x01);
        baseEntry.Properties.Add(new GdbPatchProperty(HashParent, GdbFieldType.Reference, 0x11111111));
        baseEntry.Properties.Add(new GdbPatchProperty(hashWeaponType, GdbFieldType.Int, 1));
        baseEntry.Properties.Add(new GdbPatchProperty(HashBasePrice, GdbFieldType.Int, 20));
        baseDb.Entries.Add(baseEntry);
        baseDb.Names[swordId] = "WoodenSword";
        baseDb.Names[HashParent] = "parent";
        baseDb.Names[hashWeaponType] = "WeaponType";
        baseDb.Names[HashBasePrice] = "BasePrice";

        byte[] baseBytes = GdbWriter.Serialize(baseDb);
        GdbMulti baseMulti = GdbMulti.Build(new[] { baseBytes });

        // Sparse patch modifying only BasePrice to 120
        var sparseDb = new GdbPatchDatabase();
        var sparseEntry = new GdbPatchEntry(swordId, 0x01);
        sparseEntry.Properties.Add(new GdbPatchProperty(HashBasePrice, GdbFieldType.Int, 120));
        sparseDb.Entries.Add(sparseEntry);
        sparseDb.Names[HashBasePrice] = "BasePrice";

        GdbPatchDatabase fullDb = GdbPatchBuilder.SynthesizeCompleteDatabase(sparseDb, baseMulti);

        AssertThat(fullDb.Entries.Count).IsEqual(1);
        var entry = fullDb.Entries[0];
        AssertThat(entry.Id).IsEqual(swordId);
        AssertThat(entry.Properties.Count).IsEqual(3);

        var priceProp = entry.Properties.Find(p => p.NameHash == HashBasePrice);
        AssertThat(priceProp).IsNotNull();
        AssertThat(priceProp!.Value).IsEqual(120u);

        var parentProp = entry.Properties.Find(p => p.NameHash == HashParent);
        AssertThat(parentProp).IsNotNull();
        AssertThat(parentProp!.Value).IsEqual(0x11111111u);

        var weaponTypeProp = entry.Properties.Find(p => p.NameHash == hashWeaponType);
        AssertThat(weaponTypeProp).IsNotNull();
        AssertThat(weaponTypeProp!.Value).IsEqual(1u);

        // Verify names propagated
        AssertThat(fullDb.Names.ContainsKey(swordId)).IsTrue();
        AssertThat(fullDb.Names[swordId]).IsEqual("WoodenSword");
        AssertThat(fullDb.Names.ContainsKey(HashParent)).IsTrue();
        AssertThat(fullDb.Names[HashParent]).IsEqual("parent");
        AssertThat(fullDb.Names.ContainsKey(hashWeaponType)).IsTrue();
        AssertThat(fullDb.Names[hashWeaponType]).IsEqual("WeaponType");
    }
}
