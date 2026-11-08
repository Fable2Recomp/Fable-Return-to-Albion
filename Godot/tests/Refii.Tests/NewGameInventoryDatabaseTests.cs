using System;
using System.Linq;
using System.Runtime.CompilerServices;
using GdUnit4;
using Refii.Core;
using Refii.Gdb;
using Refii.Titles.Fable2;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>The New Game inventory answers the same whether its database stayed in memory or was built again.</summary>
[TestSuite]
public sealed class NewGameInventoryDatabaseTests
{
    private const uint Item = 0x10000001, Augmentable = 0x10000002, Augments = 0x10000003, Entry = 0x10000004, Primary = 0x10000005, Secondary = 0x10000006;

    private static GdbPatchEntry Record(uint hash, params (string Field, uint Value)[] references)
    {
        var entry = new GdbPatchEntry(hash, 0x01);
        foreach ((string field, uint value) in references)
            entry.Properties.Add(new GdbPatchProperty(Engine_GameHash.Fnv1(field), GdbFieldType.Reference, value));
        return entry;
    }

    private static GdbMulti Database()
    {
        var db = new GdbPatchDatabase();
        db.Entries.Add(Record(Item, ("AugmentableComponent", Augmentable)));
        db.Entries.Add(Record(Augmentable, ("InitialAugmentations", Augments)));
        db.Entries.Add(Record(Augments, ("First", Entry)));
        db.Entries.Add(Record(Entry, ("PrimaryType", Primary), ("SecondaryType", Secondary)));
        return GdbMulti.Build(new byte[]?[] { GdbWriter.Serialize(db) });
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Engine_NewGameInventory Inventory(Func<GdbMulti> rebuild, bool keep) => new(Database(), rebuild, keep);

    [TestCase]
    public void KeptNeverRebuilds()
    {
        int rebuilds = 0;
        Engine_NewGameInventory inventory = Inventory(() => { rebuilds++; return Database(); }, keep: true);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        AssertThat(inventory.InitialAugments(Item).Single().Primary).IsEqual(Primary);
        AssertThat(rebuilds).IsEqual(0);
    }

    [TestCase]
    public void CollectedRebuildsOnceAndAnswersTheSame()
    {
        int rebuilds = 0;
        Engine_NewGameInventory inventory = Inventory(() => { rebuilds++; return Database(); }, keep: false);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Engine_SaveHero_Check(inventory);
        AssertThat(rebuilds).IsEqual(1);
        GC.KeepAlive(inventory);
    }

    [TestCase]
    public void AliasPatchWritesAndNamesTheRecord()
    {
        var db = new GdbPatchDatabase();
        db.Entries.Add(Record(Item, ("AugmentableComponent", Augmentable)));
        db.Entries.Add(Record(Augmentable));
        db.Aliases.Add((Engine_GameHash.Fnv1("Item"), Item));
        byte[] source = GdbWriter.Serialize(db);
        var inventory = new Engine_NewGameInventory(GdbMulti.Build(new byte[]?[] { source }), () => throw new InvalidOperationException(), keepDatabase: true);

        byte[] patch = GdbWriter.Serialize(inventory.Aliases);

        GdbMulti layered = GdbMulti.Build(new byte[]?[] { source, patch });
        AssertThat(layered.TryLookup(Engine_GameHash.Fnv1(inventory.RecordName(Item)!), out var named)).IsTrue();
        AssertThat(named.View.RecordHash(named.Record)).IsEqual(Item);
        AssertThat(layered.TryLookup(Item, out var item)).IsTrue();
        AssertThat(item.View.RawFields(item.Record).Single().Hash).IsEqual(Engine_GameHash.Fnv1("AugmentableComponent"));
    }

    /// <summary>A record's copy in the patch shadows the original, so its text fields must still resolve
    /// there: a quest's NameTag came out empty and the pause menu said "!!!&lt;id&gt;".</summary>
    [TestCase]
    public void AliasPatchCarriesTheRecordsStrings()
    {
        uint nameTag = Engine_GameHash.Fnv1("NameTag"), title = Engine_GameHash.Fnv1("TEXT_QUEST_TITLE");
        var quest = new GdbPatchEntry(Item, 0x01);
        quest.Properties.Add(new GdbPatchProperty(nameTag, GdbFieldType.String, title));
        var db = new GdbPatchDatabase();
        db.Entries.Add(quest);
        db.Aliases.Add((Engine_GameHash.Fnv1("Quest"), Item));
        db.Names[nameTag] = "NameTag";
        db.Names[title] = "TEXT_QUEST_TITLE";
        byte[] source = GdbWriter.Serialize(db);
        var inventory = new Engine_NewGameInventory(GdbMulti.Build(new byte[]?[] { source }), () => throw new InvalidOperationException(), keepDatabase: true);

        GdbMulti layered = GdbMulti.Build(new byte[]?[] { source, GdbWriter.Serialize(inventory.Aliases) });

        AssertThat(layered.TryLookup(Item, out var copy)).IsTrue();
        AssertThat(copy.View.TryFindLocal(copy.Record, nameTag, (int)GdbFieldType.String, out GdbField field)).IsTrue();
        AssertThat(field.Text).IsEqual("TEXT_QUEST_TITLE");
        AssertThat(field.Name).IsEqual("NameTag");
    }

    private static void Engine_SaveHero_Check(Engine_NewGameInventory inventory)
    {
        var augments = inventory.InitialAugments(Item);
        AssertThat(augments.Count).IsEqual(1);
        AssertThat(augments[0].Primary).IsEqual(Primary);
        AssertThat(augments[0].Secondary).IsEqual(Secondary);
        AssertThat(inventory.InitialAugments(0x7FFFFFFF).Count).IsEqual(0);
    }
}
