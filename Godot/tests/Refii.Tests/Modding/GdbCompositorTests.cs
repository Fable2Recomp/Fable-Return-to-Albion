namespace Refii.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using GdUnit4;
using static GdUnit4.Assertions;
using Godot;
using Refii.Gdb;

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
        string tempDir = Path.Combine(Path.GetTempPath(), "refii_test_" + Guid.NewGuid().ToString("N"));
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
        string tempDir = Path.Combine(Path.GetTempPath(), "refii_test_" + Guid.NewGuid().ToString("N"));
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
        string tempDir = Path.Combine(Path.GetTempPath(), "refii_test_" + Guid.NewGuid().ToString("N"));
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
}
