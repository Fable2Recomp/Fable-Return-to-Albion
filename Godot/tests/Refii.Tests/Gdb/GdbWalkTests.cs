namespace Refii.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Animations;
using Refii.Gdb;

/// <summary>
/// The cheaper database walks answer what the expensive ones did: lazily decoded names, the search
/// that builds no field list, the index that keeps layers only where there are several, and the
/// views and indexes shared between databases over equal files.
/// </summary>
[TestSuite]
public class GdbWalkTests
{
    private const uint HashA = 0x11110001, HashB = 0x11110002, HashC = 0x11110003;
    private const uint FieldInt = 0x22220001, FieldFloat = 0x22220002, FieldRef = 0x22220003;

    private static GdbPatchEntry Entry(uint id, uint parent = 0, params (uint Hash, GdbFieldType Type, uint Raw)[] fields)
    {
        var entry = new GdbPatchEntry(id, 0x01);
        foreach ((uint hash, GdbFieldType type, uint raw) in fields)
        {
            entry.Properties.Add(new GdbPatchProperty(hash, type, raw));
        }

        if (parent != 0)
        {
            entry.Properties.Add(new GdbPatchProperty(GdbView.HashParent, GdbFieldType.Reference, parent));
        }

        return entry;
    }

    private static byte[] Blob(params GdbPatchEntry[] entries)
    {
        var db = new GdbPatchDatabase();
        db.Entries.AddRange(entries);
        return GdbWriter.Serialize(db);
    }

