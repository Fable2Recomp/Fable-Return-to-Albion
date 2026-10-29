namespace Refii.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Core;
using Refii.Gdb;

/// <summary>
/// THE WRITER IS THE INVERSE OF THE PARSER. Every container <see cref="GdbWriter"/> emits is read
/// back as the guest reads (<see cref="GdbContainerCheck"/>: through the interpolation deltas, the
/// declaration indices, the sorted id, schema and alias tables, the name hashes) and compared
/// with what was asked for — on a synthetic database here, and on the shipped
/// <c>globals.gdb</c> when the packaged data is present (read-only; nothing is written beside it).
/// Pure .NET: no Godot type reaches the test host.
/// </summary>
[TestSuite]
public class GdbContainerRoundTripTests
{
    /// <summary>The installed package's copy: its title folder, or the flat layout it had before.</summary>
    private static readonly string[] ShippedGlobals =
    {
        "out/package/Fable 2 Return to Albion/assets/Fable2/game/data/Globals/globals.gdb",
        "out/package/Fable 2 Return to Albion/assets/game/data/Globals/globals.gdb",
    };

    internal static string? RepoRoot()
    {
        string? at = AppContext.BaseDirectory;
        for (int i = 0; i < 12 && at != null; i++)
        {
            if (File.Exists(Path.Combine(at, "Fable2", "src", "vfs", "include", "core", "refii_mod_events.h")))
            {
                return at;
            }

            at = Path.GetDirectoryName(at);
        }

        return null;
    }

    internal static byte[]? ReadShippedGlobals()
    {
        string? root = RepoRoot();
        if (root is null)
        {
            return null;
        }

        foreach (string shipped in ShippedGlobals)
        {
            string path = Path.Combine(root, shipped.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path))
            {
                return File.ReadAllBytes(path);
            }
        }

