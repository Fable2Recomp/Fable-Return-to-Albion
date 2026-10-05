namespace Refii.Tests.Modding;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Mods;
using Refii.Gdb;

[TestSuite]
public class F2ModPackageTests
{
    [TestCase]
    public void TestCreatePackageAndReadEntries()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "refii_f2mod_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string packagePath = Path.Combine(tempDir, "TestMod.f2mod");

        try
        {
            var meta = new ModMetadata
            {
                Title = "Test Mod Title",
                Version = "1.2.3",
                Author = "RefiiDev",
                Description = "A test package for Fable 2",
            };

            byte[] tuData = Encoding.UTF8.GetBytes("gdb_tu_payload_binary_bytes");
            byte[] babelData = Encoding.UTF8.GetBytes("key,text\n12345,Cool Sword");

            var files = new List<(string Path, byte[] Bytes)>
            {
                ("globals/globals.gdb.tu", tuData),
                ("language/en-uk/text/book.babel.csv", babelData)
            };

            bool created = F2ModPackage.Create(packagePath, meta, files, out string error);
            AssertThat(created).IsTrue();
            AssertThat(error).IsEqual(string.Empty);
            AssertThat(File.Exists(packagePath)).IsTrue();

            // Test TryReadMetadata without loading entries:
            bool metaOk = F2ModPackage.TryReadMetadata(packagePath, out ModMetadata readMeta, out string metaErr);
            AssertThat(metaOk).IsTrue();
            AssertThat(readMeta.Title).IsEqual("Test Mod Title");
            AssertThat(readMeta.Version).IsEqual("1.2.3");
            AssertThat(readMeta.Author).IsEqual("RefiiDev");

            // Test TryOpen and reading entries directly via offset/length:
            bool openOk = F2ModPackage.TryOpen(packagePath, out F2ModPackage? pkg, out string openErr);
            AssertThat(openOk).IsTrue();
            AssertThat(pkg).IsNotNull();

            using (pkg)
            {
                AssertThat(pkg!.Entries.Count).IsEqual(2);

                bool foundTu = pkg.TryGetEntry("globals/globals.gdb.tu", out F2ModEntry? tuEntry);
                AssertThat(foundTu).IsTrue();
                AssertThat(tuEntry).IsNotNull();
                byte[] readTuBytes = pkg.ReadEntryBytes(tuEntry!);
                AssertThat(readTuBytes).IsEqual(tuData);

                bool foundBabel = pkg.TryGetEntry("language/en-uk/text/book.babel.csv", out F2ModEntry? babelEntry);
                AssertThat(foundBabel).IsTrue();
                AssertThat(babelEntry).IsNotNull();
                byte[] readBabelBytes = pkg.ReadEntryBytes(babelEntry!);
                AssertThat(readBabelBytes).IsEqual(babelData);
            }
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
    public void TestIncrementalSyncAddNewData()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "refii_f2mod_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string packagePath = Path.Combine(tempDir, "IncrementalAdd.f2mod");

        try
        {
            var meta = new ModMetadata { Title = "IncrementalAdd", Version = "1.0.0" };
            byte[] file1Data = Encoding.UTF8.GetBytes("file1_initial_content");
            F2ModPackage.Create(packagePath, meta, new[] { ("file1.txt", file1Data) }, out _);

            // Incrementally add file2.txt:
            byte[] file2Data = Encoding.UTF8.GetBytes("file2_new_content_bytes");
            bool syncOk = F2ModPackage.IncrementalSync(
                packagePath,
                new[] { ("file2.txt", file2Data) });
            AssertThat(syncOk).IsTrue();

            // Verify both files are present and match:
            AssertThat(F2ModPackage.TryOpen(packagePath, out F2ModPackage? pkg, out _)).IsTrue();
            using (pkg)
            {
                AssertThat(pkg!.Entries.Count).IsEqual(2);
                AssertThat(pkg.TryGetEntry("file1.txt", out var e1)).IsTrue();
                AssertThat(pkg.ReadEntryBytes(e1!)).IsEqual(file1Data);

                AssertThat(pkg.TryGetEntry("file2.txt", out var e2)).IsTrue();
                AssertThat(pkg.ReadEntryBytes(e2!)).IsEqual(file2Data);
            }
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
    public void TestIncrementalSyncEditExistingData()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "refii_f2mod_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string packagePath = Path.Combine(tempDir, "IncrementalEdit.f2mod");

        try
        {
            var meta = new ModMetadata { Title = "IncrementalEdit", Version = "1.0.0" };
            byte[] originalTu = Encoding.UTF8.GetBytes("tu_version_1");
            F2ModPackage.Create(packagePath, meta, new[] { ("globals.gdb.tu", originalTu) }, out _);

            // Edit globals.gdb.tu to version 2:
            byte[] updatedTu = Encoding.UTF8.GetBytes("tu_version_2_updated_with_new_weapon_stats_extra_long");
            bool syncOk = F2ModPackage.IncrementalSync(
                packagePath,
                new[] { ("globals.gdb.tu", updatedTu) });
            AssertThat(syncOk).IsTrue();

            AssertThat(F2ModPackage.TryOpen(packagePath, out F2ModPackage? pkg, out _)).IsTrue();
            using (pkg)
            {
                AssertThat(pkg!.Entries.Count).IsEqual(1);
                AssertThat(pkg.TryGetEntry("globals.gdb.tu", out var entry)).IsTrue();
                AssertThat(pkg.ReadEntryBytes(entry!)).IsEqual(updatedTu);
            }
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
    public void TestIncrementalSyncDeleteData()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "refii_f2mod_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string packagePath = Path.Combine(tempDir, "IncrementalDelete.f2mod");

        try
        {
            var meta = new ModMetadata { Title = "IncrementalDelete", Version = "1.0.0" };
            byte[] f1 = Encoding.UTF8.GetBytes("keep_me");
            byte[] f2 = Encoding.UTF8.GetBytes("delete_me");
            F2ModPackage.Create(packagePath, meta, new[] { ("keep.txt", f1), ("delete.txt", f2) }, out _);

            // Delete delete.txt:
            bool syncOk = F2ModPackage.IncrementalSync(
                packagePath,
                Array.Empty<(string, byte[])>(),
                deletes: new[] { "delete.txt" });
            AssertThat(syncOk).IsTrue();

            AssertThat(F2ModPackage.TryOpen(packagePath, out F2ModPackage? pkg, out _)).IsTrue();
            using (pkg)
            {
                AssertThat(pkg!.Entries.Count).IsEqual(1);
                AssertThat(pkg.TryGetEntry("keep.txt", out var eKeep)).IsTrue();
                AssertThat(pkg.ReadEntryBytes(eKeep!)).IsEqual(f1);
                AssertThat(pkg.TryGetEntry("delete.txt", out _)).IsFalse();
            }
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
    public void TestDiscoverOnlyF2ModPackages()
    {
        string modsDir = Path.Combine(Path.GetTempPath(), "refii_mods_dir_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(modsDir);

        try
        {
            // Valid .f2mod packages:
            F2ModPackage.Create(Path.Combine(modsDir, "ModA.f2mod"), new ModMetadata { Title = "Mod A" }, Array.Empty<(string, byte[])>(), out _);
            F2ModPackage.Create(Path.Combine(modsDir, "ModB.f2mod"), new ModMetadata { Title = "Mod B" }, Array.Empty<(string, byte[])>(), out _);

            // Directories and other files that must be ignored:
            Directory.CreateDirectory(Path.Combine(modsDir, ".cache"));
            Directory.CreateDirectory(Path.Combine(modsDir, "UnpackedFolder"));
            File.WriteAllText(Path.Combine(modsDir, "random.txt"), "hello");
            File.WriteAllText(Path.Combine(modsDir, "legacy.f2zip"), "zip");

            var discovered = ModWorkspace.DiscoverModInstalls(modsDir);

            AssertThat(discovered.Count).IsEqual(2);
            AssertThat(discovered.Exists(m => m.Name == "ModA")).IsTrue();
            AssertThat(discovered.Exists(m => m.Name == "ModB")).IsTrue();
            AssertThat(discovered.Exists(m => m.Name == "UnpackedFolder")).IsFalse();
            AssertThat(discovered.Exists(m => m.Name == ".cache")).IsFalse();
        }
        finally
        {
            if (Directory.Exists(modsDir))
            {
                Directory.Delete(modsDir, true);
            }
        }
    }

    [TestCase]
    public void TestConflictTallyMatchesConflictDetails()
    {
        var entries = new List<ResolvedEntry>
        {
            new ResolvedEntry
            {
                Path = "globals/globals.gdb.tu",
                Owner = "WinnerMod",
                Kind = ResolvedEntry.KindTu
            }
        };

        var recordConflicts = new List<GdbRecordConflict>
        {
            new GdbRecordConflict
            {
                RecordHash = 0x12345678,
                RecordName = "ToyGun",
                PropertyHash = 0x87654321,
                PropertyName = "BasePrice",
                DatabaseSubPath = "globals/globals.gdb.tu",
                WinnerMod = "WinnerMod",
            }
        };
        recordConflicts[0].LoserMods.Add("LoserMod");

        var tallies = ModIndex.Tally(entries, recordConflicts);

        AssertThat(tallies.ContainsKey("LoserMod")).IsTrue();
        ModTally loserTally = tallies["LoserMod"];

        // Verification: tally.Lost must match tally.ConflictDetails.Count exactly!
        AssertThat(loserTally.Lost).IsEqual(1);
        AssertThat(loserTally.ConflictDetails.Count).IsEqual(1);
        AssertThat(loserTally.ConflictDetails[0]).Contains("BasePrice on ToyGun");
    }
}
