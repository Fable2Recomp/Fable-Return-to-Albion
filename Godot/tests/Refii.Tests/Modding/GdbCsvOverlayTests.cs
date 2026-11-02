namespace Refii.Tests.Modding;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Bnk;
using Refii.Core;
using Refii.Gdb;
using Refii.Mods;

/// <summary>
/// A mod's <c>*.gdb.csv</c> can SET an existing value, ADD a typed property, ADD an entry from a
/// parent, and set an INHERITED value on the child (materialised there, the sibling untouched) —
/// the three cases the phase-2 overlay refused. The rows compile to the same sparse patch a
/// <c>.gdb.tu</c> holds, merge with one through the compositor with conflicts reported, and the
/// composite is a container the guest's read accepts. On the shipped <c>globals.gdb</c> too, when
/// present. Pure .NET: no Godot type reaches the test host.
/// </summary>
[TestSuite]
public class GdbCsvOverlayTests
{
    private static readonly uint ParentId = Engine_GameHash.Fnv1("BaseFirearm");
    private static readonly uint ChildId = Engine_GameHash.Fnv1("ToyGunFirearm");
    private static readonly uint SiblingId = Engine_GameHash.Fnv1("FlintlockFirearm");
    private static readonly uint Range = Engine_GameHash.Fnv1("Range");
    private static readonly uint Capacity = Engine_GameHash.Fnv1("BulletCapacity");
    private static readonly uint ReloadOnlyIfEmpty = Engine_GameHash.Fnv1("ReloadOnlyIfEmpty");

    private const string Rows =
        "entry,property,value\n" +
        "# a value the entry has locally\n" +
        "ToyGunFirearm,Range,200\n" +
        "# a value it inherits: materialised on the child\n" +
        "ToyGunFirearm,BulletCapacity,10\n" +
        "# a property nothing in its chain declares: typed\n" +
        "ToyGunFirearm,MuzzleFlash,string,FX_MUZZLE\n" +
        "# an entry the database lacks, from a parent named by its alias\n" +
        "MyToyGunFirearm,parent,ToyGunFirearmAlias\n" +
        "MyToyGunFirearm,BulletCapacity,20\n" +
        "MyToyGunFirearm,ReloadOnlyIfEmpty,false\n" +
        "MyToyGunFirearm,Rarity,int,3\n";

    private static GdbMulti SyntheticBase(out byte[] bytes)
    {
        bytes = GdbWriter.Serialize(GdbContainerRoundTripTests.SyntheticDatabase());
        return GdbMulti.Build(new[] { bytes });
    }

