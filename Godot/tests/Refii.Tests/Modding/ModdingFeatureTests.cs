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

/// <summary>
/// End-to-end feature integration tests for the Refii modding pipeline.
/// Covers full multi-mod VFS mounting, GDB leaf synthesis with inherited base archetypes,
/// Babel text overlays, multi-system conflict tallying, and cache invalidation lifecycles.
/// </summary>
[TestSuite]
public class ModdingFeatureTests
{
    private const uint WoodenSwordId = 0xA2AE58D0;
    private const uint BaseMeleeArchetypeId = 0x11110001;
    private const uint HashParent = GdbView.HashParent;
    private const uint HashWeaponType = 0x77777777;
    private const uint HashDamageMult = 0x88888888;
    private const uint HashBasePrice = 0x44C44B51;

    private const uint SwordNameTag = 0x1000A001;
    private const uint SwordDescTag = 0x1000A002;

    [TestCase]
    public void TestFullModdingLifecycle_MultiModCompositionAndInheritance_E2E()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "refii_feature_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            // 1. Build realistic base game database (globals/globals.gdb)
            var baseDb = new GdbPatchDatabase();

            // Archetype record with WeaponType = 1 (Melee)
            var archEntry = new GdbPatchEntry(BaseMeleeArchetypeId, 0x01);
            archEntry.Properties.Add(new GdbPatchProperty(HashWeaponType, GdbFieldType.Int, 1));
            baseDb.Entries.Add(archEntry);

            // WoodenSword record inheriting from archetype
            var swordEntry = new GdbPatchEntry(WoodenSwordId, 0x01);
            swordEntry.Properties.Add(new GdbPatchProperty(HashParent, GdbFieldType.Reference, BaseMeleeArchetypeId));
            swordEntry.Properties.Add(new GdbPatchProperty(HashBasePrice, GdbFieldType.Int, 20));
            swordEntry.Properties.Add(new GdbPatchProperty(HashDamageMult, GdbFieldType.Float, BitConverter.SingleToUInt32Bits(1.0f)));
            baseDb.Entries.Add(swordEntry);

            baseDb.Names[BaseMeleeArchetypeId] = "BaseMeleeWeapon";
            baseDb.Names[WoodenSwordId] = "WoodenSword";
            baseDb.Names[HashParent] = "parent";
            baseDb.Names[HashWeaponType] = "WeaponType";
            baseDb.Names[HashBasePrice] = "BasePrice";
            baseDb.Names[HashDamageMult] = "DamageMultiplier";

            byte[] baseGdbBytes = GdbWriter.Serialize(baseDb);

            // 2. Build realistic base book.babel
            var baseBabelEntries = new Dictionary<uint, string>
            {
                { SwordNameTag, "Wooden Practice Sword" },
                { SwordDescTag, "A blunt training sword." }
            };
            byte[] baseBabelBytes = CreateMinimalBabel(baseBabelEntries);

            // 3. Mount base files into BnkFileSystem and bind resolver
            var fs = new BnkFileSystem();
            fs.AddVirtualFile("data/globals/globals.gdb", "base", baseGdbBytes.Length, () => baseGdbBytes);
            fs.AddVirtualFile("globals/globals.gdb", "base", baseGdbBytes.Length, () => baseGdbBytes);
            fs.AddVirtualFile("data/language/en-uk/text/book.babel", "base", baseBabelBytes.Length, () => baseBabelBytes);
            fs.AddVirtualFile("language/en-uk/text/book.babel", "base", baseBabelBytes.Length, () => baseBabelBytes);

            Engine_GameGdb.SetResolver(vpath => fs.TryGetFile(vpath, out var f) ? f.ReadAllBytes() : null);
            Engine_GameGdb.SetBaseResolver(vpath => fs.TryGetFile(vpath, out var f) ? f.ReadAllBytes() : null);

