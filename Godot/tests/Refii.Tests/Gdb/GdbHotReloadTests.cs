namespace Refii.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Core;
using Refii.Gdb;

/// <summary>
/// The plan a remount posts to the guest tick (<see cref="GdbHotReload.Plan"/>): a changed or new
/// composite is read again by the guest's loader, a replaced base file before its patch, rows a
/// composite states no more are reverted to the base in lines the tick can split, and nothing is
/// posted for a mount that changed no database. On the shipped <c>globals.gdb</c> too, when the
/// packaged data is present. Pure .NET: no Godot type reaches the test host.
/// </summary>
[TestSuite]
public class GdbHotReloadTests
{
    private const string Globals = "globals/globals.gdb.tu";

    private static readonly uint ParentId = Engine_GameHash.Fnv1("BaseFirearm");
    private static readonly uint ChildId = Engine_GameHash.Fnv1("ToyGunFirearm");
    private static readonly uint SiblingId = Engine_GameHash.Fnv1("FlintlockFirearm");
    private static readonly uint Range = Engine_GameHash.Fnv1("Range");

    /// <summary>A patch stating the given entries, each with one float <c>Range</c>.</summary>
    private static byte[] Patch(float range, params uint[] ids)
    {
        var db = new GdbPatchDatabase();
        foreach (uint id in ids)
        {
            var entry = new GdbPatchEntry(id, 1);
            entry.Properties.Add(new GdbPatchProperty(Range, GdbFieldType.Float, BitConverter.SingleToUInt32Bits(range)));
            db.Entries.Add(entry);
        }

        db.Names[Range] = "Range";
        return GdbWriter.Serialize(db);
    }

    private static Dictionary<string, byte[]> Set(params (string Path, byte[] Bytes)[] items)
    {
        var set = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach ((string path, byte[] bytes) in items)
        {
            set[path] = bytes;
        }

        return set;
    }

    [TestCase]
    public void RecordIdsAreTheContainersSortedIds()
    {
        byte[] patch = Patch(1f, ChildId, ParentId, SiblingId);
        List<uint> ids = GdbHotReload.RecordIds(patch).ToList();
        AssertThat(ids.Count).IsEqual(3);
        AssertThat(ids.OrderBy(i => i).SequenceEqual(ids)).IsTrue();
        AssertThat(ids.Contains(ChildId)).IsTrue();
        AssertThat(GdbHotReload.RecordIds(new byte[] { 1, 2, 3 }).Any()).IsFalse();
    }

    [TestCase]
    public void UnchangedMountPostsNothing()
    {
        byte[] patch = Patch(1f, ChildId);
        var loaded = Set((Globals, patch));
        var now = Set((Globals, (byte[])patch.Clone()));
        List<string> lines = GdbHotReload.Plan(loaded, now, new[] { "data/ui/hud.json" });
        AssertThat(lines.Count).IsEqual(0);
    }

    [TestCase]
    public void ChangedCompositeIsReloadedAndRemovedRowsReverted()
    {
        var loaded = Set((Globals, Patch(1f, ChildId, SiblingId)));
        var now = Set((Globals, Patch(2f, ChildId, ParentId)));
        List<string> lines = GdbHotReload.Plan(loaded, now, new[] { "data/" + Globals });
        AssertThat(lines.Count).IsEqual(2);
        AssertThat(lines[0]).IsEqual("gdb_reload " + Globals);
        AssertThat(lines[1]).IsEqual($"gdb_revert {Globals} 0x{SiblingId:X8}");
    }

    [TestCase]
    public void NewCompositeIsReloadedWithNoRevert()
    {
        var loaded = Set();
        var now = Set((Globals, Patch(1f, ChildId)));
        List<string> lines = GdbHotReload.Plan(loaded, now, new[] { "data/" + Globals });
        AssertThat(lines.Count).IsEqual(1);
        AssertThat(lines[0]).IsEqual("gdb_reload " + Globals);
    }