    [TestCase]
    public void RowsSetAddAndMaterialise()
    {
        GdbMulti baseMulti = SyntheticBase(out byte[] baseBytes);
        GdbCsvResult result = GdbCsvOverlay.Parse(Rows, baseMulti);

        AssertThat(string.Join("\n", result.Errors)).IsEqual(string.Empty);
        AssertThat(result.Applied).IsEqual(7);
        AssertThat(result.Database.Entries.Count).IsEqual(2);

        GdbPatchEntry child = result.Database.Entries.Find(e => e.Id == ChildId)!;
        AssertThat(child).IsNotNull();
        AssertThat(child.Properties.Count).IsEqual(3);
        GdbPatchProperty range = child.Properties.Find(p => p.NameHash == Range)!;
        AssertThat(range.Type).IsEqual(GdbFieldType.Float);
        AssertThat(BitConverter.UInt32BitsToSingle(range.Value)).IsEqual(200f);
        GdbPatchProperty capacity = child.Properties.Find(p => p.NameHash == Capacity)!;
        AssertThat(capacity.Type).IsEqual(GdbFieldType.Int);
        AssertThat(capacity.Value).IsEqual(10u);
        uint muzzle = Engine_GameHash.Fnv1("MuzzleFlash");
        GdbPatchProperty flash = child.Properties.Find(p => p.NameHash == muzzle)!;
        AssertThat(flash.Type).IsEqual(GdbFieldType.String);
        AssertThat(flash.Value).IsEqual(Engine_GameHash.Fnv1("FX_MUZZLE"));
        AssertThat(result.Database.Names[muzzle]).IsEqual("MuzzleFlash");
        AssertThat(result.Database.Names[Engine_GameHash.Fnv1("FX_MUZZLE")]).IsEqual("FX_MUZZLE");

        uint newId = Engine_GameHash.Fnv1("MyToyGunFirearm");
        GdbPatchEntry added = result.Database.Entries.Find(e => e.Id == newId)!;
        AssertThat(added).IsNotNull();
        AssertThat(added.SchemaFlags).IsEqual((byte)1);
        AssertThat(result.Database.Names[newId]).IsEqual("MyToyGunFirearm");
        GdbPatchProperty parent = added.Properties.Find(p => p.NameHash == GdbView.HashParent)!;
        AssertThat(parent.Type).IsEqual(GdbFieldType.Reference);
        // The alias resolved to the record's own id, not the alias hash.
        AssertThat(parent.Value).IsEqual(ChildId);
        AssertThat(added.Properties.Find(p => p.NameHash == Capacity)!.Value).IsEqual(20u);
        AssertThat(added.Properties.Find(p => p.NameHash == ReloadOnlyIfEmpty)!.Type).IsEqual(GdbFieldType.Bool);
        AssertThat(added.Properties.Find(p => p.NameHash == ReloadOnlyIfEmpty)!.Value).IsEqual(0u);
        AssertThat(added.Properties.Find(p => p.NameHash == Engine_GameHash.Fnv1("Rarity"))!.Type).IsEqual(GdbFieldType.Int);

        // THE PATCH THE GUEST READS: synthesised complete, written, read back through the strict check.
        GdbPatchDatabase complete = GdbPatchBuilder.SynthesizeCompleteDatabase(result.Database, baseMulti);
        byte[] tu = GdbWriter.Serialize(complete);
        AssertThat(GdbContainerCheck.TryVerify(tu, out _, out string error)).IsTrue();
        AssertThat(error).IsEqual(string.Empty);

        // Layered on the base, the child answers 10 FROM ITSELF, the sibling still inherits 1.
        GdbMulti layered = GdbMulti.BuildWithOverlays(baseMulti, new[] { tu });
        AssertThat(layered.TryLookup(ChildId, out GdbCursor childCursor)).IsTrue();
        AssertThat(layered.TryFindInherited(childCursor, Capacity, -1, out GdbCursor owner, out uint value, out _)).IsTrue();
        AssertThat(value).IsEqual(10u);
        AssertThat(owner.View.RecordHash(owner.Record)).IsEqual(ChildId);
        AssertThat(layered.TryLookup(SiblingId, out GdbCursor siblingCursor)).IsTrue();
        AssertThat(layered.TryFindInherited(siblingCursor, Capacity, -1, out GdbCursor siblingOwner, out uint siblingValue, out _)).IsTrue();
        AssertThat(siblingValue).IsEqual(1u);
        AssertThat(siblingOwner.View.RecordHash(siblingOwner.Record)).IsEqual(ParentId);

        // The new entry is a record, inherits through its parent, and is named.
        AssertThat(layered.TryLookup(newId, out GdbCursor newCursor)).IsTrue();
        AssertThat(layered.TryFindInherited(newCursor, Range, -1, out _, out uint newRange, out _)).IsTrue();
        AssertThat(BitConverter.UInt32BitsToSingle(newRange)).IsEqual(200f);
        AssertThat(layered.NameOf(newId)).IsEqual("MyToyGunFirearm");

        // What the report says.
        var composite = new CompositeGdbResult();
        GdbPatchCompositor.Classify(result.Database, baseMulti, composite);
        // Keys carry the record's AUTHORED name, which for most shipped records is its alias.
        AssertThat(composite.AddedEntries).ContainsExactly("MyToyGunFirearm");
        AssertThat(composite.AddedProperties).ContainsExactly("ToyGunFirearmAlias.MuzzleFlash");
        AssertThat(composite.Materialised).ContainsExactly("ToyGunFirearmAlias.BulletCapacity");
        AssertThat(composite.Applied).IsEqual(7);

        RowMerge merge = RowMerge.FromComposite(composite);
        string described = ModRowOverlays.Describe(merge);
        AssertThat(described).Contains("1 entry(ies) added");
        AssertThat(described).Contains("1 property(ies) added");
        AssertThat(described).Contains("1 inherited value(s) set on the entry");
    }