            // 4. Create ModA package (lower priority): DamageMultiplier = 2.0, Name = "Oak Practice Sword"
            string modAPath = Path.Combine(tempDir, "ModA.f2mod");
            var modADb = new GdbPatchDatabase();
            var modAEntry = new GdbPatchEntry(WoodenSwordId, 0x01);
            modAEntry.Properties.Add(new GdbPatchProperty(HashDamageMult, GdbFieldType.Float, BitConverter.SingleToUInt32Bits(2.0f)));
            modADb.Entries.Add(modAEntry);
            modADb.Names[HashDamageMult] = "DamageMultiplier";

            string modACsv = $"0x{SwordNameTag:X8},Oak Practice Sword\n";
            byte[] looseA = new byte[] { 0xAA, 0xAA };

            var filesA = new List<(string Path, byte[] Bytes)>
            {
                ("globals/globals.gdb.tu", GdbWriter.Serialize(modADb)),
                ("language/en-uk/text/book.babel.csv", Encoding.UTF8.GetBytes(modACsv)),
                ("textures/sword.dds", looseA)
            };
            AssertThat(F2ModPackage.Create(modAPath, new ModMetadata { Title = "Mod A" }, filesA, out _)).IsTrue();

            // 5. Create ModB package (higher priority): DamageMultiplier = 3.0, BasePrice = 99, Name = "Ironwood Practice Sword"
            string modBPath = Path.Combine(tempDir, "ModB.f2mod");
            var modBDb = new GdbPatchDatabase();
            var modBEntry = new GdbPatchEntry(WoodenSwordId, 0x01);
            modBEntry.Properties.Add(new GdbPatchProperty(HashDamageMult, GdbFieldType.Float, BitConverter.SingleToUInt32Bits(3.0f)));
            modBEntry.Properties.Add(new GdbPatchProperty(HashBasePrice, GdbFieldType.Int, 99));
            modBDb.Entries.Add(modBEntry);
            modBDb.Names[HashDamageMult] = "DamageMultiplier";
            modBDb.Names[HashBasePrice] = "BasePrice";

            string modBCsv = $"0x{SwordNameTag:X8},Ironwood Practice Sword\n";
            byte[] looseB = new byte[] { 0xBB, 0xBB };

            var filesB = new List<(string Path, byte[] Bytes)>
            {
                ("globals/globals.gdb.tu", GdbWriter.Serialize(modBDb)),
                ("language/en-uk/text/book.babel.csv", Encoding.UTF8.GetBytes(modBCsv)),
                ("textures/sword.dds", looseB)
            };
            AssertThat(F2ModPackage.Create(modBPath, new ModMetadata { Title = "Mod B" }, filesB, out _)).IsTrue();

            // 6. Setup Load Order: ModB (highest priority / slot 1), ModA (lower priority / slot 2)
            var order = new ModLoadOrder();
            order.Enabled.Add("ModB");
            order.Enabled.Add("ModA");

            var installs = ModWorkspace.DiscoverModInstalls(tempDir);
            AssertThat(installs.Count).IsEqual(2);

            var overlays = ModIndex.ResolveOverlays(tempDir, order, installs);
            AssertThat(overlays.Count).IsEqual(2);

            // 7. Mount packages and run full compositors
            List<CompositeGdbResult> gdbResults = GdbPatchCompositor.Apply(fs, overlays, tempDir);
            List<RowMerge> rowResults = ModRowOverlays.Apply(fs, overlays);

            AssertThat(gdbResults.Count).IsEqual(1);
            AssertThat(rowResults.Count).IsEqual(1);

            // 8. Assert VFS GDB Title Update synthesis
            AssertThat(fs.TryGetFile("globals/globals.gdb.tu", out BnkFile tuFile)).IsTrue();
            byte[] tuBytes = tuFile.ReadAllBytes();
            AssertThat(GdbView.TryParse(tuBytes, out GdbView tuView, out _)).IsTrue();

            // Record must exist
            AssertThat(tuView.TryLookup(WoodenSwordId, out int recIdx)).IsTrue();

            var fields = tuView.Fields(recIdx);

            // MUST preserve parent reference to archetype (crucial for native weapon draw!)
            var parentField = fields.Find(f => f.Hash == HashParent);
            AssertThat(parentField).IsNotNull();
            AssertThat(parentField!.Type).IsEqual(GdbFieldType.Reference);
            AssertThat(parentField.Raw).IsEqual(BaseMeleeArchetypeId);
            AssertThat(parentField.Name).IsEqual("parent");