    [TestCase]
    public void NamesAreTheSameDecodedLaterAndFromSeveralThreads()
    {
        uint named = Refii.Core.Engine_GameHash.Fnv1("FieldInt");
        var db = new GdbPatchDatabase();
        db.Entries.Add(Entry(HashA, 0, (named, GdbFieldType.Int, 7), (FieldRef, GdbFieldType.Reference, HashA)));
        db.Names[named] = "FieldInt";
        db.Names[HashA] = "RecordA";
        byte[] bytes = GdbWriter.Serialize(db);

        AssertThat(GdbView.TryParse(bytes, out GdbView view, out _)).IsTrue();

        IReadOnlyDictionary<uint, string>[] seen = Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => view.Names))).Result;
        foreach (IReadOnlyDictionary<uint, string> names in seen)
        {
            AssertThat(ReferenceEquals(names, seen[0])).IsTrue();
        }

        AssertThat(view.Names[named]).IsEqual("FieldInt");
        AssertThat(view.RecordName(0)).IsEqual("RecordA");

        GdbField intField = view.Fields(0).First(f => f.Hash == named);
        AssertThat(intField.Name).IsEqual("FieldInt");
        GdbField refField = view.Fields(0).First(f => f.Hash == FieldRef);
        AssertThat(refField.Text).IsEqual("RecordA");
    }

    /// <summary>The search stands in for a loop over the field list, so it must match that loop.</summary>
    [TestCase]
    public void FindFirstAnswersWhatASearchOfFieldsAnswers()
    {
        var db = new GdbPatchDatabase();
        db.Entries.Add(Entry(HashA, HashB, (FieldInt, GdbFieldType.Int, 7), (FieldFloat, GdbFieldType.Float, 0x3F800000)));
        db.Entries.Add(Entry(HashB, 0, (FieldRef, GdbFieldType.Reference, HashA)));
        db.Names[HashA] = "RecordA";
        AssertThat(GdbView.TryParse(GdbWriter.Serialize(db), out GdbView view, out _)).IsTrue();

        uint[] hashes = { FieldInt, FieldFloat, FieldRef, GdbView.HashParent, 0xABCDEF01 };
        int[] types = { -1, (int)GdbFieldType.Int, (int)GdbFieldType.Float, (int)GdbFieldType.Reference };
        for (int record = 0; record < view.RecordCount; record++)
        {
            foreach (uint hash in hashes)
            {
                foreach (int type in types)
                {
                    GdbField expected = default;
                    bool expectedFound = false;
                    foreach (GdbField field in view.Fields(record))
                    {
                        if (field.Hash == hash && (type < 0 || (int)field.Type == type))
                        {
                            expected = field;
                            expectedFound = true;
                            break;
                        }
                    }

                    AssertThat(view.TryFindFirst(record, hash, type, out GdbField found)).IsEqual(expectedFound);
                    AssertThat(found.Hash).IsEqual(expected.Hash);
                    AssertThat(found.Raw).IsEqual(expected.Raw);
                    AssertThat(found.Type).IsEqual(expected.Type);
                    AssertThat(found.Name).IsEqual(expected.Name);
                    AssertThat(found.Text).IsEqual(expected.Text);
                }
            }
        }
    }

    [TestCase]
    public void InheritedLookupWalksPastTheStackGuardAndStopsAtALoop()
    {
        // A chain longer than the guard's stack space, with the value at its far end.
        var entries = new List<GdbPatchEntry>();
        const uint first = 0x33330000;
        const int length = 80;
        for (uint i = 0; i < length; i++)
        {
            entries.Add(i == length - 1
                ? Entry(first + i, 0, (FieldInt, GdbFieldType.Int, 41))
                : Entry(first + i, first + i + 1));
        }

        // And a loop with nothing in it.
        entries.Add(Entry(HashA, HashB));
        entries.Add(Entry(HashB, HashA));

        GdbMulti multi = GdbMulti.Build(new byte[]?[] { Blob(entries.ToArray()) });

        AssertThat(multi.TryLookup(first, out GdbCursor start)).IsTrue();
        AssertThat(multi.TryFindInherited(start, FieldInt, -1, out GdbCursor owner, out uint value, out _)).IsTrue();
        AssertThat(value).IsEqual(41u);
        AssertThat(owner.View.RecordHash(owner.Record)).IsEqual(first + length - 1);

        AssertThat(multi.TryLookup(HashA, out GdbCursor looped)).IsTrue();
        AssertThat(multi.TryFindInherited(looped, FieldInt, -1, out _, out _, out _)).IsFalse();
    }

    [TestCase]
    public void LayersStayInOrderForRecordsSeveralViewsState()
    {
        byte[] bottom = Blob(
            Entry(HashA, HashC, (FieldInt, GdbFieldType.Int, 1)),
            Entry(HashC, 0, (FieldFloat, GdbFieldType.Float, 5)));
        byte[] middle = Blob(Entry(HashA, 0, (FieldRef, GdbFieldType.Reference, HashC)));
        byte[] top = Blob(Entry(HashA, 0, (FieldInt, GdbFieldType.Int, 3)), Entry(HashB, 0, (FieldInt, GdbFieldType.Int, 9)));
        GdbMulti multi = GdbMulti.Build(new byte[]?[] { bottom, middle, top });

        AssertThat(multi.ViewIndexOf(HashA)).IsEqual(2);
        AssertThat(multi.ViewIndexOf(HashC)).IsEqual(0);
        AssertThat(multi.ViewIndexOf(HashB)).IsEqual(2);

        // The top layer wins, a lower layer answers what it does not state, and the parent comes
        // from the first layer down that names one.
        AssertThat(multi.TryLookup(HashA, out GdbCursor a)).IsTrue();
        AssertThat(multi.TryFindInherited(a, FieldInt, -1, out _, out uint fromTop, out _)).IsTrue();
        AssertThat(fromTop).IsEqual(3u);
        AssertThat(multi.TryFindInherited(a, FieldRef, -1, out GdbCursor owner, out uint fromMiddle, out _)).IsTrue();
        AssertThat(fromMiddle).IsEqual(HashC);
        AssertThat(ReferenceEquals(owner.View, multi.Views[1])).IsTrue();
        AssertThat(multi.TryFindInherited(a, FieldFloat, -1, out _, out uint fromParent, out _)).IsTrue();
        AssertThat(fromParent).IsEqual(5u);

        // A cursor on an alias behaves as a one-layer record does.
        var aliased = new GdbPatchDatabase();
        aliased.Entries.Add(Entry(HashC, 0, (FieldInt, GdbFieldType.Int, 11)));
        aliased.Aliases.Add((0x44440001, HashC));
        byte[] aliasBlob = GdbWriter.Serialize(aliased);
        byte[] child = Blob(Entry(HashB, 0x44440001));
        GdbMulti withAlias = GdbMulti.Build(new byte[]?[] { aliasBlob, child });
        AssertThat(withAlias.TryLookup(HashB, out GdbCursor b)).IsTrue();
        AssertThat(withAlias.TryFindInherited(b, FieldInt, -1, out _, out uint throughAlias, out _)).IsTrue();
        AssertThat(throughAlias).IsEqual(11u);
    }

    [TestCase]
    public void EqualFilesShareAViewAndAnIndexButNotADatabase()
    {
        byte[] file = Blob(Entry(0x55550001, 0, (FieldInt, GdbFieldType.Int, 1)));
        byte[] same = (byte[])file.Clone();
        byte[] other = Blob(Entry(0x55550001, 0, (FieldInt, GdbFieldType.Int, 2)));
        AssertThat(other.Length).IsEqual(file.Length);

        AssertThat(GdbView.TryParseShared(file, out GdbView a, out _)).IsTrue();
        AssertThat(GdbView.TryParseShared(same, out GdbView b, out _)).IsTrue();
        AssertThat(ReferenceEquals(a, b)).IsTrue();

        // GdbView.TryParse stays a fresh parse.
        AssertThat(GdbView.TryParse(file, out GdbView fresh, out _)).IsTrue();
        AssertThat(ReferenceEquals(a, fresh)).IsFalse();

        // Callers tell databases apart by reference, so a second build is a second database.
        GdbMulti one = GdbMulti.Build(new byte[]?[] { file });
        GdbMulti two = GdbMulti.Build(new byte[]?[] { (byte[])file.Clone() });
        AssertThat(ReferenceEquals(one, two)).IsFalse();
        AssertThat(ReferenceEquals(one.Views, two.Views)).IsTrue();
        AssertThat(two.TryLookup(0x55550001, out GdbCursor cursor)).IsTrue();
        AssertThat(ReferenceEquals(cursor.View, one.Views[0])).IsTrue();

        // Bytes that differ, length alike, are another database.
        AssertThat(GdbView.TryParseShared(other, out GdbView c, out _)).IsTrue();
        AssertThat(ReferenceEquals(a, c)).IsFalse();
        AssertThat(c.TryFindLocalRaw(0, FieldInt, -1, out uint raw, out _)).IsTrue();
        AssertThat(raw).IsEqual(2u);
    }

    /// <summary>One file twice in a database is two layers, as it was when every parse was fresh.</summary>
    [TestCase]
    public void AFileListedTwiceStaysTwoLayers()
    {
        byte[] file = Blob(Entry(0x66660001, 0, (FieldInt, GdbFieldType.Int, 1)));
        GdbMulti multi = GdbMulti.Build(new byte[]?[] { file, (byte[])file.Clone(), file });

        AssertThat(multi.Views.Count).IsEqual(3);
        AssertThat(multi.Views.Distinct().Count()).IsEqual(3);
        AssertThat(multi.ViewIndexOf(0x66660001)).IsEqual(2);

        GdbMulti overlaid = GdbMulti.BuildWithOverlays(multi, new byte[]?[] { file });
        AssertThat(overlaid.Views.Count).IsEqual(4);
        AssertThat(overlaid.ViewIndexOf(0x66660001)).IsEqual(3);
    }

    /// <summary>What one database's scan remembers must not reach the next, shorter or longer.</summary>
    [TestCase]
    public void ScanFindsTheSameBindingsWhateverWasScannedBefore()
    {
        const uint model = 0x77770001, skeleton = 0x77770002, clipKey = 0x77770003, otherKey = 0x77770004;
        const uint record = 0x77770010, component = 0x77770011, parent = 0x77770012, group = 0x77770013, member = 0x77770014;
        uint slash = Refii.Core.Engine_GameHash.Fnv1("Slash");

        byte[] Db(uint key)
        {
            var db = new GdbPatchDatabase();
            db.Entries.Add(Entry(parent, 0, (Engine_AnimGdbHashes.SkeletonFile, GdbFieldType.String, skeleton)));
            db.Entries.Add(Entry(record, parent,
                (Engine_AnimGdbHashes.GraphicAppearanceAnimatedMeshComponent, GdbFieldType.Reference, component),
                (Engine_AnimGdbHashes.Animation, GdbFieldType.String, key),
                (Engine_AnimGdbHashes.AnimationList, GdbFieldType.Reference, group)));
            db.Entries.Add(Entry(component, 0, (Engine_AnimGdbHashes.ModelFile, GdbFieldType.String, model)));
            db.Entries.Add(Entry(group, 0));
            db.Entries.Add(Entry(member, group, (slash, GdbFieldType.String, otherKey)));
            db.Names[slash] = "Slash";
            return GdbWriter.Serialize(db);
        }

        // A longer database, with values at the same record numbers that must not be remembered.
        var filler = new List<GdbPatchEntry>();
        for (uint i = 0; i < 64; i++)
        {
            filler.Add(Entry(0x10000000 + i, 0,
                (Engine_AnimGdbHashes.SkeletonFile, GdbFieldType.String, 0xBAD00000 + i),
                (Engine_AnimGdbHashes.GraphicAppearanceAnimatedMeshComponent, GdbFieldType.Reference, 0xBAD10000 + i),
                (Engine_AnimGdbHashes.Animation, GdbFieldType.String, 0xBAD20000 + i)));
        }

        static List<string> Scan(params byte[][] blobs)
        {
            var clips = new List<AnimClip>
            {
                new() { Key0 = 0x77770003, Name = "id_a" },
                new() { Key0 = 0x77770004, Name = "id_b" },
            };
            var byKey = new Dictionary<uint, int> { [0x77770003] = 0, [0x77770004] = 1 };
            var bindings = new List<ModelAnimationBinding>();
            var seen = new HashSet<(uint Model, int Clip, uint Key)>();
            foreach (byte[] blob in blobs)
            {
                AssertThat(GdbView.TryParse(blob, out GdbView view, out _)).IsTrue();
                Engine_AnimGdbScan.Scan(view, byKey, clips, bindings, seen);
            }

            Engine_AnimGdbScan.ReleaseScratch();
            return bindings.Select(b =>
                $"{b.ModelPathHash:x8} {b.SkeletonFileHash:x8} {b.RetargetSkeletonFileHash:x8} {b.AnimationRecordHash:x8} " +
                $"{b.AnimationKey:x8} {b.SourceRecordHash:x8} {b.ClipIndex}").ToList();
        }

        List<string> alone = Scan(Db(clipKey));
        AssertThat(alone.Count).IsEqual(2);
        AssertThat(alone[0]).IsEqual(
            $"{model:x8} {skeleton:x8} 00000000 {record:x8} {clipKey:x8} {record:x8} 0");
        AssertThat(alone[1]).IsEqual(
            $"{model:x8} {skeleton:x8} 00000000 00000000 {otherKey:x8} {record:x8} 1");

        byte[] longer = Blob(filler.ToArray());
        AssertThat(Scan(longer, Db(clipKey)).SequenceEqual(alone)).IsTrue();
        AssertThat(Scan(Db(clipKey), longer).SequenceEqual(alone)).IsTrue();
    }
}
