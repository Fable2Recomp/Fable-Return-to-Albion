namespace Refii.Tests.Modding;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Gdb;
using Refii.Mods;

[TestSuite]
public class ModContentScannerTests
{
    private static byte[] BuildSyntheticTuBytes(uint recordA, uint recordB)
    {
        var db = new GdbPatchDatabase();
        var entry1 = new GdbPatchEntry(recordA, 0);
        entry1.Properties.Add(new GdbPatchProperty(0x11111111, GdbFieldType.Int, 42));
        db.Entries.Add(entry1);

        var entry2 = new GdbPatchEntry(recordB, 0);
        entry2.Properties.Add(new GdbPatchProperty(0x22222222, GdbFieldType.Float, BitConverter.SingleToUInt32Bits(1.5f)));
        db.Entries.Add(entry2);

        return GdbWriter.Serialize(db);
    }

    [TestCase]
    public void TestScanEmptyOrMissingModReturnsEmptySummary()
    {
        string tempDir = Path.Combine(Refii.Tests.TestTemp.Root, "refii_scanner_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            ModContentSummary summary = ModContentScanner.ScanMod(tempDir, "NonExistentMod");
            AssertThat(summary.ModName).IsEqual("NonExistentMod");
            AssertThat(summary.PatchedRecordCount).IsEqual(0);
            AssertThat(summary.LocalizedStringCount).IsEqual(0);
            AssertThat(summary.ScriptCount).IsEqual(0);
            AssertThat(summary.Entries.Count).IsEqual(0);
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
    public void TestScanModWithTitleUpdatesAndBabel()
    {
        string tempDir = Path.Combine(Refii.Tests.TestTemp.Root, "refii_scanner_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string packagePath = Path.Combine(tempDir, "DragonMod.f2mod");

        try
        {
            var meta = new ModMetadata
            {
                Title = "Dragon Mod",
                Version = "1.0.0",
                Author = "Tester"
            };

            uint rec1 = 0x12345678;
            uint rec2 = 0x87654321;
            byte[] tuBytes = BuildSyntheticTuBytes(rec1, rec2);
            byte[] babelBytes = Encoding.UTF8.GetBytes("# Refii Babel\n0x1001,Dragon Sword\n0x1002,A blazing weapon\n");
            byte[] scriptBytes = Encoding.UTF8.GetBytes("print('Hello from DragonMod')");

            var files = new List<(string Path, byte[] Bytes)>
            {
                ("globals/globals.gdb.tu", tuBytes),
                ("language/en-uk/text/book.babel.csv", babelBytes),
                ("scripts/dragon_quest.lua", scriptBytes)
            };

            bool created = F2ModPackage.Create(packagePath, meta, files, out _);
            AssertThat(created).IsTrue();

            ModContentSummary summary = ModContentScanner.ScanMod(tempDir, "DragonMod");
            AssertThat(summary.ModName).IsEqual("DragonMod");
            AssertThat(summary.PatchedRecordCount).IsEqual(2);
            AssertThat(summary.LocalizedStringCount).IsEqual(2);
            AssertThat(summary.ScriptCount).IsEqual(1);
            AssertThat(summary.Entries.Count).IsEqual(5);

            // Verify specific entry detection:
            bool hasRec1 = summary.Entries.Exists(e => e.RecordHash == rec1);
            bool hasRec2 = summary.Entries.Exists(e => e.RecordHash == rec2);
            bool hasBabel = summary.Entries.Exists(e => e.PathOrKey == "0x1001" && e.Label == "Dragon Sword");
            bool hasScript = summary.Entries.Exists(e => e.Kind == ModAuthoredKind.Script && e.Label == "dragon_quest.lua");

            AssertThat(hasRec1).IsTrue();
            AssertThat(hasRec2).IsTrue();
            AssertThat(hasBabel).IsTrue();
            AssertThat(hasScript).IsTrue();
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
    public void TestScanModStrictIsolationIgnoresOtherMods()
    {
        string tempDir = Path.Combine(Refii.Tests.TestTemp.Root, "refii_scanner_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            // Mod A: 1 record
            byte[] tuA = BuildSyntheticTuBytes(0xAAAAAAAA, 0xBBBBBBBB);
            F2ModPackage.Create(Path.Combine(tempDir, "ModA.f2mod"), new ModMetadata { Title = "Mod A" },
                new List<(string, byte[])> { ("globals/globals.gdb.tu", tuA) }, out _);

            // Mod B: 1 record
            byte[] tuB = BuildSyntheticTuBytes(0xCCCCCCCC, 0xDDDDDDDD);
            F2ModPackage.Create(Path.Combine(tempDir, "ModB.f2mod"), new ModMetadata { Title = "Mod B" },
                new List<(string, byte[])> { ("globals/globals.gdb.tu", tuB) }, out _);

            // Scanning Mod A should strictly report Mod A records, NEVER Mod B:
            ModContentSummary summaryA = ModContentScanner.ScanMod(tempDir, "ModA");
            AssertThat(summaryA.PatchedRecordCount).IsEqual(2);
            AssertThat(summaryA.Entries.Exists(e => e.RecordHash == 0xAAAAAAAA)).IsTrue();
            AssertThat(summaryA.Entries.Exists(e => e.RecordHash == 0xCCCCCCCC)).IsFalse();

            // Scanning Mod B should strictly report Mod B records:
            ModContentSummary summaryB = ModContentScanner.ScanMod(tempDir, "ModB");
            AssertThat(summaryB.PatchedRecordCount).IsEqual(2);
            AssertThat(summaryB.Entries.Exists(e => e.RecordHash == 0xCCCCCCCC)).IsTrue();
            AssertThat(summaryB.Entries.Exists(e => e.RecordHash == 0xAAAAAAAA)).IsFalse();
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
    public void TestRevertGdbRecordRemovesSingleRecord()
    {
        string tempDir = Path.Combine(Refii.Tests.TestTemp.Root, "refii_scanner_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string packagePath = Path.Combine(tempDir, "RevertMod.f2mod");

        try
        {
            uint keepHash = 0x11112222;
            uint removeHash = 0x33334444;
            byte[] tuBytes = BuildSyntheticTuBytes(keepHash, removeHash);

            F2ModPackage.Create(packagePath, new ModMetadata { Title = "Revert Test" },
                new List<(string, byte[])> { ("globals/globals.gdb.tu", tuBytes) }, out _);

            // Revert removeHash:
            bool ok = ModContentScanner.RevertGdbRecord(tempDir, "RevertMod", "globals/globals.gdb.tu", removeHash, out string error);
            AssertThat(ok).IsTrue();
            AssertThat(error).IsEqual(string.Empty);

            // Rescan:
            ModContentSummary rescanned = ModContentScanner.ScanMod(tempDir, "RevertMod");
            AssertThat(rescanned.PatchedRecordCount).IsEqual(1);
            AssertThat(rescanned.Entries.Exists(e => e.RecordHash == keepHash)).IsTrue();
            AssertThat(rescanned.Entries.Exists(e => e.RecordHash == removeHash)).IsFalse();
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
    public void TestRevertBabelStringRemovesSingleKey()
    {
        string tempDir = Path.Combine(Refii.Tests.TestTemp.Root, "refii_scanner_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string packagePath = Path.Combine(tempDir, "BabelRevertMod.f2mod");

        try
        {
            byte[] babelBytes = Encoding.UTF8.GetBytes("KEY_KEEP,Keep Text\nKEY_REMOVE,Remove Text\n");
            F2ModPackage.Create(packagePath, new ModMetadata { Title = "Babel Revert" },
                new List<(string, byte[])> { ("language/en-uk/text/book.babel.csv", babelBytes) }, out _);

            bool ok = ModContentScanner.RevertBabelString(tempDir, "BabelRevertMod", "KEY_REMOVE", out string error);
            AssertThat(ok).IsTrue();
            AssertThat(error).IsEqual(string.Empty);

            ModContentSummary rescanned = ModContentScanner.ScanMod(tempDir, "BabelRevertMod");
            AssertThat(rescanned.LocalizedStringCount).IsEqual(1);
            AssertThat(rescanned.Entries.Exists(e => e.PathOrKey == "KEY_KEEP")).IsTrue();
            AssertThat(rescanned.Entries.Exists(e => e.PathOrKey == "KEY_REMOVE")).IsFalse();
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
    public void TestScanModFriendlyRecordNameAndValueSummary()
    {
        string tempDir = Path.Combine(Refii.Tests.TestTemp.Root, "refii_scanner_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string packagePath = Path.Combine(tempDir, "FriendlyTestMod.f2mod");

        try
        {
            var db = new GdbPatchDatabase();
            uint swordRecord = 0x61C68648;
            uint basePriceProp = Refii.Core.Engine_GameHash.Fnv1("BasePrice");

            var entry = new GdbPatchEntry(swordRecord, 0);
            entry.Properties.Add(new GdbPatchProperty(basePriceProp, GdbFieldType.Int, 5000));
            db.Entries.Add(entry);

            // Authored friendly name and property name:
            db.Names[swordRecord] = "Hal's Sword";
            db.Names[basePriceProp] = "BasePrice";

            byte[] tuBytes = GdbWriter.Serialize(db);
            F2ModPackage.Create(packagePath, new ModMetadata { Title = "Friendly Test" },
                new List<(string, byte[])> { ("globals/globals.gdb.tu", tuBytes) }, out _);

            ModContentSummary summary = ModContentScanner.ScanMod(tempDir, "FriendlyTestMod");
            AssertThat(summary.PatchedRecordCount).IsEqual(1);

            ModAuthoredEntry? authoredEntry = summary.Entries.Find(e => e.RecordHash == swordRecord);
            AssertThat(authoredEntry).IsNotNull();
            AssertThat(authoredEntry!.Label).IsEqual("Hal's Sword");
            AssertThat(authoredEntry.Details).Contains("Base Price: 5000");
            AssertThat(authoredEntry.Subtitle).Contains("globals.gdb.tu");
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
    public void TestConflictEvaluationOrder1WinsOverOrder2()
    {
        string tempDir = Path.Combine(Refii.Tests.TestTemp.Root, "refii_conflict_order_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            uint recordHash = 0x61C68648;
            uint basePriceProp = Refii.Core.Engine_GameHash.Fnv1("BasePrice");

            // Mod1: Order 1, sets BasePrice = 1000
            var db1 = new GdbPatchDatabase();
            var entry1 = new GdbPatchEntry(recordHash, 0);
            entry1.Properties.Add(new GdbPatchProperty(basePriceProp, GdbFieldType.Int, 1000));
            db1.Entries.Add(entry1);
            db1.Names[recordHash] = "Hal's Sword";
            byte[] tu1 = GdbWriter.Serialize(db1);
            F2ModPackage.Create(Path.Combine(tempDir, "Mod1.f2mod"), new ModMetadata { Title = "Mod 1" },
                new List<(string, byte[])> { ("globals/globals.gdb.tu", tu1) }, out _);

            // Mod2: Order 2, sets BasePrice = 2000
            var db2 = new GdbPatchDatabase();
            var entry2 = new GdbPatchEntry(recordHash, 0);
            entry2.Properties.Add(new GdbPatchProperty(basePriceProp, GdbFieldType.Int, 2000));
            db2.Entries.Add(entry2);
            db2.Names[recordHash] = "Hal's Sword";
            byte[] tu2 = GdbWriter.Serialize(db2);
            F2ModPackage.Create(Path.Combine(tempDir, "Mod2.f2mod"), new ModMetadata { Title = "Mod 2" },
                new List<(string, byte[])> { ("globals/globals.gdb.tu", tu2) }, out _);

            // Set load order: Mod1 is Order 1 (index 0), Mod2 is Order 2 (index 1)
            var order = new ModLoadOrder();
            order.RegisterMod("Mod1", enabled: true);
            order.RegisterMod("Mod2", enabled: true);
            order.Write(tempDir, out _);

            // 1. Scan Mod2 (Order 2): should detect that it is overridden by Mod1 higher in load order!
            ModContentSummary summary2 = ModContentScanner.ScanMod(tempDir, "Mod2");
            AssertThat(summary2.HasConflicts).IsTrue();
            AssertThat(summary2.ConflictStatusText).Contains("overridden by 'Mod1'");
            var item2 = summary2.Entries.Find(e => e.RecordHash == recordHash);
            AssertThat(item2).IsNotNull();
            AssertThat(item2!.Details).Contains("Overridden by 'Mod1'");

            // 2. Scan Mod1 (Order 1): should win, reporting priority over Mod2!
            ModContentSummary summary1 = ModContentScanner.ScanMod(tempDir, "Mod1");
            AssertThat(summary1.HasConflicts).IsFalse();
            AssertThat(summary1.ConflictStatusText).Contains("Priority Mod");
            AssertThat(summary1.ConflictStatusText).Contains("overrides 'Mod2'");
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
    public void TestConflictEvaluationMultiModAttributionAndBanner()
    {
        string tempDir = Path.Combine(Refii.Tests.TestTemp.Root, "refii_multimod_conflict_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            uint record1 = 0x11112222;
            uint record2 = 0x33334444;
            uint propPrice = Refii.Core.Engine_GameHash.Fnv1("BasePrice");

            // ModTop (Order 1): Overrides record1 only
            var dbTop = new GdbPatchDatabase();
            var entryTop = new GdbPatchEntry(record1, 0);
            entryTop.Properties.Add(new GdbPatchProperty(propPrice, GdbFieldType.Int, 100));
            dbTop.Entries.Add(entryTop);
            dbTop.Names[record1] = "Top Record";
            F2ModPackage.Create(Path.Combine(tempDir, "ModTop.f2mod"), new ModMetadata { Title = "Mod Top" },
                new List<(string, byte[])> { ("globals/globals.gdb.tu", GdbWriter.Serialize(dbTop)) }, out _);

            // ModMid (Order 2): Overrides record2 only
            var dbMid = new GdbPatchDatabase();
            var entryMid = new GdbPatchEntry(record2, 0);
            entryMid.Properties.Add(new GdbPatchProperty(propPrice, GdbFieldType.Int, 200));
            dbMid.Entries.Add(entryMid);
            dbMid.Names[record2] = "Mid Record";
            F2ModPackage.Create(Path.Combine(tempDir, "ModMid.f2mod"), new ModMetadata { Title = "Mod Mid" },
                new List<(string, byte[])> { ("globals/globals.gdb.tu", GdbWriter.Serialize(dbMid)) }, out _);

            // ModLow (Order 3): Contains both record1 and record2
            var dbLow = new GdbPatchDatabase();
            var entryLow1 = new GdbPatchEntry(record1, 0);
            entryLow1.Properties.Add(new GdbPatchProperty(propPrice, GdbFieldType.Int, 300));
            dbLow.Entries.Add(entryLow1);
            dbLow.Names[record1] = "Low Record 1";

            var entryLow2 = new GdbPatchEntry(record2, 0);
            entryLow2.Properties.Add(new GdbPatchProperty(propPrice, GdbFieldType.Int, 400));
            dbLow.Entries.Add(entryLow2);
            dbLow.Names[record2] = "Low Record 2";
            F2ModPackage.Create(Path.Combine(tempDir, "ModLow.f2mod"), new ModMetadata { Title = "Mod Low" },
                new List<(string, byte[])> { ("globals/globals.gdb.tu", GdbWriter.Serialize(dbLow)) }, out _);

            // Configure load order: ModTop (1), ModMid (2), ModLow (3)
            var order = new ModLoadOrder();
            order.RegisterMod("ModTop", enabled: true);
            order.RegisterMod("ModMid", enabled: true);
            order.RegisterMod("ModLow", enabled: true);
            order.Write(tempDir, out _);

            // 1. Scan ModLow:
            ModContentSummary lowSummary = ModContentScanner.ScanMod(tempDir, "ModLow");
            AssertThat(lowSummary.HasConflicts).IsTrue();
            // Banner must report 2 collisions and list both higher mods:
            AssertThat(lowSummary.ConflictStatusText).IsEqual("⚠ 2 collisions overridden by 2 higher-priority mods ('ModTop', 'ModMid')");

            // Item 1 must be accurately attributed to ModTop:
            ModAuthoredEntry? item1 = lowSummary.Entries.Find(e => e.RecordHash == record1);
            AssertThat(item1).IsNotNull();
            AssertThat(item1!.Details).Contains("Overridden by 'ModTop'");

            // Item 2 must be accurately attributed to ModMid (NOT ModTop!):
            ModAuthoredEntry? item2 = lowSummary.Entries.Find(e => e.RecordHash == record2);
            AssertThat(item2).IsNotNull();
            AssertThat(item2!.Details).Contains("Overridden by 'ModMid'");
            AssertThat(item2.Details).NotContains("ModTop");

            // 2. Scan ModMid: Won record2 over ModLow, 0 losses
            ModContentSummary midSummary = ModContentScanner.ScanMod(tempDir, "ModMid");
            AssertThat(midSummary.HasConflicts).IsFalse();
            AssertThat(midSummary.ConflictStatusText).Contains("Priority Mod");
            AssertThat(midSummary.ConflictStatusText).Contains("overrides 'ModLow'");
            ModAuthoredEntry? midItem2 = midSummary.Entries.Find(e => e.RecordHash == record2);
            AssertThat(midItem2).IsNotNull();
            AssertThat(midItem2!.Details).NotContains("Overridden by");
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
    public void TestScanMod_DiscreteSaveTuAdditionsAndRemovals()
    {
        string tempDir = Path.Combine(Refii.Tests.TestTemp.Root, "refii_scanner_savetu_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string packagePath = Path.Combine(tempDir, "LevelMod.f2mod");

        try
        {
            var meta = new ModMetadata
            {
                Title = "Level Mod",
                Version = "1.0.0",
                Author = "Tester"
            };

            var savePatch = new SaveMapPatch();
            savePatch.RemoveEntity(0x20000010);
            savePatch.UpsertEntity("DefaultLayer", 0x90001234, "NewPlacedBench", "id_5A4CBED4");
            savePatch.GetOrCreateLayer("QuestIntro_Layer", "id_11223344").LoadOverride = GdbSaveLoadState.AlwaysOn;

            byte[] saveTuBytes = savePatch.SerializeToUtf8();

            var files = new List<(string Path, byte[] Bytes)>
            {
                ("levels/albion/bowerstonemarket.save.tu", saveTuBytes)
            };

            bool created = F2ModPackage.Create(packagePath, meta, files, out _);
            AssertThat(created).IsTrue();

            ModContentSummary summary = ModContentScanner.ScanMod(tempDir, "LevelMod");
            AssertThat(summary.ModName).IsEqual("LevelMod");

            // We expect:
            // 1 aggregate .save.tu entry
            // 1 discrete removed entry (0x20000010)
            // 1 discrete placement entry (0x90001234)
            // 1 layer state override entry (QuestIntro_Layer_LoadState)
            AssertThat(summary.Entries.Count).IsEqual(4);

            ModAuthoredEntry? remEntry = summary.Entries.Find(e => e.RecordHash == 0x20000010);
            AssertThat(remEntry).IsNotNull();
            AssertThat(remEntry!.Label).Contains("Removed");
            AssertThat(remEntry.Details).Contains("removed");

            ModAuthoredEntry? addEntry = summary.Entries.Find(e => e.RecordHash == 0x90001234);
            AssertThat(addEntry).IsNotNull();
            AssertThat(addEntry!.Label).Contains("Placement");
            AssertThat(addEntry.Details).Contains("Placed in layer 'DefaultLayer'");
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