        return null;
    }

    /// <summary>A parent, two children (one overriding the parent locally), aliases and names of every type.</summary>
    internal static GdbPatchDatabase SyntheticDatabase()
    {
        var db = new GdbPatchDatabase();
        uint parentId = Engine_GameHash.Fnv1("BaseFirearm");
        uint childId = Engine_GameHash.Fnv1("ToyGunFirearm");
        uint siblingId = Engine_GameHash.Fnv1("FlintlockFirearm");
        uint range = Engine_GameHash.Fnv1("Range");
        uint capacity = Engine_GameHash.Fnv1("BulletCapacity");
        uint reloadOnlyIfEmpty = Engine_GameHash.Fnv1("ReloadOnlyIfEmpty");
        uint nameTag = Engine_GameHash.Fnv1("NameTag");
        uint weaponType = Engine_GameHash.Fnv1("WeaponType");
        uint model = Engine_GameHash.Fnv1("Model");
        uint tagValue = Engine_GameHash.Fnv1("TEXT_TOY_GUN");
        uint enumValue = Engine_GameHash.Fnv1("Pistol");

        var parent = new GdbPatchEntry(parentId, 1);
        parent.Properties.Add(new GdbPatchProperty(range, GdbFieldType.Float, BitConverter.SingleToUInt32Bits(12.5f)));
        parent.Properties.Add(new GdbPatchProperty(capacity, GdbFieldType.Int, 1));
        parent.Properties.Add(new GdbPatchProperty(reloadOnlyIfEmpty, GdbFieldType.Bool, 1));
        parent.Properties.Add(new GdbPatchProperty(weaponType, GdbFieldType.Enum, enumValue));
        parent.Properties.Add(new GdbPatchProperty(model, GdbFieldType.ResourceReference, 0x0BADF00D));
        db.Entries.Add(parent);

        var child = new GdbPatchEntry(childId, 1);
        child.Properties.Add(new GdbPatchProperty(GdbView.HashParent, GdbFieldType.Reference, parentId));
        child.Properties.Add(new GdbPatchProperty(range, GdbFieldType.Float, BitConverter.SingleToUInt32Bits(150f)));
        child.Properties.Add(new GdbPatchProperty(nameTag, GdbFieldType.String, tagValue));
        db.Entries.Add(child);

        var sibling = new GdbPatchEntry(siblingId, 1);
        sibling.Properties.Add(new GdbPatchProperty(GdbView.HashParent, GdbFieldType.Reference, parentId));
        db.Entries.Add(sibling);

        foreach ((uint hash, string text) in new[]
        {
            (parentId, "BaseFirearm"), (childId, "ToyGunFirearm"), (siblingId, "FlintlockFirearm"),
            (range, "Range"), (capacity, "BulletCapacity"), (reloadOnlyIfEmpty, "ReloadOnlyIfEmpty"),
            (nameTag, "NameTag"), (weaponType, "WeaponType"), (model, "Model"),
            (tagValue, "TEXT_TOY_GUN"), (enumValue, "Pistol"), (GdbView.HashParent, "parent"),
        })
        {
            db.Names[hash] = text;
        }

        // An alias whose hash is NOT the entry id, as the shipped tables have: the by-name bridge.
        db.Aliases.Add((Engine_GameHash.Fnv1("ToyGunFirearmAlias"), childId));
        db.Names[Engine_GameHash.Fnv1("ToyGunFirearmAlias")] = "ToyGunFirearmAlias";
        return db;
    }

    [TestCase]
    public void SyntheticDatabaseRoundTripsThroughTheGuestRead()
    {
        GdbPatchDatabase db = SyntheticDatabase();
        byte[] bytes = GdbWriter.Serialize(db);

        AssertThat(GdbContainerCheck.TryVerify(bytes, out GdbPatchDatabase back, out string error)).IsTrue();
        AssertThat(error).IsEqual(string.Empty);
        AssertThat(GdbContainerCheck.SameContent(db, back, out string difference)).IsTrue();
        AssertThat(difference).IsEqual(string.Empty);

        // The lenient reader agrees, alias lookup included.
        AssertThat(GdbView.TryParse(bytes, out GdbView view, out _)).IsTrue();
        AssertThat(view.RecordCount).IsEqual(3);
        AssertThat(view.TryLookup(Engine_GameHash.Fnv1("ToyGunFirearmAlias"), out int index)).IsTrue();
        AssertThat(view.RecordHash(index)).IsEqual(Engine_GameHash.Fnv1("ToyGunFirearm"));
        AssertThat(view.RecordName(index)).IsEqual("ToyGunFirearmAlias");

        // Odd and even entry counts both pad correctly: three entries above, add a fourth.
        db.Entries.Add(new GdbPatchEntry(0xFFFFFFF0, 1));
        byte[] even = GdbWriter.Serialize(db);
        AssertThat(GdbContainerCheck.TryVerify(even, out GdbPatchDatabase evenBack, out string evenError)).IsTrue();
        AssertThat(evenError).IsEqual(string.Empty);
        AssertThat(evenBack.Entries.Count).IsEqual(4);

        // Empty is a valid container too (a patch with nothing in it).
        byte[] empty = GdbWriter.Serialize(new GdbPatchDatabase());
        AssertThat(GdbContainerCheck.TryVerify(empty, out _, out string emptyError)).IsTrue();
        AssertThat(emptyError).IsEqual(string.Empty);
    }

    [TestCase]
    public void WriterRefusesWhatTheGuestWouldRefuse()
    {
        // A property declared twice is PRESERVED, in declaration order: 391 schemas of the shipped
        // globals.gdb repeat a hash (entry 0x000CAC77 among them), so the guest takes it and the
        // writer keeps it.
        var twice = new GdbPatchDatabase();
        var entry = new GdbPatchEntry(0x10, 1);
        entry.Properties.Add(new GdbPatchProperty(0xAAAA, GdbFieldType.Int, 1));
        entry.Properties.Add(new GdbPatchProperty(0xAAAA, GdbFieldType.Int, 2));
        twice.Entries.Add(entry);
        AssertThat(GdbContainerCheck.TryVerify(GdbWriter.Serialize(twice), out GdbPatchDatabase twiceBack, out _)).IsTrue();
        AssertThat(GdbContainerCheck.SameContent(twice, twiceBack, out _)).IsTrue();

        // A name filed under a hash that is not its FNV-1 would make the guest refuse the whole
        // table; the writer files it under its own hash, and when the key was an entry id, adds
        // the alias pair that names the entry — what the caller meant.
        var misfiled = new GdbPatchDatabase();
        misfiled.Entries.Add(new GdbPatchEntry(0x10, 1));
        misfiled.Names[0x12345678] = "NotItsHash";
        misfiled.Names[0x10] = "TheEntry";
        AssertThat(GdbContainerCheck.TryVerify(GdbWriter.Serialize(misfiled), out GdbPatchDatabase refiled, out _)).IsTrue();
        AssertThat(refiled.Names.ContainsKey(0x12345678)).IsFalse();
        AssertThat(refiled.Names[Engine_GameHash.Fnv1("NotItsHash")]).IsEqual("NotItsHash");
        AssertThat(refiled.Names[Engine_GameHash.Fnv1("TheEntry")]).IsEqual("TheEntry");
        AssertThat(refiled.Aliases).ContainsExactly((Engine_GameHash.Fnv1("TheEntry"), 0x10u));
        AssertThat(GdbView.TryParse(GdbWriter.Serialize(misfiled), out GdbView named, out _)).IsTrue();
        AssertThat(named.RecordName(0)).IsEqual("TheEntry");

        // An alias naming two entries is ambiguous to a binary search.
        var ambiguous = new GdbPatchDatabase();
        ambiguous.Entries.Add(new GdbPatchEntry(0x10, 1));
        ambiguous.Entries.Add(new GdbPatchEntry(0x20, 1));
        ambiguous.Aliases.Add((0x5, 0x10));
        ambiguous.Aliases.Add((0x5, 0x20));
        AssertThrown(() => GdbWriter.Serialize(ambiguous)).IsInstanceOf<InvalidOperationException>();

        // An alias naming an entry the container lacks.
        var dangling = new GdbPatchDatabase();
        dangling.Entries.Add(new GdbPatchEntry(0x10, 1));
        dangling.Aliases.Add((0x5, 0x99));
        AssertThrown(() => GdbWriter.Serialize(dangling)).IsInstanceOf<InvalidOperationException>();

        // Duplicate entry ids.
        var duplicate = new GdbPatchDatabase();
        duplicate.Entries.Add(new GdbPatchEntry(0x10, 1));
        duplicate.Entries.Add(new GdbPatchEntry(0x10, 1));
        AssertThrown(() => GdbWriter.Serialize(duplicate)).IsInstanceOf<InvalidOperationException>();
    }

    [TestCase]
    public void CheckRejectsBrokenContainers()
    {
        byte[] good = GdbWriter.Serialize(SyntheticDatabase());

        // Flip the reserved word.
        byte[] reserved = (byte[])good.Clone();
        reserved[23] = 1;
        AssertThat(GdbContainerCheck.TryVerify(reserved, out _, out string reservedError)).IsFalse();
        AssertThat(reservedError).Contains("reserved");

        // Break a delta so a record lands outside the block.
        AssertThat(GdbView.TryParse(good, out GdbView view, out _)).IsTrue();
        byte[] delta = (byte[])good.Clone();
        int deltas = view.OffsetBase;
        delta[deltas] = 0x7F;
        delta[deltas + 1] = 0xFF;
        AssertThat(GdbContainerCheck.TryVerify(delta, out _, out string deltaError)).IsFalse();
        AssertThat(deltaError).Contains("delta");

        // Swap two ids so they are no longer ascending.
        byte[] ids = (byte[])good.Clone();
        for (int i = 0; i < 4; i++)
        {
            (ids[view.HashBase + i], ids[view.HashBase + 4 + i]) = (ids[view.HashBase + 4 + i], ids[view.HashBase + i]);
        }

        AssertThat(GdbContainerCheck.TryVerify(ids, out _, out string idsError)).IsFalse();
        AssertThat(idsError).Contains("ascending");

        // Swap two schema hashes so a schema is no longer sorted.
        byte[] schema = (byte[])good.Clone();
        int firstSchema = view.SchemaBase + 4;
        for (int i = 0; i < 4; i++)
        {
            (schema[firstSchema + i], schema[firstSchema + 4 + i]) = (schema[firstSchema + 4 + i], schema[firstSchema + i]);
        }

        AssertThat(GdbContainerCheck.TryVerify(schema, out _, out string schemaError)).IsFalse();
        AssertThat(schemaError).Contains("sorted");

        // Corrupt a name's text so its hash no longer matches.
        byte[] name = (byte[])good.Clone();
        int tail = view.AliasBase + (8 * view.AliasCount);
        name[tail + 12 + 4] ^= 0x01;
        AssertThat(GdbContainerCheck.TryVerify(name, out _, out string nameError)).IsFalse();
        AssertThat(nameError).Contains("hashes to");
    }

    /// <summary>
    /// THE SHIPPED FILE. Lionhead's own container satisfies every invariant the check asks for —
    /// which is the proof that the invariant set is the right one — and re-emitting it through the
    /// writer gives back every record, property, alias and name, with the same counts and the same
    /// records block size (records are laid out in id order with fixed sizes, so the deltas are
    /// reproduced exactly). Skipped, with a line to the console, when the packaged data is absent.
    /// </summary>
    [TestCase]
    public void ShippedGlobalsRoundTrips()
    {
        byte[]? original = ReadShippedGlobals();
        if (original is null)
        {
            Console.WriteLine("ShippedGlobalsRoundTrips: packaged globals.gdb not present; skipped");
            return;
        }

        bool verified = GdbContainerCheck.TryVerify(original, out GdbPatchDatabase shipped, out string error);
        AssertThat(error).IsEqual(string.Empty);
        AssertThat(verified).IsTrue();
        AssertThat(shipped.Entries.Count).IsEqual(71847);
        AssertThat(shipped.Aliases.Count).IsEqual(9993);
        AssertThat(shipped.Names.Count).IsEqual(19595);

        byte[] rewritten = GdbWriter.Serialize(shipped);
        AssertThat(GdbContainerCheck.TryVerify(rewritten, out GdbPatchDatabase back, out string backError)).IsTrue();
        AssertThat(backError).IsEqual(string.Empty);
        AssertThat(GdbContainerCheck.SameContent(shipped, back, out string difference)).IsTrue();
        AssertThat(difference).IsEqual(string.Empty);

        // Same header counts and sizes: the schemas block is the same size (every schema re-emits
        // byte for byte, below; only the block's order is the writer's first-use order), the
        // records block the same bytes except the schema offsets they carry.
        for (int at = 4; at < 20; at++)
        {
            AssertThat(rewritten[at]).IsEqual(original[at]);
        }

        AssertThat(rewritten.Length).IsEqual(original.Length);

        // THE GUEST'S DESCRIPTOR CONVENTION, on Lionhead's bytes: every record's schema — count,
        // flags, sorted hashes and all descriptors — comes back identical, and the Toy Gun
        // (0x2FDDF213) declares `parent` first, as the guest's enumeration assumes.
        int toyGunIndex = shipped.Entries.FindIndex(e => e.Id == 0x2FDDF213);
        AssertThat(toyGunIndex).IsGreaterEqual(0);
        AssertThat(shipped.Entries[toyGunIndex].Properties[0].NameHash).IsEqual(GdbView.HashParent);
        AssertThat(SchemasPerRecord(original)).IsEqual(SchemasPerRecord(rewritten));

        // THE NAME TAIL, byte for byte: pairs in the same order, the registry capacity 0x10000 and
        // all 19 595 offsets in the registry's slot order (GdbNameRegistry).
        int tail = TailStart(original);
        AssertThat(TailStart(rewritten)).IsEqual(tail);
        AssertThat(original[tail..]).IsEqual(rewritten[tail..]);

        // The lenient reader sees the same records and names through the rewritten bytes.
        AssertThat(GdbView.TryParse(rewritten, out GdbView view, out _)).IsTrue();
        AssertThat(view.RecordCount).IsEqual(71847);
        AssertThat(view.Names.Count).IsEqual(19595);
        AssertThat(view.TryLookup(0x2FDDF213, out int toyGun)).IsTrue();
        AssertThat(view.TryFindLocal(toyGun, GdbView.HashParent, 6, out GdbField parent)).IsTrue();
        AssertThat(parent.Raw).IsEqual(0xEF0BF7F4u);
    }

    private static uint Be32(byte[] data, long at) =>
        ((uint)data[at] << 24) | ((uint)data[at + 1] << 16) | ((uint)data[at + 2] << 8) | data[at + 3];

    private static int TailStart(byte[] data)
    {
        long count = Be32(data, 4);
        long records = Be32(data, 8);
        long schemas = Be32(data, 12);
        long aliases = Be32(data, 16);
        return (int)(GdbView.HeaderSize + records + schemas + (2 * ((3 * count) + (count & 1) + (4 * aliases))));
    }

    /// <summary>Each record's schema bytes (count word, hashes, descriptors), in record order.</summary>
    private static string SchemasPerRecord(byte[] data)
    {
        long count = Be32(data, 4);
        long recordsSize = Be32(data, 8);
        long records = GdbView.HeaderSize;
        long schemas = records + recordsSize;
        long ids = schemas + Be32(data, 12);
        long deltas = ids + (4 * count);
        long stride = count != 0 ? (recordsSize << 8) / count : 0;
        var text = new System.Text.StringBuilder();
        for (long i = 0; i < count; i++)
        {
            short delta = (short)((data[deltas + (2 * i)] << 8) | data[deltas + (2 * i) + 1]);
            long record = records + (4 * (delta + ((stride * i) >> 10)));
            long schema = schemas + Be32(data, record);
            long properties = Be32(data, schema) >> 8;
            text.Append(Convert.ToHexString(data, (int)schema, (int)(4 + (8 * properties))));
            text.Append('\n');
        }

        return text.ToString();
    }
}