    [TestCase]
    public void RowsAreRefusedWithTheReason()
    {
        GdbMulti baseMulti = SyntheticBase(out _);

        string Refusal(string row)
        {
            GdbCsvResult result = GdbCsvOverlay.Parse("entry,property,value\n" + row + "\n", baseMulti);
            AssertThat(result.Errors.Count).IsEqual(1);
            return result.Errors[0];
        }

        AssertThat(Refusal("ToyGunFirearm,Rarity,3")).Contains("states its type");
        AssertThat(Refusal("ToyGunFirearm,Range,int,3")).Contains("cannot change");
        AssertThat(Refusal("ToyGunFirearm,BulletCapacity,float,3")).Contains("cannot change");
        AssertThat(Refusal("Nowhere,Range,3")).Contains("starts with its parent row");
        AssertThat(Refusal("NewThing,parent,NoSuchParent")).Contains("neither an entry of the database");
        AssertThat(Refusal("ToyGunFirearm,@flags,2")).Contains("shipped entry");
        AssertThat(Refusal("ToyGunFirearm,ReloadOnlyIfEmpty,maybe")).Contains("true/false");
        AssertThat(Refusal("ToyGunFirearm,BulletCapacity,ten")).Contains("whole number");
        AssertThat(Refusal("ToyGunFirearm,parent,NoSuchParent")).Contains("neither an entry");
        AssertThat(Refusal("ToyGunFirearm,Range,float,one,two")).Contains("got 5 column(s)");
        AssertThat(Refusal("ToyGunFirearm,Range,blob,1")).Contains("unknown type");
        AssertThat(Refusal("ToyGunFirearm,Range,t99,1")).Contains("cannot change");

        // Set twice: the first stands, the second is refused.
        GdbCsvResult twice = GdbCsvOverlay.Parse("ToyGunFirearm,Range,1\nToyGunFirearm,Range,2\n", baseMulti);
        AssertThat(twice.Errors.Count).IsEqual(1);
        AssertThat(twice.Errors[0]).Contains("set twice");
        AssertThat(BitConverter.UInt32BitsToSingle(twice.Database.Entries[0].Properties[0].Value)).IsEqual(1f);

        // A matching stated type on an existing property is accepted (the formatter emits them).
        GdbCsvResult typed = GdbCsvOverlay.Parse("ToyGunFirearm,Range,float,7\n#1234abcd,parent,BaseFirearm\n", baseMulti);
        AssertThat(string.Join("\n", typed.Errors)).IsEqual(string.Empty);
        AssertThat(typed.Database.Entries.Count).IsEqual(2);
        AssertThat(typed.Database.Entries[1].Id).IsEqual(0x1234abcdu);

        // Without a database only the form is checked; the validator runs it this way.
        GdbCsvResult form = GdbCsvOverlay.Parse("A,B,int,x\nA,B,bool,1\nA,B\n", null);
        AssertThat(form.Errors.Count).IsEqual(2);
        AssertThat(ModValidate.KindOf("Globals/Globals.gdb.csv")).IsEqual(ModValidate.GdbRowsKind);
        AssertThat(ModValidate.KindOf("globals/globals.gdb.tu")).IsEqual(string.Empty);
        IReadOnlyList<ModIssue> issues = ModValidate.Validate("globals/globals.gdb.csv", "entry,property,value\nA,B,int,x\n");
        AssertThat(issues.Count).IsEqual(1);
        AssertThat(issues[0].Error).IsTrue();
        AssertThat(issues[0].Pointer).IsEqual("line 2");
        AssertThat(ModValidate.Validate("globals/globals.gdb.csv", Rows).Count).IsEqual(0);
    }

