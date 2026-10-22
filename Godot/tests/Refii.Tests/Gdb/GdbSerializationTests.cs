namespace Refii.Tests;

using System;
using System.IO;
using GdUnit4;
using static GdUnit4.Assertions;
using Godot;
using Refii.Core;
using Refii.Gdb;

[TestSuite]
public class GdbSerializationTests
{
    [TestCase]
    public void TestMagicAndHeaderValidation()
    {
        var db = new GdbPatchDatabase();
        var entry = new GdbPatchEntry(0x10002000, 0x01);
        entry.Properties.Add(new GdbPatchProperty(0xAABBCCDD, GdbFieldType.Int, 100));
        db.Entries.Add(entry);

        byte[] bytes = GdbWriter.Serialize(db);
        AssertThat(bytes.Length).IsGreater(32);

        // Header check: 'G', 'D', 'B', 0
        AssertThat(bytes[0]).IsEqual((byte)'G');
        AssertThat(bytes[1]).IsEqual((byte)'D');
        AssertThat(bytes[2]).IsEqual((byte)'B');
        AssertThat(bytes[3]).IsEqual((byte)0);

        bool success = GdbView.TryParse(bytes, out GdbView view, out string error);
        AssertThat(success).IsTrue();
        AssertThat(error).IsEqual(string.Empty);
        AssertThat(view.RecordCount).IsEqual(1);
    }

    [TestCase]
    public void TestAllPropertyTypesRoundTrip()
    {
        var db = new GdbPatchDatabase();
        var entry = new GdbPatchEntry(0x20001000, 0x01);

        // A property's hash IS the FNV-1 of its name: the name table maps FNV-1(text) to text
        // and nothing else, and the writer files names that way.
        uint intPropHash = Engine_GameHash.Fnv1("IntProp");
        uint floatPropHash = Engine_GameHash.Fnv1("FloatProp");
        uint boolPropHash = Engine_GameHash.Fnv1("BoolProp");
        uint refPropHash = Engine_GameHash.Fnv1("RefProp");
        uint resRefPropHash = Engine_GameHash.Fnv1("ResRefProp");
        uint enumPropHash = Engine_GameHash.Fnv1("EnumProp");

        float testFloat = 123.456f;
        uint rawFloat = unchecked((uint)BitConverter.SingleToInt32Bits(testFloat));

        entry.Properties.Add(new GdbPatchProperty(intPropHash, GdbFieldType.Int, 42));
        entry.Properties.Add(new GdbPatchProperty(floatPropHash, GdbFieldType.Float, rawFloat));
        entry.Properties.Add(new GdbPatchProperty(boolPropHash, GdbFieldType.Bool, 1));
        entry.Properties.Add(new GdbPatchProperty(refPropHash, GdbFieldType.Reference, 0x99998888));
        entry.Properties.Add(new GdbPatchProperty(resRefPropHash, GdbFieldType.ResourceReference, 0x77776666));
        entry.Properties.Add(new GdbPatchProperty(enumPropHash, GdbFieldType.Enum, 3));

        db.Entries.Add(entry);

        db.Names[intPropHash] = "IntProp";
        db.Names[floatPropHash] = "FloatProp";
        db.Names[boolPropHash] = "BoolProp";
        db.Names[refPropHash] = "RefProp";
        db.Names[resRefPropHash] = "ResRefProp";
        db.Names[enumPropHash] = "EnumProp";

        byte[] bytes = GdbWriter.Serialize(db);
        bool success = GdbView.TryParse(bytes, out GdbView view, out string error);
        AssertThat(success).IsTrue();
        AssertThat(view.TryLookup(0x20001000, out int recIdx)).IsTrue();

        var fields = view.Fields(recIdx);
        AssertThat(fields.Count).IsEqual(6);

        var intField = fields.Find(f => f.Hash == intPropHash);
        AssertThat(intField).IsNotNull();
        AssertThat(intField!.Type).IsEqual(GdbFieldType.Int);
        AssertThat(intField.Raw).IsEqual(42u);
        AssertThat(intField.Name).IsEqual("IntProp");

        var floatField = fields.Find(f => f.Hash == floatPropHash);
        AssertThat(floatField).IsNotNull();
        AssertThat(floatField!.Type).IsEqual(GdbFieldType.Float);
        AssertThat(floatField.AsFloat).IsEqualApprox(testFloat, 0.001f);

        var boolField = fields.Find(f => f.Hash == boolPropHash);
        AssertThat(boolField).IsNotNull();
        AssertThat(boolField!.Type).IsEqual(GdbFieldType.Bool);
        AssertThat(boolField.AsBool).IsTrue();

        var refField = fields.Find(f => f.Hash == refPropHash);
        AssertThat(refField).IsNotNull();
        AssertThat(refField!.Type).IsEqual(GdbFieldType.Reference);
        AssertThat(refField.Raw).IsEqual(0x99998888u);

        var resRefField = fields.Find(f => f.Hash == resRefPropHash);
        AssertThat(resRefField).IsNotNull();
        AssertThat(resRefField!.Type).IsEqual(GdbFieldType.ResourceReference);
        AssertThat(resRefField.Raw).IsEqual(0x77776666u);
    }

