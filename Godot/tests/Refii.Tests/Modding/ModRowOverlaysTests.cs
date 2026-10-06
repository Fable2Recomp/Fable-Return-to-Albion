namespace Refii.Tests.Modding;

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Bnk;
using Refii.Gdb;
using Refii.Mods;
using Refii.Text;

[TestSuite]
public class ModRowOverlaysTests
{
    private const uint SwordNameTag = 0x1000A001;
    private const uint SwordDescTag = 0x1000A002;
    private const uint ModNewItemTag = 0x2000B001;

    [TestCase]
    public void TestBabelOverlayFromF2ModPackageAppliesToVfs()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "refii_babel_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            // 1. Create a base book.babel with vanilla strings
            var baseEntries = new Dictionary<uint, string>
            {
                { SwordNameTag, "Rusty Iron Sword" },
                { SwordDescTag, "A weathered blade from the old days." }
            };
            byte[] baseBabelBytes = CreateMinimalBabel(baseEntries);

            // Set up BnkFileSystem with base file mounted
            var fs = new BnkFileSystem();
            fs.AddVirtualFile(
                "language/en-uk/text/book.babel",
                "base_game",
                baseBabelBytes.Length,
                () => baseBabelBytes);

            // Verify vanilla lookup works
            TextBank? baseBank = TextBank.FromBytes(baseBabelBytes);
            AssertThat(baseBank).IsNotNull();
            AssertThat(baseBank!.TryLookup(SwordNameTag, out string vanillaName)).IsTrue();
            AssertThat(vanillaName).IsEqual("Rusty Iron Sword");

            // 2. Create an .f2mod package containing language/en-uk/text/book.babel.csv
            string pkgPath = Path.Combine(tempDir, "OverhaulMod.f2mod");
            var csvSb = new StringBuilder();
            csvSb.AppendLine("# Key,Value");
            csvSb.AppendLine($"0x{SwordNameTag:X8},Blade of the Hero");
            csvSb.AppendLine($"0x{SwordDescTag:X8},A legendary sword pulsing with ancient power.");
            csvSb.AppendLine($"0x{ModNewItemTag:X8},Moonstone Ring");

            byte[] csvBytes = Encoding.UTF8.GetBytes(csvSb.ToString());
            bool created = F2ModPackage.Create(
                pkgPath,
                new ModMetadata { Title = "OverhaulMod", Version = "1.0.0" },
                new[] { ("language/en-uk/text/book.babel.csv", csvBytes) },
                out string createErr);

            AssertThat(created).IsTrue();
            AssertThat(createErr).IsEqual(string.Empty);

            // 3. Mount overlay and Apply row overlays
            var overlays = new List<ModOverlay>
            {
                new() { Name = "OverhaulMod", PackagePath = pkgPath, AssetRoot = pkgPath }
            };

            List<RowMerge> merges = ModRowOverlays.Apply(fs, overlays);

            AssertThat(merges.Count).IsEqual(1);
            RowMerge merge = merges[0];
            AssertThat(merge.Table).IsEqual("language/en-uk/text/book.babel");
            AssertThat(merge.Applied).IsEqual(3);
            AssertThat(merge.Errors.Count).IsEqual(0);
            AssertThat(merge.Contributors.Count).IsEqual(1);
            AssertThat(merge.Contributors[0]).IsEqual("OverhaulMod");

            // 4. Verify the patched book.babel in VFS
            AssertThat(fs.TryGetFile("language/en-uk/text/book.babel", out BnkFile patchedFile)).IsTrue();
            byte[] patchedBytes = patchedFile.ReadAllBytes();
            TextBank? patchedBank = TextBank.FromBytes(patchedBytes);
            AssertThat(patchedBank).IsNotNull();

            AssertThat(patchedBank!.TryLookup(SwordNameTag, out string patchedName)).IsTrue();
            AssertThat(patchedName).IsEqual("Blade of the Hero");

            AssertThat(patchedBank.TryLookup(SwordDescTag, out string patchedDesc)).IsTrue();
            AssertThat(patchedDesc).IsEqual("A legendary sword pulsing with ancient power.");

            AssertThat(patchedBank.TryLookup(ModNewItemTag, out string modItemName)).IsTrue();
            AssertThat(modItemName).IsEqual("Moonstone Ring");