    [TestCase]
    public void FormatIsTheInverseOfParse()
    {
        GdbMulti baseMulti = SyntheticBase(out _);
        GdbCsvResult first = GdbCsvOverlay.Parse(Rows, baseMulti);
        AssertThat(first.Errors.Count).IsEqual(0);

        // Keys come back as the names the base knows (an alias is a valid key), typed, parent first.
        string text = GdbCsvOverlay.Format(first.Database, baseMulti);
        AssertThat(text).Contains("MyToyGunFirearm,parent,ref,ToyGunFirearmAlias\n");
        AssertThat(text).Contains("ToyGunFirearmAlias,MuzzleFlash,string,FX_MUZZLE\n");
        AssertThat(text).Contains(",Range,float,200\n");
        AssertThat(text).NotContains("@flags");

        GdbCsvResult second = GdbCsvOverlay.Parse(text, baseMulti);
        AssertThat(string.Join("\n", second.Errors)).IsEqual(string.Empty);
        AssertThat(GdbContainerCheck.SameContent(first.Database, second.Database, out string difference)).IsTrue();
        AssertThat(difference).IsEqual(string.Empty);
    }

    [TestCase]
    public void CsvAndTuMergeThroughOneCompositorWithConflicts()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "refii_gdbcsv_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            GdbMulti baseMulti = SyntheticBase(out _);

            // Mod A (lower): a Studio-made binary patch, BulletCapacity = 5 on the child.
            var dbA = new GdbPatchDatabase();
            var entryA = new GdbPatchEntry(ChildId, 1);
            entryA.Properties.Add(new GdbPatchProperty(Capacity, GdbFieldType.Int, 5));
            dbA.Entries.Add(entryA);
            dbA.Names[Capacity] = "BulletCapacity";
            string tuPath = Path.Combine(tempDir, "a.gdb.tu");
            File.WriteAllBytes(tuPath, GdbWriter.Serialize(dbA));

            // Mod B (higher): rows, BulletCapacity = 10 and a new entry.
            string csvPath = Path.Combine(tempDir, "b.gdb.csv");
            File.WriteAllText(csvPath, "ToyGunFirearm,BulletCapacity,10\nMyToyGunFirearm,parent,ToyGunFirearm\n");

            var sources = new List<GdbPatchSource>
            {
                new("ModA", tuPath, tuPath),
                new("ModB", csvPath, csvPath),
            };
            var refused = new List<string>();
            var (merged, conflicts) = GdbPatchCompositor.MergeSources("test/test.gdb.tu", sources, baseMulti, refused);

            AssertThat(refused.Count).IsEqual(0);
            AssertThat(merged.Entries.Count).IsEqual(2);
            AssertThat(merged.Entries.Find(e => e.Id == ChildId)!.Properties.Find(p => p.NameHash == Capacity)!.Value).IsEqual(10u);
            AssertThat(conflicts.Count).IsEqual(1);
            AssertThat(conflicts[0].WinnerMod).IsEqual("ModB");
            AssertThat(conflicts[0].LoserMods).ContainsExactly("ModA");
            AssertThat(conflicts[0].PropertyName).IsEqual("BulletCapacity");

            // A refused row is reported with the mod and the file, and the rest still merges.
            File.WriteAllText(csvPath, "ToyGunFirearm,BulletCapacity,10\nToyGunFirearm,Rarity,3\n");
            refused.Clear();
            (merged, _) = GdbPatchCompositor.MergeSources("test/test.gdb.tu", sources, baseMulti, refused);
            AssertThat(refused.Count).IsEqual(1);
            AssertThat(refused[0]).StartsWith("ModB: ");
            AssertThat(refused[0]).Contains("states its type");
            AssertThat(merged.Entries.Count).IsEqual(1);