    [TestCase]
    public void VanishedCompositeRevertsEveryRowInTickSizedLines()
    {
        uint[] ids = Enumerable.Range(1, 2 * GdbHotReload.HashesPerLine + 5).Select(i => (uint)(0x1000 + i)).ToArray();
        var loaded = Set((Globals, Patch(1f, ids)));
        List<string> lines = GdbHotReload.Plan(loaded, Set(), Array.Empty<string>());
        AssertThat(lines.Count).IsEqual(3);
        foreach (string line in lines)
        {
            AssertThat(line.StartsWith("gdb_revert " + Globals + " 0x", StringComparison.Ordinal)).IsTrue();
            // The tick drains into char line[4096] and splits at 64 words (mod_commands.cpp).
            AssertThat(line.Length < 4096).IsTrue();
            AssertThat(line.Split(' ').Length <= 64).IsTrue();
        }

        int hashes = lines.Sum(l => l.Split(' ').Length - 2);
        AssertThat(hashes).IsEqual(ids.Length);
        AssertThat(lines[0].Split(' ').Length - 2).IsEqual(GdbHotReload.HashesPerLine);
    }

    [TestCase]
    public void ReplacedBaseIsReloadedBeforeItsPatch()
    {
        byte[] patch = Patch(1f, ChildId);
        var loaded = Set((Globals, patch));
        var now = Set((Globals, (byte[])patch.Clone()));
        List<string> lines = GdbHotReload.Plan(
            loaded, now, new[] { "data/globals/globals.gdb", "data/art/x.tex" });
        AssertThat(lines.Count).IsEqual(2);
        AssertThat(lines[0]).IsEqual("gdb_reload data/globals/globals.gdb");
        AssertThat(lines[1]).IsEqual("gdb_reload " + Globals);
    }

    [TestCase]
    public void AnotherDatabasesBaseDoesNotReloadThisPatch()
    {
        byte[] patch = Patch(1f, ChildId);
        var loaded = Set((Globals, patch));
        var now = Set((Globals, (byte[])patch.Clone()));
        List<string> lines = GdbHotReload.Plan(
            loaded, now, new[] { "data/globals/speechaction.gdb" });
        AssertThat(lines.Count).IsEqual(1);
        AssertThat(lines[0]).IsEqual("gdb_reload data/globals/speechaction.gdb");
    }

    [TestCase]
    public void APatchOutsideTheCompositesIsStillReloaded()
    {
        List<string> lines = GdbHotReload.Plan(
            Set(), Set(), new[] { "data/environmentthemes/environmentthemes.gdb.tu" });
        AssertThat(lines.Count).IsEqual(1);
        AssertThat(lines[0]).IsEqual("gdb_reload data/environmentthemes/environmentthemes.gdb.tu");
    }

    [TestCase]
    public void ShippedGlobalsPatchOfARealRecordPlansOneReload()
    {
        byte[]? original = GdbContainerRoundTripTests.ReadShippedGlobals();
        if (original is null)
        {
            Console.WriteLine("ShippedGlobalsPatchOfARealRecordPlansOneReload: packaged globals.gdb not present; skipped");
            return;
        }

        // A patch restating a shipped record whole, the way a composite does, with one value changed.
        AssertThat(GdbView.TryParse(original, out GdbView view, out _)).IsTrue();
        int index = Enumerable.Range(0, view.RecordCount)
            .First(i => view.RawFields(i).Any(f => f.Type == GdbFieldType.Float));
        uint id = view.RecordHash(index);
        var db = new GdbPatchDatabase();
        var entry = new GdbPatchEntry(id, view.RecordSchemaFlags(index));
        foreach ((uint hash, GdbFieldType type, uint raw) in view.RawFields(index))
        {
            uint value = type == GdbFieldType.Float ? BitConverter.SingleToUInt32Bits(123.5f) : raw;
            entry.Properties.Add(new GdbPatchProperty(hash, type, value));
            if (view.Names.TryGetValue(hash, out string? name))
            {
                db.Names[hash] = name;
            }
        }

        db.Entries.Add(entry);
        byte[] patch = GdbWriter.Serialize(db);
        AssertThat(GdbHotReload.RecordIds(patch).Single()).IsEqual(id);

        List<string> lines = GdbHotReload.Plan(Set(), Set((Globals, patch)), new[] { "data/" + Globals });
        AssertThat(lines.Count).IsEqual(1);
        AssertThat(lines[0]).IsEqual("gdb_reload " + Globals);

        // Disabling the mod again: the row goes back to the shipped record.
        lines = GdbHotReload.Plan(Set((Globals, patch)), Set(), Array.Empty<string>());
        AssertThat(lines.Count).IsEqual(1);
        AssertThat(lines[0]).IsEqual($"gdb_revert {Globals} 0x{id:X8}");
    }
}
