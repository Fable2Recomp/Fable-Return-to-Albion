using System.Linq;
using GdUnit4;
using Refii.Core;
using Refii.Gdb;
using static GdUnit4.Assertions;

namespace Refii.Tests;

[TestSuite]
public class GdbOverlayAliasTests
{
    private static GdbPatchEntry Entry(uint id)
    {
        var entry = new GdbPatchEntry(id, 0x01);
        entry.Properties.Add(new GdbPatchProperty(Engine_GameHash.Fnv1("Value"), GdbFieldType.Reference, id));
        return entry;
    }

    [TestCase]
    public void TestAnOverlaysAliasReplacesTheBasesAndANewOneIsAdded()
    {
        uint a = Engine_GameHash.Fnv1("A"), b = Engine_GameHash.Fnv1("B"), c = Engine_GameHash.Fnv1("C");
        var baseDb = new GdbPatchDatabase();
        foreach (uint id in new uint[] { 1, 2 }) baseDb.Entries.Add(Entry(id));
        baseDb.Aliases.Add((a, 1));
        baseDb.Aliases.Add((b, 2));
        var overlay = new GdbPatchDatabase();
        foreach (uint id in new uint[] { 2, 3 }) overlay.Entries.Add(Entry(id));
        overlay.Aliases.Add((b, 3));
        overlay.Aliases.Add((c, 2));

        byte[] merged = GdbPatchBuilder.ApplyOverlayToBaseGdb(GdbWriter.Serialize(baseDb), GdbWriter.Serialize(overlay));
        var result = GdbPatchBuilder.LoadOrCreatePatchDatabase(merged);

        // The file keeps aliases by hash, so only the pairs are compared.
        AssertThat(result.Aliases.OrderBy(pair => pair.AliasHash).ToArray())
            .ContainsExactly(new[] { (a, 1u), (b, 3u), (c, 2u) }.OrderBy(pair => pair.Item1).ToArray());
        AssertThat(result.Entries.Select(entry => entry.Id).Order().ToArray()).ContainsExactly(1u, 2u, 3u);
    }

    [TestCase]
    public void TestManyAliasesMergeAtOnce()
    {
        // New Game's carry names: tens of thousands of them over globals.gdb.tu's own.
        var baseDb = new GdbPatchDatabase();
        var overlay = new GdbPatchDatabase();
        for (uint id = 1; id <= 20000; id++)
        {
            baseDb.Entries.Add(Entry(id));
            baseDb.Aliases.Add((Engine_GameHash.Fnv1("base" + id), id));
            overlay.Entries.Add(Entry(id));
            overlay.Aliases.Add((Engine_GameHash.Fnv1("carry" + id), id));
        }

        var timer = System.Diagnostics.Stopwatch.StartNew();
        byte[] merged = GdbPatchBuilder.ApplyOverlayToBaseGdb(GdbWriter.Serialize(baseDb), GdbWriter.Serialize(overlay));
        AssertThat(GdbPatchBuilder.LoadOrCreatePatchDatabase(merged).Aliases.Count).IsEqual(40000);
        AssertThat(timer.ElapsedMilliseconds).IsLess(5000);
    }
}