            // ModB (winner) won DamageMultiplier = 3.0f
            var dmgField = fields.Find(f => f.Hash == HashDamageMult);
            AssertThat(dmgField).IsNotNull();
            AssertThat(dmgField!.Type).IsEqual(GdbFieldType.Float);
            AssertThat(dmgField.AsFloat).IsEqualApprox(3.0f, 0.001f);

            // ModB (winner) set BasePrice = 99
            var priceField = fields.Find(f => f.Hash == HashBasePrice);
            AssertThat(priceField).IsNotNull();
            AssertThat(priceField!.Raw).IsEqual(99u);

            // String table in synthesized GDB must contain entry name and property names
            AssertThat(tuView.Names.ContainsKey(WoodenSwordId)).IsTrue();
            AssertThat(tuView.Names[WoodenSwordId]).IsEqual("WoodenSword");
            AssertThat(tuView.Names.ContainsKey(HashParent)).IsTrue();
            AssertThat(tuView.Names[HashParent]).IsEqual("parent");

            // 9. Assert VFS Babel text overlay resolution
            AssertThat(fs.TryGetFile("language/en-uk/text/book.babel", out BnkFile babelFile)).IsTrue();
            TextBank? mergedBank = TextBank.FromBytes(babelFile.ReadAllBytes());
            AssertThat(mergedBank).IsNotNull();

            // ModB won the sword name
            AssertThat(mergedBank!.TryLookup(SwordNameTag, out string actualName)).IsTrue();
            AssertThat(actualName).IsEqual("Ironwood Practice Sword");

