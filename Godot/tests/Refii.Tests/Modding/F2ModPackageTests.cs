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
        string tempDir = Path.Combine(Refii.Tests.TestTemp.Root, "refii_f2mod_test_" + Guid.NewGuid().ToString("N"));
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
        string tempDir = Path.Combine(Refii.Tests.TestTemp.Root, "refii_f2mod_test_" + Guid.NewGuid().ToString("N"));
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
        string tempDir = Path.Combine(Refii.Tests.TestTemp.Root, "refii_f2mod_test_" + Guid.NewGuid().ToString("N"));
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
        string tempDir = Path.Combine(Refii.Tests.TestTemp.Root, "refii_f2mod_test_" + Guid.NewGuid().ToString("N"));
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
        string modsDir = Path.Combine(Refii.Tests.TestTemp.Root, "refii_mods_dir_" + Guid.NewGuid().ToString("N"));
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

    [TestCase]
    public void TestCalculateZombieBytesOnFreshPackageReturnsZero()
    {
        string tempDir = Path.Combine(Refii.Tests.TestTemp.Root, "refii_defrag_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string pkgPath = Path.Combine(tempDir, "Fresh.f2mod");

        try
        {
            var meta = new ModMetadata { Title = "FreshMod", Version = "1.0.0", Author = "Tester" };
            byte[] f1 = new byte[1024];
            byte[] f2 = new byte[2048];
            Array.Fill(f1, (byte)0xAA);
            Array.Fill(f2, (byte)0xBB);

            AssertThat(F2ModPackage.Create(pkgPath, meta, new[] { ("a.bin", f1), ("b.bin", f2) }, out _)).IsTrue();

            ulong zombies = F2ModPackage.CalculateZombieBytes(pkgPath);
            AssertThat(zombies).IsEqual(0UL);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [TestCase]
    public void TestCalculateZombieBytesDetectsOrphanedBytesAfterDelete()
    {
        string tempDir = Path.Combine(Refii.Tests.TestTemp.Root, "refii_defrag_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string pkgPath = Path.Combine(tempDir, "ZombieDelete.f2mod");

        try
        {
            var meta = new ModMetadata { Title = "ZombieDelete", Version = "1.0.0" };
            byte[] keep = new byte[100_000];
            byte[] del = new byte[200_000];
            Array.Fill(keep, (byte)0x11);
            Array.Fill(del, (byte)0x22);

            AssertThat(F2ModPackage.Create(pkgPath, meta, new[] { ("keep.bin", keep), ("del.bin", del) }, out _)).IsTrue();
            long initialSize = new FileInfo(pkgPath).Length;

            // Delete del.bin without defrag
            AssertThat(F2ModPackage.IncrementalSync(pkgPath, Array.Empty<(string, byte[])>(), new[] { "del.bin" }, defrag: false)).IsTrue();

            long sizeAfterDelete = new FileInfo(pkgPath).Length;
            // The 200,000 bytes of del.bin are still stranded in the payload section
            AssertThat(sizeAfterDelete > 250_000).IsTrue();

            ulong zombies = F2ModPackage.CalculateZombieBytes(pkgPath);
            AssertThat(zombies >= 200_000UL).IsTrue();
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [TestCase]
    public void TestDefragmentReclaimsSpaceFromDeletedEntries()
    {
        string tempDir = Path.Combine(Refii.Tests.TestTemp.Root, "refii_defrag_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string pkgPath = Path.Combine(tempDir, "ReclaimDelete.f2mod");

        try
        {
            var meta = new ModMetadata { Title = "ReclaimDelete", Version = "2.5.0", Author = "RefiiDev" };
            byte[] f1 = new byte[150_000];
            byte[] f2 = new byte[250_000];
            for (int i = 0; i < f1.Length; i++) f1[i] = (byte)(i & 0xFF);
            for (int i = 0; i < f2.Length; i++) f2[i] = (byte)((i * 3) & 0xFF);

            AssertThat(F2ModPackage.Create(pkgPath, meta, new[] { ("survivor.dat", f1), ("victim.dat", f2) }, out _)).IsTrue();

            // Delete victim.dat
            AssertThat(F2ModPackage.IncrementalSync(pkgPath, Array.Empty<(string, byte[])>(), new[] { "victim.dat" }, defrag: false)).IsTrue();

            long bloatedSize = new FileInfo(pkgPath).Length;
            AssertThat(F2ModPackage.CalculateZombieBytes(pkgPath) >= 250_000UL).IsTrue();

            // Defragment
            AssertThat(F2ModPackage.Defragment(pkgPath, out string error)).IsTrue();
            AssertThat(error).IsEqual(string.Empty);

            long compactedSize = new FileInfo(pkgPath).Length;
            AssertThat(compactedSize < bloatedSize - 200_000).IsTrue();
            AssertThat(F2ModPackage.CalculateZombieBytes(pkgPath)).IsEqual(0UL);

            // Verify package integrity & data bit-for-bit
            AssertThat(F2ModPackage.TryOpen(pkgPath, out F2ModPackage? pkg, out string openErr)).IsTrue();
            AssertThat(openErr).IsEqual(string.Empty);
            using (pkg)
            {
                AssertThat(pkg!.Metadata.Title).IsEqual("ReclaimDelete");
                AssertThat(pkg.Metadata.Version).IsEqual("2.5.0");
                AssertThat(pkg.Metadata.Author).IsEqual("RefiiDev");
                AssertThat(pkg.Entries.Count).IsEqual(1);
                AssertThat(pkg.TryGetEntry("survivor.dat", out var entry)).IsTrue();
                AssertThat(entry!.Size).IsEqual((ulong)f1.Length);
                AssertThat(pkg.ReadEntryBytes(entry)).IsEqual(f1);
            }
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [TestCase]
    public void TestDefragmentReclaimsSpaceFromMultipleOverwrites()
    {
        string tempDir = Path.Combine(Refii.Tests.TestTemp.Root, "refii_defrag_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string pkgPath = Path.Combine(tempDir, "Overwrites.f2mod");

        try
        {
            var meta = new ModMetadata { Title = "OverwritesMod", Version = "1.0.0" };
            byte[] dataV1 = new byte[100_000];
            Array.Fill(dataV1, (byte)1);
            AssertThat(F2ModPackage.Create(pkgPath, meta, new[] { ("model.mdl", dataV1) }, out _)).IsTrue();

            // Overwrite 4 times with different data
            byte[] latestData = Array.Empty<byte>();
            for (int v = 2; v <= 5; v++)
            {
                latestData = new byte[100_000];
                Array.Fill(latestData, (byte)v);
                AssertThat(F2ModPackage.IncrementalSync(pkgPath, new[] { ("model.mdl", latestData) }, defrag: false)).IsTrue();
            }

            long bloatedSize = new FileInfo(pkgPath).Length;
            // 5 versions of 100KB payload means > 400KB of dead zombie payload
            AssertThat(bloatedSize >= 500_000).IsTrue();
            AssertThat(F2ModPackage.CalculateZombieBytes(pkgPath) >= 350_000UL).IsTrue();

            // Defragment
            AssertThat(F2ModPackage.Defragment(pkgPath, out string error)).IsTrue();
            AssertThat(error).IsEqual(string.Empty);

            long compactedSize = new FileInfo(pkgPath).Length;
            AssertThat(compactedSize <= 120_000).IsTrue();
            AssertThat(F2ModPackage.CalculateZombieBytes(pkgPath)).IsEqual(0UL);

            // Verify content is latest version
            AssertThat(F2ModPackage.TryOpen(pkgPath, out F2ModPackage? pkg, out _)).IsTrue();
            using (pkg)
            {
                AssertThat(pkg!.Entries.Count).IsEqual(1);
                AssertThat(pkg.TryGetEntry("model.mdl", out var entry)).IsTrue();
                AssertThat(pkg.ReadEntryBytes(entry!)).IsEqual(latestData);
            }
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [TestCase]
    public void TestIncrementalSyncWithDefragFlagDirectlyCompacts()
    {
        string tempDir = Path.Combine(Refii.Tests.TestTemp.Root, "refii_defrag_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string pkgPath = Path.Combine(tempDir, "AutoDefrag.f2mod");

        try
        {
            var meta = new ModMetadata { Title = "AutoDefrag", Version = "1.0.0" };
            byte[] fileA = new byte[80_000];
            byte[] fileB = new byte[80_000];
            Array.Fill(fileA, (byte)0xA);
            Array.Fill(fileB, (byte)0xB);

            AssertThat(F2ModPackage.Create(pkgPath, meta, new[] { ("a.bin", fileA), ("b.bin", fileB) }, out _)).IsTrue();

            // Delete fileB with defrag: true
            AssertThat(F2ModPackage.IncrementalSync(
                pkgPath,
                Array.Empty<(string, byte[])>(),
                deletes: new[] { "b.bin" },
                defrag: true)).IsTrue();

            long finalSize = new FileInfo(pkgPath).Length;
            AssertThat(finalSize < 100_000).IsTrue();
            AssertThat(F2ModPackage.CalculateZombieBytes(pkgPath)).IsEqual(0UL);

            AssertThat(F2ModPackage.TryOpen(pkgPath, out F2ModPackage? pkg, out _)).IsTrue();
            using (pkg)
            {
                AssertThat(pkg!.Entries.Count).IsEqual(1);
                AssertThat(pkg.TryGetEntry("a.bin", out var entryA)).IsTrue();
                AssertThat(pkg.ReadEntryBytes(entryA!)).IsEqual(fileA);
                AssertThat(pkg.TryGetEntry("b.bin", out _)).IsFalse();
            }
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [TestCase]
    public void TestDefragmentPreserves16ByteAlignmentAndArbitrarySizes()
    {
        string tempDir = Path.Combine(Refii.Tests.TestTemp.Root, "refii_defrag_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string pkgPath = Path.Combine(tempDir, "Alignment.f2mod");

        try
        {
            var meta = new ModMetadata { Title = "AlignmentMod", Version = "3.0.0" };
            byte[] s1 = new byte[17];   // Odd size
            byte[] s2 = new byte[333];  // Odd size
            byte[] s3 = new byte[4099]; // Prime/odd
            new Random(42).NextBytes(s1);
            new Random(43).NextBytes(s2);
            new Random(44).NextBytes(s3);

            AssertThat(F2ModPackage.Create(pkgPath, meta, new[]
            {
                ("odd17.bin", s1),
                ("odd333.bin", s2),
                ("odd4099.bin", s3)
            }, out _)).IsTrue();

            // Force a defragment
            AssertThat(F2ModPackage.Defragment(pkgPath, out string err)).IsTrue();
            AssertThat(err).IsEqual(string.Empty);

            AssertThat(F2ModPackage.TryOpen(pkgPath, out F2ModPackage? pkg, out _)).IsTrue();
            using (pkg)
            {
                AssertThat(pkg!.Entries.Count).IsEqual(3);
                foreach (var entry in pkg.Entries)
                {
                    AssertThat(entry.Offset % 16).IsEqual(0UL);
                }

                AssertThat(pkg.TryGetEntry("odd17.bin", out var e1)).IsTrue();
                AssertThat(pkg.ReadEntryBytes(e1!)).IsEqual(s1);

                AssertThat(pkg.TryGetEntry("odd333.bin", out var e2)).IsTrue();
                AssertThat(pkg.ReadEntryBytes(e2!)).IsEqual(s2);

                AssertThat(pkg.TryGetEntry("odd4099.bin", out var e3)).IsTrue();
                AssertThat(pkg.ReadEntryBytes(e3!)).IsEqual(s3);
            }
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [TestCase]
    public void TestDefragmentEmptyPackage()
    {
        string tempDir = Path.Combine(Refii.Tests.TestTemp.Root, "refii_defrag_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string pkgPath = Path.Combine(tempDir, "Empty.f2mod");

        try
        {
            var meta = new ModMetadata { Title = "EmptyMod", Version = "1.0.0", Description = "No files" };
            AssertThat(F2ModPackage.Create(pkgPath, meta, Array.Empty<(string, byte[])>(), out _)).IsTrue();

            AssertThat(F2ModPackage.Defragment(pkgPath, out string err)).IsTrue();
            AssertThat(err).IsEqual(string.Empty);

            AssertThat(F2ModPackage.TryOpen(pkgPath, out F2ModPackage? pkg, out _)).IsTrue();
            using (pkg)
            {
                AssertThat(pkg!.Metadata.Title).IsEqual("EmptyMod");
                AssertThat(pkg.Entries.Count).IsEqual(0);
            }
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}