    [TestCase]
    public void TestAuthoredStringDictionaryRoundTrip()
    {
        var db = new GdbPatchDatabase();
        var entry = new GdbPatchEntry(0x30001000, 0x01);

        uint name1Hash = Engine_GameHash.Fnv1("BulletCapacity");
        uint name2Hash = Engine_GameHash.Fnv1("BasePrice");
        uint name3Hash = Engine_GameHash.Fnv1("ReloadOnlyIfEmpty");

        entry.Properties.Add(new GdbPatchProperty(name1Hash, GdbFieldType.Int, 15));
        entry.Properties.Add(new GdbPatchProperty(name2Hash, GdbFieldType.Int, 500));
        entry.Properties.Add(new GdbPatchProperty(name3Hash, GdbFieldType.Bool, 0));
        db.Entries.Add(entry);

        uint toyGunNameHash = Engine_GameHash.Fnv1("CustomToyGun");
        db.Names[name1Hash] = "BulletCapacity";
        db.Names[name2Hash] = "BasePrice";
        db.Names[name3Hash] = "ReloadOnlyIfEmpty";
        db.Names[toyGunNameHash] = "CustomToyGun";
        db.Aliases.Add((toyGunNameHash, entry.Id));

        byte[] bytes = GdbWriter.Serialize(db);
        bool success = GdbView.TryParse(bytes, out GdbView view, out string error);
        AssertThat(success).IsTrue();

        AssertThat(view.TryLookup(entry.Id, out int recIdx)).IsTrue();
        AssertThat(view.Names[toyGunNameHash]).IsEqual("CustomToyGun");
        AssertThat(view.RecordName(recIdx)).IsEqual("CustomToyGun");

        var fields = view.Fields(recIdx);
        var f1 = fields.Find(f => f.Hash == name1Hash);
        AssertThat(f1).IsNotNull();
        AssertThat(f1!.Name).IsEqual("BulletCapacity");

        var f2 = fields.Find(f => f.Hash == name2Hash);
        AssertThat(f2).IsNotNull();
        AssertThat(f2!.Name).IsEqual("BasePrice");

        var f3 = fields.Find(f => f.Hash == name3Hash);
        AssertThat(f3).IsNotNull();
        AssertThat(f3!.Name).IsEqual("ReloadOnlyIfEmpty");
    }

    [TestCase]
    public void TestAliasesRoundTrip()
    {
        var db = new GdbPatchDatabase();
        var entry = new GdbPatchEntry(0x40001000, 0x01);
        entry.Properties.Add(new GdbPatchProperty(0x1111, GdbFieldType.Int, 1));
        db.Entries.Add(entry);

        db.Aliases.Add((0x99991111, 0x40001000));

        byte[] bytes = GdbWriter.Serialize(db);
        bool success = GdbView.TryParse(bytes, out GdbView view, out string error);
        AssertThat(success).IsTrue();
        AssertThat(view.AliasCount).IsEqual(1);

        var aliases = new System.Collections.Generic.List<(uint Alias, uint Target)>(view.AliasPairs());
        AssertThat(aliases.Count).IsEqual(1);
        AssertThat(aliases[0].Alias).IsEqual(0x99991111u);
        AssertThat(aliases[0].Target).IsEqual(0x40001000u);
    }
}
