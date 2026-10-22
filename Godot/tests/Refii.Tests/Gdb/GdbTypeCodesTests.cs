namespace Refii.Tests;

using System;
using System.IO;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Gdb;

/// <summary>
/// ONE SET OF TYPE CODES. The guest's property types (the byte a schema descriptor carries,
/// consumed by <c>GDBEntry_GetPropTyped</c> through the shim at 0x8229CE90) are declared twice in
/// this tree — <see cref="GdbFieldType"/> for the engine and <c>src/vfs/include/gdb/gdb_database.h</c>
/// for the native converter — and the native header once had Int and Enum swapped against the
/// data (BasePrice, Rating and BulletCapacity are stored as 1; WeaponType, Faction and
/// ReactionType as 5; the guest asks for "BasePrice" as 1 at 0x8296B540 and for "Gender" as 5 at
/// 0x82401B84). This pins the header's text to the enum, the way <c>ModApiTests</c> pins
/// <c>refii_mod.h</c>. Pure .NET.
/// </summary>
[TestSuite]
public class GdbTypeCodesTests
{
    [TestCase]
    public void TheNativeHeaderStatesTheEnumsCodes()
    {
        AssertThat((byte)GdbFieldType.Bool).IsEqual((byte)0);
        AssertThat((byte)GdbFieldType.Int).IsEqual((byte)1);
        AssertThat((byte)GdbFieldType.Float).IsEqual((byte)3);
        AssertThat((byte)GdbFieldType.String).IsEqual((byte)4);
        AssertThat((byte)GdbFieldType.Enum).IsEqual((byte)5);
        AssertThat((byte)GdbFieldType.Reference).IsEqual((byte)6);
        AssertThat((byte)GdbFieldType.ResourceReference).IsEqual((byte)7);

        string? root = GdbContainerRoundTripTests.RepoRoot();
        if (root == null)
        {
            Console.WriteLine("TheNativeHeaderStatesTheEnumsCodes: repo root not found; header not pinned");
            return;
        }

        string header = File.ReadAllText(Path.Combine(root, "src", "vfs", "include", "gdb", "gdb_database.h"));
        foreach ((string native, GdbFieldType managed) in new[]
        {
            ("kTypeBool", GdbFieldType.Bool),
            ("kTypeInt", GdbFieldType.Int),
            ("kTypeFloat", GdbFieldType.Float),
            ("kTypeString", GdbFieldType.String),
            ("kTypeEnum", GdbFieldType.Enum),
            ("kTypeRef", GdbFieldType.Reference),
            ("kTypeLink", GdbFieldType.ResourceReference),
        })
        {
            string line = $"inline constexpr uint8_t {native} = {(byte)managed};";
            AssertThat(header.Contains(line, StringComparison.Ordinal))
                .OverrideFailureMessage($"gdb_database.h does not state '{line}'; GdbFieldType.{managed} = {(byte)managed}")
                .IsTrue();
        }
    }
}