            // Unmodified vanilla sword description preserved
            AssertThat(mergedBank.TryLookup(SwordDescTag, out string actualDesc)).IsTrue();
            AssertThat(actualDesc).IsEqual("A blunt training sword.");
        }
        finally
        {
            Engine_GameGdb.SetResolver(null);
            Engine_GameGdb.SetBaseResolver(null);
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [TestCase]
    public void TestConflictDetectionAndTally_E2E()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "refii_feature_conflict_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            // Create ModA and ModB packages with colliding GDB properties and Babel strings
            string modAPath = Path.Combine(tempDir, "ModA.f2mod");
            var modADb = new GdbPatchDatabase();
            var modAEntry = new GdbPatchEntry(WoodenSwordId, 0x01);
            modAEntry.Properties.Add(new GdbPatchProperty(HashDamageMult, GdbFieldType.Float, BitConverter.SingleToUInt32Bits(2.0f)));
            modADb.Entries.Add(modAEntry);
            modADb.Names[HashDamageMult] = "DamageMultiplier";

            var filesA = new List<(string Path, byte[] Bytes)>
            {
                ("globals/globals.gdb.tu", GdbWriter.Serialize(modADb)),
                ("language/en-uk/text/book.babel.csv", Encoding.UTF8.GetBytes($"0x{SwordNameTag:X8},Name A\n")),
                ("textures/sword.dds", new byte[] { 0xAA })
            };
            AssertThat(F2ModPackage.Create(modAPath, new ModMetadata { Title = "Mod A" }, filesA, out _)).IsTrue();

            string modBPath = Path.Combine(tempDir, "ModB.f2mod");
            var modBDb = new GdbPatchDatabase();
            var modBEntry = new GdbPatchEntry(WoodenSwordId, 0x01);
            modBEntry.Properties.Add(new GdbPatchProperty(HashDamageMult, GdbFieldType.Float, BitConverter.SingleToUInt32Bits(3.0f)));
            modBDb.Entries.Add(modBEntry);
            modBDb.Names[HashDamageMult] = "DamageMultiplier";

            var filesB = new List<(string Path, byte[] Bytes)>
            {
                ("globals/globals.gdb.tu", GdbWriter.Serialize(modBDb)),
                ("language/en-uk/text/book.babel.csv", Encoding.UTF8.GetBytes($"0x{SwordNameTag:X8},Name B\n")),
                ("textures/sword.dds", new byte[] { 0xBB })
            };
            AssertThat(F2ModPackage.Create(modBPath, new ModMetadata { Title = "Mod B" }, filesB, out _)).IsTrue();

            var order = new ModLoadOrder();
            order.Enabled.Add("ModB"); // Priority 1 (winner)
            order.Enabled.Add("ModA"); // Priority 2 (loser)

            var installs = ModWorkspace.DiscoverModInstalls(tempDir);

            // Detect conflicts across all subsystems
            List<GdbRecordConflict> gdbConflicts = GdbPatchCompositor.DetectRecordConflicts(tempDir, order, installs);
            List<(string Table, RowConflict Conflict)> rowConflicts = ModRowOverlays.DetectRowConflicts(order, installs);

            AssertThat(gdbConflicts.Count).IsEqual(1);
            AssertThat(gdbConflicts[0].WinnerMod).IsEqual("ModB");
            AssertThat(gdbConflicts[0].LoserMods.Count).IsEqual(1);
            AssertThat(gdbConflicts[0].LoserMods[0]).IsEqual("ModA");

            AssertThat(rowConflicts.Count).IsEqual(1);
            AssertThat(rowConflicts[0].Conflict.Winner).IsEqual("ModB");
            AssertThat(rowConflicts[0].Conflict.Losers.Count).IsEqual(1);
            AssertThat(rowConflicts[0].Conflict.Losers[0]).IsEqual("ModA");

            // Mock file collision entry
            var resolvedEntries = new List<ResolvedEntry>
            {
                new()
                {
                    Path = "textures/sword.dds",
                    Owner = "ModB",
                    Kind = ResolvedEntry.KindOverride,
                    Shadowed = new List<string> { "ModA" }
                }
            };

            Dictionary<string, ModTally> tallies = ModIndex.Tally(resolvedEntries, gdbConflicts, rowConflicts);

            AssertThat(tallies.ContainsKey("ModB")).IsTrue();
            AssertThat(tallies.ContainsKey("ModA")).IsTrue();

            ModTally tallyB = tallies["ModB"];
            ModTally tallyA = tallies["ModA"];

            // Winner (ModB): 0 lost, 3 won conflicts
            AssertThat(tallyB.Lost).IsEqual(0);
            AssertThat(tallyB.Conflicts).IsEqual(3);
            AssertThat(tallyB.WonConflictDetails.Count).IsEqual(3);
            AssertThat(tallyB.WonConflictDetails.Exists(d => d.Contains("textures/sword.dds"))).IsTrue();
            AssertThat(tallyB.WonConflictDetails.Exists(d => d.Contains("DamageMultiplier"))).IsTrue();
            AssertThat(tallyB.WonConflictDetails.Exists(d => d.Contains($"0x{SwordNameTag:X8}"))).IsTrue();

            // Loser (ModA): 3 lost conflicts
            AssertThat(tallyA.Lost).IsEqual(3);
            AssertThat(tallyA.Conflicts).IsEqual(3);
            AssertThat(tallyA.ConflictDetails.Count).IsEqual(3);
            AssertThat(tallyA.ConflictDetails.Exists(d => d.Contains("textures/sword.dds"))).IsTrue();
            AssertThat(tallyA.ConflictDetails.Exists(d => d.Contains("DamageMultiplier"))).IsTrue();
            AssertThat(tallyA.ConflictDetails.Exists(d => d.Contains($"0x{SwordNameTag:X8}"))).IsTrue();
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
    public void TestModDisablingAndCacheLifecycle_E2E()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "refii_feature_cache_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            // Setup base game files
            var baseDb = new GdbPatchDatabase();
            var swordEntry = new GdbPatchEntry(WoodenSwordId, 0x01);
            swordEntry.Properties.Add(new GdbPatchProperty(HashDamageMult, GdbFieldType.Float, BitConverter.SingleToUInt32Bits(1.0f)));
            baseDb.Entries.Add(swordEntry);
            byte[] baseGdbBytes = GdbWriter.Serialize(baseDb);

            var baseBabel = new Dictionary<uint, string> { { SwordNameTag, "Vanilla Sword" } };
            byte[] baseBabelBytes = CreateMinimalBabel(baseBabel);

            var fs = new BnkFileSystem();
            fs.AddVirtualFile("globals/globals.gdb", "base", baseGdbBytes.Length, () => baseGdbBytes);
            fs.AddVirtualFile("language/en-uk/text/book.babel", "base", baseBabelBytes.Length, () => baseBabelBytes);

            Engine_GameGdb.SetResolver(vpath => fs.TryGetFile(vpath, out var f) ? f.ReadAllBytes() : null);

            // ModA
            string modAPath = Path.Combine(tempDir, "ModA.f2mod");
            var modADb = new GdbPatchDatabase();
            var entryA = new GdbPatchEntry(WoodenSwordId, 0x01);
            entryA.Properties.Add(new GdbPatchProperty(HashDamageMult, GdbFieldType.Float, BitConverter.SingleToUInt32Bits(2.0f)));
            modADb.Entries.Add(entryA);
            AssertThat(F2ModPackage.Create(modAPath, new ModMetadata { Title = "Mod A" }, new[]
            {
                ("globals/globals.gdb.tu", GdbWriter.Serialize(modADb)),
                ("language/en-uk/text/book.babel.csv", Encoding.UTF8.GetBytes($"0x{SwordNameTag:X8},Mod A Sword\n"))
            }, out _)).IsTrue();

            // ModB
            string modBPath = Path.Combine(tempDir, "ModB.f2mod");
            var modBDb = new GdbPatchDatabase();
            var entryB = new GdbPatchEntry(WoodenSwordId, 0x01);
            entryB.Properties.Add(new GdbPatchProperty(HashDamageMult, GdbFieldType.Float, BitConverter.SingleToUInt32Bits(3.0f)));
            modBDb.Entries.Add(entryB);
            AssertThat(F2ModPackage.Create(modBPath, new ModMetadata { Title = "Mod B" }, new[]
            {
                ("globals/globals.gdb.tu", GdbWriter.Serialize(modBDb)),
                ("language/en-uk/text/book.babel.csv", Encoding.UTF8.GetBytes($"0x{SwordNameTag:X8},Mod B Sword\n"))
            }, out _)).IsTrue();

            var order = new ModLoadOrder();
            order.Enabled.Add("ModB");
            order.Enabled.Add("ModA");

            var installs = ModWorkspace.DiscoverModInstalls(tempDir);

            // TRANSITION 1: Both mods enabled
            var overlays = ModIndex.ResolveOverlays(tempDir, order, installs);
            GdbPatchCompositor.Apply(fs, overlays, tempDir);
            ModRowOverlays.Apply(fs, overlays);

            string cacheDir = Path.Combine(tempDir, ".cache", "gdb_composite");
            AssertThat(Directory.Exists(cacheDir)).IsTrue();
            AssertThat(File.Exists(Path.Combine(cacheDir, "globals", "globals.gdb.tu"))).IsTrue();

            // TRANSITION 2: Disable ModB -> Only ModA enabled
            order.Enabled.Clear();
            order.Enabled.Add("ModA");

            var fsTransition = new BnkFileSystem();
            fsTransition.AddVirtualFile("globals/globals.gdb", "base", baseGdbBytes.Length, () => baseGdbBytes);
            fsTransition.AddVirtualFile("language/en-uk/text/book.babel", "base", baseBabelBytes.Length, () => baseBabelBytes);

            overlays = ModIndex.ResolveOverlays(tempDir, order, installs);
            GdbPatchCompositor.Apply(fsTransition, overlays, tempDir);
            ModRowOverlays.Apply(fsTransition, overlays);

            // ModA's values are now active
            AssertThat(fsTransition.TryGetFile("language/en-uk/text/book.babel", out BnkFile fTrans)).IsTrue();
            TextBank? bankTrans = TextBank.FromBytes(fTrans.ReadAllBytes());
            AssertThat(bankTrans!.TryLookup(SwordNameTag, out string nameA)).IsTrue();
            AssertThat(nameA).IsEqual("Mod A Sword");

            // TRANSITION 3: Disable all mods -> Empty enabled list
            order.Enabled.Clear();
            overlays = ModIndex.ResolveOverlays(tempDir, order, installs);

            var fsVanilla = new BnkFileSystem();
            fsVanilla.AddVirtualFile("globals/globals.gdb", "base", baseGdbBytes.Length, () => baseGdbBytes);
            fsVanilla.AddVirtualFile("language/en-uk/text/book.babel", "base", baseBabelBytes.Length, () => baseBabelBytes);

            var resultsEmpty = GdbPatchCompositor.Apply(fsVanilla, overlays, tempDir);
            AssertThat(resultsEmpty.Count).IsEqual(0);

            // Composite cache directory MUST be completely purged from disk
            AssertThat(Directory.Exists(cacheDir)).IsFalse();

            // VFS serves only original vanilla files
            AssertThat(fsVanilla.TryGetFile("globals/globals.gdb.tu", out _)).IsFalse();
            AssertThat(fsVanilla.TryGetFile("language/en-uk/text/book.babel", out BnkFile fVanilla)).IsTrue();
            TextBank? bankVanilla = TextBank.FromBytes(fVanilla.ReadAllBytes());
            AssertThat(bankVanilla!.TryLookup(SwordNameTag, out string vanillaName)).IsTrue();
            AssertThat(vanillaName).IsEqual("Vanilla Sword");
        }
        finally
        {
            Engine_GameGdb.SetResolver(null);
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [TestCase]
    public void TestLoadOrderDisabledModPositionPreservation_E2E()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "refii_feature_order_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var initialOrder = new ModLoadOrder();
            initialOrder.Enabled.AddRange(new[] { "Mod1", "Mod2", "Mod3", "Mod4" });
            AssertThat(initialOrder.Write(tempDir, out _)).IsTrue();

            // Simulate UI state: 4 installed mods
            var installedList = new List<(string Name, bool Enabled)>
            {
                ("Mod1", true),
                ("Mod2", true),
                ("Mod3", true),
                ("Mod4", true)
            };

            // 1. User toggles Mod2 off:
            // List order stays [Mod1, Mod2, Mod3, Mod4], but Mod2.Enabled = false
            installedList[1] = ("Mod2", false);

            // Active enabled order becomes [Mod1, Mod3, Mod4]
            var activeOrder = new ModLoadOrder();
            foreach (var item in installedList)
            {
                if (item.Enabled)
                {
                    activeOrder.Enabled.Add(item.Name);
                }
            }

            AssertThat(activeOrder.Enabled.Count).IsEqual(3);
            AssertThat(activeOrder.Enabled[0]).IsEqual("Mod1");
            AssertThat(activeOrder.Enabled[1]).IsEqual("Mod3");
            AssertThat(activeOrder.Enabled[2]).IsEqual("Mod4");

            // 2. User re-enables Mod2:
            // Because Mod2 stayed in slot 1 of the list, re-enabling it retains slot 1
            installedList[1] = ("Mod2", true);

            var reEnabledOrder = new ModLoadOrder();
            foreach (var item in installedList)
            {
                if (item.Enabled)
                {
                    reEnabledOrder.Enabled.Add(item.Name);
                }
            }

            AssertThat(reEnabledOrder.Enabled.Count).IsEqual(4);
            AssertThat(reEnabledOrder.Enabled[0]).IsEqual("Mod1");
            AssertThat(reEnabledOrder.Enabled[1]).IsEqual("Mod2");
            AssertThat(reEnabledOrder.Enabled[2]).IsEqual("Mod3");
            AssertThat(reEnabledOrder.Enabled[3]).IsEqual("Mod4");

            AssertThat(reEnabledOrder.Write(tempDir, out _)).IsTrue();
            var reloaded = ModLoadOrder.Read(tempDir);
            AssertThat(reloaded.Enabled.Count).IsEqual(4);
            AssertThat(reloaded.Enabled[1]).IsEqual("Mod2");
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

        WriteU32(1024);
        WriteU32(0);
        WriteU32(0);
        WriteU32(0);

        return file.ToArray();
    }
}