            // The public 2-tuple form still works and tells a loose .csv from a .tu by its path.
            var (viaTuples, _) = GdbPatchCompositor.MergeSources(
                "test/test.gdb.tu", new List<GdbPatchSource> { new("ModB", csvPath, csvPath) }, baseMulti, new List<string>());
            AssertThat(viaTuples.Entries.Count).IsEqual(1);
            AssertThat(GdbPatchCompositor.PatchSubPathOf("globals/globals.gdb.csv")).IsEqual("globals/globals.gdb.tu");
            AssertThat(GdbPatchCompositor.PatchSubPathOf("globals/globals.gdb.tu")).IsEqual("globals/globals.gdb.tu");
            AssertThat(GdbPatchCompositor.PatchSubPathOf("globals/globals.gdb")).IsEqual(string.Empty);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    /// <summary>
    /// End to end: a loose mod with <c>test/test.gdb.csv</c>, a mount holding <c>test/test.gdb</c>,
    /// the compositor's Apply. The virtual <c>test/test.gdb.tu</c> the guest would read passes the
    /// guest's read, the rows report names what was added, and the resolved index carries it.
    /// </summary>
    [TestCase]
    public void ApplyMountsTheCompiledPatchAndReportsIt()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "refii_gdbcsv_apply_" + Guid.NewGuid().ToString("N"));
        string modDir = Path.Combine(tempDir, "ModRows");
        Directory.CreateDirectory(Path.Combine(modDir, "test"));
        Engine_GameGdb.SetBaseResolver(_ => null);
        try
        {
            SyntheticBase(out byte[] baseBytes);
            var mounted = new BnkFileSystem();
            mounted.AddVirtualFile("test/test.gdb", "base_game", baseBytes.Length, () => baseBytes);

            File.WriteAllText(Path.Combine(modDir, "test", "test.gdb.csv"), Rows);
            var overlays = new List<ModOverlay> { new() { Name = "ModRows", AssetRoot = modDir } };

            List<CompositeGdbResult> results = GdbPatchCompositor.Apply(mounted, overlays, tempDir);
            AssertThat(results.Count).IsEqual(1);
            CompositeGdbResult composite = results[0];
            AssertThat(composite.SubPath).IsEqual("test/test.gdb.tu");
            AssertThat(composite.Refused.Count).IsEqual(0);
            AssertThat(composite.ContributingMods).ContainsExactly("ModRows");
            AssertThat(composite.AddedEntries).ContainsExactly("MyToyGunFirearm");
            AssertThat(composite.AddedProperties).ContainsExactly("ToyGunFirearmAlias.MuzzleFlash");
            AssertThat(composite.Materialised).ContainsExactly("ToyGunFirearmAlias.BulletCapacity");

            AssertThat(mounted.TryGetFile("test/test.gdb.tu", out BnkFile tu)).IsTrue();
            AssertThat(tu.SourceMod).IsEqual("ModRows");
            byte[] bytes = tu.ReadAllBytes();
            AssertThat(GdbContainerCheck.TryVerify(bytes, out GdbPatchDatabase patch, out string error)).IsTrue();
            AssertThat(error).IsEqual(string.Empty);
            AssertThat(patch.Entries.Count).IsEqual(2);

            // The resolved index's rows section.
            var merges = new List<RowMerge> { RowMerge.FromComposite(composite) };
            string report = Path.Combine(tempDir, "resolved.json");
            AssertThat(ModIndex.WriteReport(report, tempDir, overlays, new List<ResolvedEntry>(), merges, out string reportError)).IsTrue();
            AssertThat(reportError).IsEqual(string.Empty);
            string json = File.ReadAllText(report);
            AssertThat(json).Contains("\"table\": \"test/test.gdb.tu\"");
            AssertThat(json).Contains("\"added_entries\"");
            AssertThat(json).Contains("MyToyGunFirearm");
            AssertThat(json).Contains("ToyGunFirearmAlias.MuzzleFlash");
            AssertThat(json).Contains("ToyGunFirearmAlias.BulletCapacity");

            // Second run: the cache is used, and the report is identical.
            List<CompositeGdbResult> again = GdbPatchCompositor.Apply(mounted, overlays, tempDir);
            AssertThat(again[0].AddedEntries).ContainsExactly("MyToyGunFirearm");
            AssertThat(again[0].Binary.Length).IsEqual(bytes.Length);
        }
        finally
        {
            Engine_GameGdb.SetBaseResolver(null);
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    /// <summary>
    /// A composite is made against the base it is stamped with. Rows compiled against a base that
    /// lacks their entries (the launcher's loose read once missed <c>Globals/globals.gdb</c>) keep
    /// only what resolved; the same mod files against the full base make it again rather than
    /// serve the cached half.
    /// </summary>
    [TestCase]
    public void ACompositeMadeAgainstAnotherBaseIsMadeAgain()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "refii_gdbcsv_base_" + Guid.NewGuid().ToString("N"));
        string modDir = Path.Combine(tempDir, "ModRows");
        Directory.CreateDirectory(Path.Combine(modDir, "test"));
        Engine_GameGdb.SetBaseResolver(_ => null);
        try
        {
            File.WriteAllText(Path.Combine(modDir, "test", "test.gdb.csv"), Rows + "Unrelated,Flag,bool,true\n");
            var overlays = new List<ModOverlay> { new() { Name = "ModRows", AssetRoot = modDir } };

            var unrelated = new GdbPatchDatabase();
            unrelated.Entries.Add(new GdbPatchEntry(Engine_GameHash.Fnv1("Unrelated"), 1));
            byte[] thin = GdbWriter.Serialize(unrelated);
            var thinMount = new BnkFileSystem();
            thinMount.AddVirtualFile("test/test.gdb", "base_game", thin.Length, () => thin);
            CompositeGdbResult partial = GdbPatchCompositor.Apply(thinMount, overlays, tempDir)[0];
            AssertThat(partial.Refused.Count > 0).IsTrue();
            AssertThat(partial.Binary.Length > 0).IsTrue();

            GdbPatchDatabase whole = GdbContainerRoundTripTests.SyntheticDatabase();
            whole.Entries.Add(new GdbPatchEntry(Engine_GameHash.Fnv1("Unrelated"), 1));
            byte[] baseBytes = GdbWriter.Serialize(whole);
            var mounted = new BnkFileSystem();
            mounted.AddVirtualFile("test/test.gdb", "base_game", baseBytes.Length, () => baseBytes);
            CompositeGdbResult full = GdbPatchCompositor.Apply(mounted, overlays, tempDir)[0];
            AssertThat(full.Refused.Count).IsEqual(0);
            AssertThat(GdbContainerCheck.TryVerify(full.Binary, out GdbPatchDatabase patch, out string error)).IsTrue();
            AssertThat(error).IsEqual(string.Empty);
            AssertThat(patch.Entries.Count).IsEqual(3);
            GdbPatchEntry child = patch.Entries.Single(e => e.Id == ChildId);
            AssertThat(child.Properties.Single(p => p.NameHash == Range).Value).IsEqual(BitConverter.SingleToUInt32Bits(200f));
        }
        finally
        {
            Engine_GameGdb.SetBaseResolver(null);
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    /// <summary>The loose base read matches each segment without regard to case, as the game's paths do.</summary>
    [TestCase]
    public void TheLooseBaseReadIgnoresCase()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "refii_gdbcase_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(tempDir, "data", "Globals"));
        try
        {
            string file = Path.Combine(tempDir, "data", "Globals", "globals.gdb");
            File.WriteAllBytes(file, new byte[] { 1 });
            // A case-blind filesystem (Windows, macOS) answers the exact path in the query's own
            // spelling; a case-sensitive one walks the segments and returns the disk's.
            StringComparison spelling = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            string found = Engine_GameGdb.FindIgnoringCase(tempDir, "data/globals/globals.gdb") ?? string.Empty;
            AssertThat(string.Equals(Path.GetFullPath(found), file, spelling)).IsTrue();
            found = Engine_GameGdb.FindIgnoringCase(Path.Combine(tempDir, "data"), "GLOBALS/Globals.GDB") ?? string.Empty;
            AssertThat(string.Equals(Path.GetFullPath(found), file, spelling)).IsTrue();
            AssertThat(Engine_GameGdb.FindIgnoringCase(tempDir, "data/globals/missing.gdb")).IsNull();
            AssertThat(Engine_GameGdb.FindIgnoringCase(tempDir, "nothere/globals.gdb")).IsNull();
            AssertThat(Engine_GameGdb.FindIgnoringCase(tempDir, "data/globals")).IsNull();
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    /// <summary>
    /// THE SHIPPED DATABASE: a new firearm component derived from the Toy Gun's (0x2FDDF213,
    /// unique to the Toy Gun per docs/GDB_INHERITANCE_AND_PER_ITEM_MODDING.md), BulletCapacity
    /// materialised on it, the Toy Gun's own component left answering the parent's value. The
    /// composite passes the guest's read. Skipped when the packaged data is absent.
    /// </summary>
    [TestCase]
    public void ShippedGlobalsTakesNewEntryAndMaterialisedProperty()
    {
        byte[]? original = GdbContainerRoundTripTests.ReadShippedGlobals();
        if (original is null)
        {
            Console.WriteLine("ShippedGlobalsTakesNewEntryAndMaterialisedProperty: packaged globals.gdb not present; skipped");
            return;
        }

        GdbMulti baseMulti = GdbMulti.Build(new[] { original });
        const uint toyGunFirearm = 0x2FDDF213;
        const uint bulletCapacity = 0xAABED971;
        AssertThat(baseMulti.TryLookup(toyGunFirearm, out GdbCursor toy)).IsTrue();
        AssertThat(baseMulti.TryFindInherited(toy, bulletCapacity, -1, out GdbCursor declaring, out uint vanilla, out GdbFieldType vanillaType)).IsTrue();
        AssertThat(declaring.View.RecordHash(declaring.Record)).IsNotEqual(toyGunFirearm);
        AssertThat(vanillaType).IsEqual(GdbFieldType.Int);

        string rows =
            "entry,property,value\n" +
            "ModFirearm,parent,#2fddf213\n" +
            "ModFirearm,BulletCapacity,12\n" +
            "#2fddf213,BulletCapacity,7\n" +
            "#2fddf213,ModOnlyFlag,bool,true\n";
        GdbCsvResult result = GdbCsvOverlay.Parse(rows, baseMulti);
        AssertThat(string.Join("\n", result.Errors)).IsEqual(string.Empty);
        AssertThat(result.Applied).IsEqual(4);

        // The property's type came from the chain, by name, without the row stating it.
        GdbPatchEntry toyPatch = result.Database.Entries.Find(e => e.Id == toyGunFirearm)!;
        AssertThat(toyPatch.Properties.Find(p => p.NameHash == bulletCapacity)!.Type).IsEqual(GdbFieldType.Int);

        GdbPatchDatabase complete = GdbPatchBuilder.SynthesizeCompleteDatabase(result.Database, baseMulti);
        byte[] tu = GdbWriter.Serialize(complete);
        AssertThat(GdbContainerCheck.TryVerify(tu, out _, out string error)).IsTrue();
        AssertThat(error).IsEqual(string.Empty);

        GdbMulti layered = GdbMulti.BuildWithOverlays(baseMulti, new[] { tu });
        uint modFirearm = Engine_GameHash.Fnv1("ModFirearm");
        AssertThat(layered.TryLookup(modFirearm, out GdbCursor added)).IsTrue();
        AssertThat(layered.TryFindInherited(added, bulletCapacity, -1, out GdbCursor owner, out uint value, out _)).IsTrue();
        AssertThat(value).IsEqual(12u);
        AssertThat(owner.View.RecordHash(owner.Record)).IsEqual(modFirearm);

        AssertThat(layered.TryLookup(toyGunFirearm, out GdbCursor toyLayered)).IsTrue();
        AssertThat(layered.TryFindInherited(toyLayered, bulletCapacity, -1, out GdbCursor toyOwner, out uint toyValue, out _)).IsTrue();
        AssertThat(toyValue).IsEqual(7u);
        AssertThat(toyOwner.View.RecordHash(toyOwner.Record)).IsEqual(toyGunFirearm);
        AssertThat(layered.TryFindInherited(toyLayered, Engine_GameHash.Fnv1("ModOnlyFlag"), -1, out _, out uint flag, out GdbFieldType flagType)).IsTrue();
        AssertThat(flagType).IsEqual(GdbFieldType.Bool);
        AssertThat(flag).IsEqual(1u);

        // The parent the Toy Gun's component derives from still answers the vanilla value.
        uint parentId = declaring.View.RecordHash(declaring.Record);
        AssertThat(layered.TryLookup(parentId, out GdbCursor parentLayered)).IsTrue();
        AssertThat(layered.TryFindInherited(parentLayered, bulletCapacity, -1, out _, out uint parentValue, out _)).IsTrue();
        AssertThat(parentValue).IsEqual(vanilla);

        var composite = new CompositeGdbResult();
        GdbPatchCompositor.Classify(result.Database, baseMulti, composite);
        AssertThat(composite.AddedEntries).ContainsExactly("ModFirearm");
        AssertThat(composite.Materialised.Count).IsEqual(1);
        AssertThat(composite.Materialised[0]).EndsWith(".BulletCapacity");
        AssertThat(composite.AddedProperties.Count).IsEqual(1);
        AssertThat(composite.AddedProperties[0]).EndsWith(".ModOnlyFlag");
    }
}