            // 5. Verify Engine_BnkTextBankLoader integration
            Engine_BnkTextBankLoader.SetResolver(p => fs.TryGetFile(p, out var f) ? f.ReadAllBytes() : null);
            Engine_BnkTextBankLoader.Invalidate();

            AssertThat(Engine_BnkTextBankLoader.TryLookup(SwordNameTag, out string sharedName)).IsTrue();
            AssertThat(sharedName).IsEqual("Blade of the Hero");
            AssertThat(Engine_BnkTextBankLoader.TryLookup(ModNewItemTag, out string sharedModItem)).IsTrue();
            AssertThat(sharedModItem).IsEqual("Moonstone Ring");
        }
        finally
        {
            Engine_BnkTextBankLoader.SetResolver(null);
            Engine_BnkTextBankLoader.Invalidate();
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [TestCase]
    public void TestMultiModBabelOverlaysPriorityAndConflictDetection()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "refii_babel_multi_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var baseEntries = new Dictionary<uint, string>
            {
                { SwordNameTag, "Vanilla Sword" }
            };
            byte[] baseBytes = CreateMinimalBabel(baseEntries);

            var fs = new BnkFileSystem();
            fs.AddVirtualFile(
                "language/en-uk/text/book.babel",
                "base_game",
                baseBytes.Length,
                () => baseBytes);

            // Mod A (lower priority): sets sword to "Bronze Sword", adds Mod A text
            string pkgAPath = Path.Combine(tempDir, "ModA.f2mod");
            var csvA = new StringBuilder();
            csvA.AppendLine($"0x{SwordNameTag:X8},Bronze Sword");
            csvA.AppendLine("0x30000001,ModA Text");
            F2ModPackage.Create(
                pkgAPath,
                new ModMetadata { Title = "ModA" },
                new[] { ("language/en-uk/text/book.babel.csv", Encoding.UTF8.GetBytes(csvA.ToString())) },
                out _);

            // Mod B (higher priority): sets sword to "Steel Sword", adds Mod B text
            string pkgBPath = Path.Combine(tempDir, "ModB.f2mod");
            var csvB = new StringBuilder();
            csvB.AppendLine($"0x{SwordNameTag:X8},Steel Sword");
            csvB.AppendLine("0x30000002,ModB Text");
            F2ModPackage.Create(
                pkgBPath,
                new ModMetadata { Title = "ModB" },
                new[] { ("language/en-uk/text/book.babel.csv", Encoding.UTF8.GetBytes(csvB.ToString())) },
                out _);

            // Overlays list: lowest priority first (ModA then ModB)
            var overlays = new List<ModOverlay>
            {
                new() { Name = "ModA", PackagePath = pkgAPath, AssetRoot = pkgAPath },
                new() { Name = "ModB", PackagePath = pkgBPath, AssetRoot = pkgBPath }
            };

            List<RowMerge> merges = ModRowOverlays.Apply(fs, overlays);

            AssertThat(merges.Count).IsEqual(1);
            RowMerge merge = merges[0];

            // Verify conflict was detected for SwordNameTag
            AssertThat(merge.Conflicts.Count).IsEqual(1);
            AssertThat(merge.Conflicts[0].Key).IsEqual($"0x{SwordNameTag:X8}");
            AssertThat(merge.Conflicts[0].Winner).IsEqual("ModB");
            AssertThat(merge.Conflicts[0].Losers.Contains("ModA")).IsTrue();

            // Verify both contributors recorded
            AssertThat(merge.Contributors.Contains("ModA")).IsTrue();
            AssertThat(merge.Contributors.Contains("ModB")).IsTrue();

            // Verify winning value in VFS is ModB's
            AssertThat(fs.TryGetFile("language/en-uk/text/book.babel", out BnkFile patchedFile)).IsTrue();
            TextBank? bank = TextBank.FromBytes(patchedFile.ReadAllBytes());
            AssertThat(bank).IsNotNull();

            AssertThat(bank!.TryLookup(SwordNameTag, out string finalSwordName)).IsTrue();
            AssertThat(finalSwordName).IsEqual("Steel Sword");

            AssertThat(bank.TryLookup(0x30000001, out string modAText)).IsTrue();
            AssertThat(modAText).IsEqual("ModA Text");

            AssertThat(bank.TryLookup(0x30000002, out string modBText)).IsTrue();
            AssertThat(modBText).IsEqual("ModB Text");
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
    public void TestBabelOverlayWithFilesPrefixInPackage()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "refii_babel_prefix_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var baseEntries = new Dictionary<uint, string>
            {
                { SwordNameTag, "Vanilla Sword" }
            };
            byte[] baseBytes = CreateMinimalBabel(baseEntries);

            var fs = new BnkFileSystem();
            fs.AddVirtualFile(
                "language/en-uk/text/book.babel",
                "base_game",
                baseBytes.Length,
                () => baseBytes);

            // Create package with "files/" prefix
            string pkgPath = Path.Combine(tempDir, "PrefixMod.f2mod");
            string csv = $"0x{SwordNameTag:X8},Prefixed Sword Name\n";
            F2ModPackage.Create(
                pkgPath,
                new ModMetadata { Title = "PrefixMod" },
                new[] { ("files/language/en-uk/text/book.babel.csv", Encoding.UTF8.GetBytes(csv)) },
                out _);

            var overlays = new List<ModOverlay>
            {
                new() { Name = "PrefixMod", PackagePath = pkgPath, AssetRoot = pkgPath }
            };

            List<RowMerge> merges = ModRowOverlays.Apply(fs, overlays);

            AssertThat(merges.Count).IsEqual(1);
            AssertThat(merges[0].Table).IsEqual("language/en-uk/text/book.babel");
            AssertThat(merges[0].Applied).IsEqual(1);

            AssertThat(fs.TryGetFile("language/en-uk/text/book.babel", out BnkFile patchedFile)).IsTrue();
            TextBank? bank = TextBank.FromBytes(patchedFile.ReadAllBytes());
            AssertThat(bank).IsNotNull();
            AssertThat(bank!.TryLookup(SwordNameTag, out string name)).IsTrue();
            AssertThat(name).IsEqual("Prefixed Sword Name");
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
    public void TestBabelOverlayWithDataAliasResolution()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "refii_babel_alias_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var baseEntries = new Dictionary<uint, string>
            {
                { SwordNameTag, "Base Vanilla Item" }
            };
            byte[] baseBytes = CreateMinimalBabel(baseEntries);

            var fs = new BnkFileSystem();
            // Base file added with data/ prefix (as found on disk)
            fs.AddVirtualFile(
                "data/language/en-uk/text/book.babel",
                "base_game",
                baseBytes.Length,
                () => baseBytes);

            // Mod authored without data/ prefix
            string pkgPath = Path.Combine(tempDir, "AliasMod.f2mod");
            string csv = $"0x{SwordNameTag:X8},Patched Through Alias\n";
            F2ModPackage.Create(
                pkgPath,
                new ModMetadata { Title = "AliasMod" },
                new[] { ("language/en-uk/text/book.babel.csv", Encoding.UTF8.GetBytes(csv)) },
                out _);

            var overlays = new List<ModOverlay>
            {
                new() { Name = "AliasMod", PackagePath = pkgPath, AssetRoot = pkgPath }
            };

            List<RowMerge> merges = ModRowOverlays.Apply(fs, overlays);

            AssertThat(merges.Count).IsEqual(1);
            AssertThat(merges[0].Applied).IsEqual(1);

            // Both the alias and original path should return patched bytes
            AssertThat(fs.TryGetFile("language/en-uk/text/book.babel", out BnkFile file1)).IsTrue();
            AssertThat(fs.TryGetFile("data/language/en-uk/text/book.babel", out BnkFile file2)).IsTrue();

            TextBank? bank1 = TextBank.FromBytes(file1.ReadAllBytes());
            TextBank? bank2 = TextBank.FromBytes(file2.ReadAllBytes());

            AssertThat(bank1).IsNotNull();
            AssertThat(bank2).IsNotNull();
            AssertThat(bank1!.TryLookup(SwordNameTag, out string val1)).IsTrue();
            AssertThat(bank2!.TryLookup(SwordNameTag, out string val2)).IsTrue();
            AssertThat(val1).IsEqual("Patched Through Alias");
            AssertThat(val2).IsEqual("Patched Through Alias");
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
    public void TestDetectRowConflictsBetweenMultipleMods()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "refii_rowconflict_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            string pkgAPath = Path.Combine(tempDir, "ModA.f2mod");
            string pkgBPath = Path.Combine(tempDir, "ModB.f2mod");

            string csvA = $"0x{SwordNameTag:X8},Blade From Mod A\n0x{SwordDescTag:X8},Desc A\n";
            string csvB = $"0x{SwordNameTag:X8},Blade From Mod B\n0x{ModNewItemTag:X8},New Item B\n";

            var filesA = new List<(string Path, byte[] Bytes)>
            {
                ("language/en-uk/text/book.babel.csv", Encoding.UTF8.GetBytes(csvA))
            };
            var filesB = new List<(string Path, byte[] Bytes)>
            {
                ("language/en-uk/text/book.babel.csv", Encoding.UTF8.GetBytes(csvB))
            };

            var metaA = new ModMetadata { Title = "Mod A", Version = "1.0", Author = "Dev" };
            var metaB = new ModMetadata { Title = "Mod B", Version = "1.0", Author = "Dev" };

            AssertThat(F2ModPackage.Create(pkgAPath, metaA, filesA, out _)).IsTrue();
            AssertThat(F2ModPackage.Create(pkgBPath, metaB, filesB, out _)).IsTrue();

            var order = new ModLoadOrder();
            order.Enabled.Add("ModA");
            order.Enabled.Add("ModB");

            var installs = new List<ModInstall>
            {
                new()
                {
                    Name = "ModA",
                    PackagePath = pkgAPath,
                    Metadata = metaA
                },
                new()
                {
                    Name = "ModB",
                    PackagePath = pkgBPath,
                    Metadata = metaB
                }
            };

            List<(string Table, RowConflict Conflict)> conflicts = ModRowOverlays.DetectRowConflicts(order, installs);

            // Both mods touch SwordNameTag (0x1000A001) in book.babel
            AssertThat(conflicts.Count).IsEqual(1);
            var (table, conflict) = conflicts[0];
            AssertThat(table).IsEqual("language/en-uk/text/book.babel");
            AssertThat(conflict.Key).IsEqual($"0x{SwordNameTag:X8}");
            AssertThat(conflict.Winner).IsEqual("ModA");
            AssertThat(conflict.Losers.Count).IsEqual(1);
            AssertThat(conflict.Losers[0]).IsEqual("ModB");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    private static byte[] CreateMinimalBabel(Dictionary<uint, string> entries)
    {
        var blocks = new List<byte[]>();
        var records = new List<(uint Key, uint Block, uint Offset)>();
        var current = new MemoryStream();

        foreach (var (key, value) in entries)
        {
            var bytes = new byte[4 + ((value.Length + 1) * 2)];
            BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)(value.Length + 1));
            for (int i = 0; i < value.Length; i++)
            {
                BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4 + (i * 2)), value[i]);
            }
            records.Add((key, 0, (uint)current.Length));
            current.Write(bytes);
        }
        blocks.Add(current.ToArray());

        using var file = new MemoryStream();
        void WriteU32(uint val)
        {
            Span<byte> w = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(w, val);
            file.Write(w);
        }

        WriteU32(0x5B010000u);
        WriteU32((uint)records.Count);
        foreach (var (key, block, offset) in records)
        {
            WriteU32(key);
            WriteU32(block);
            WriteU32(offset);
        }

        WriteU32((uint)blocks.Count);
        for (int i = 0; i < blocks.Count; i++)
        {
            using var compStream = new MemoryStream();
            using (var zlib = new ZLibStream(compStream, CompressionLevel.Optimal, true))
            {
                zlib.Write(blocks[i]);
            }
            byte[] compressed = compStream.ToArray();
            WriteU32((uint)i);
            WriteU32((uint)compressed.Length);
            WriteU32((uint)blocks[i].Length);
            file.Write(compressed);
        }

        // Tail: 4 bytes hint, 4 bytes speechLinks, 4 bytes textGroups, 4 bytes speakerNames
        WriteU32(1024);
        WriteU32(0);
        WriteU32(0);
        WriteU32(0);

        return file.ToArray();
    }
}
