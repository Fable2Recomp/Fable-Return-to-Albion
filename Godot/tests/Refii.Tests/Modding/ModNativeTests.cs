namespace Refii.Tests.Modding;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Mods;

[TestSuite]
public class ModNativeTests
{
    private const string Hook = """
        // REX_EXTERN(rex_InAComment) { is not a definition
        /* REX_EXTERN(rex_InABlockComment) { neither */
        extern "C" REX_FUNC(__imp__rex_Original);
        REX_EXTERN(__imp__rex_Declared);
        REX_EXTERN(rex_StaminaGate) {
          __imp__rex_StaminaGate(ctx, base);
        }
        REX_EXTERN( sub_826F57E8 )
        {
        }
        """;

    [TestCase]
    public void DerivesOnlyDefinitionsOutsideComments()
    {
        List<string> overrides = ModNative.DeriveOverrides(Hook).ToList();
        AssertThat(overrides.Count).IsEqual(2);
        AssertThat(overrides.Contains("rex_StaminaGate")).IsTrue();
        AssertThat(overrides.Contains("sub_826F57E8")).IsTrue();
    }

    [TestCase]
    public void ConflictsAgainstRefiiAndBetweenMods()
    {
        var first = new ModNativeSources { ModName = "souls", Overrides = { "rex_A", "rex_B" } };
        var second = new ModNativeSources { ModName = "other", Overrides = { "rex_B", "rex_C" } };
        var refii = new Dictionary<string, string> { ["rex_C"] = ModNative.RefiiOwner };

        List<ModNativeConflict> conflicts = ModNative.FindConflicts(new[] { first, second }, refii);

        AssertThat(conflicts.Count).IsEqual(2);
        AssertThat(conflicts.Any(c => c.Function == "rex_B" && c.First == "souls" && c.Second == "other")).IsTrue();
        AssertThat(conflicts.Any(c => c.Function == "rex_C" && c.First == ModNative.RefiiOwner && c.Second == "other")).IsTrue();
    }

    [TestCase]
    public void SoftOnlyModSetHashesEmpty()
    {
        AssertThat(ModNative.HashSet(Array.Empty<ModNativeSources>())).IsEqual(string.Empty);
        var mod = new ModNativeSources { ModName = "souls", ContentHash = "AB" };
        AssertThat(ModNative.HashSet(new[] { mod }).Length).IsEqual(64);
    }

    [TestCase]
    public void RejectsPathsThatLeaveTheMod()
    {
        AssertThat(ModNative.IsSafeRelativePath("combat.cpp")).IsTrue();
        AssertThat(ModNative.IsSafeRelativePath("include/combat.h")).IsTrue();
        AssertThat(ModNative.IsSafeRelativePath("../escape.cpp")).IsFalse();
        AssertThat(ModNative.IsSafeRelativePath("a/../../b.cpp")).IsFalse();
        AssertThat(ModNative.IsSafeRelativePath("/abs.cpp")).IsFalse();
        AssertThat(ModNative.IsSafeRelativePath("C:/x.cpp")).IsFalse();
    }

    [TestCase]
    public void ReadsNativeSourcesFromAPackage()
    {
        string root = Path.Combine(Path.GetTempPath(), "refii_native_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string package = Path.Combine(root, "souls.f2mod");
            var files = new List<(string, byte[])>
            {
                ("native/combat.cpp", Encoding.UTF8.GetBytes(Hook)),
                ("native/combat.h", Encoding.UTF8.GetBytes("#pragma once\n")),
                ("scripts/ai/x.lua", Encoding.UTF8.GetBytes("-- soft\n")),
            };
            AssertThat(F2ModPackage.Create(package, new ModMetadata { Title = "souls" }, files, out string error)).IsTrue();

            ModNativeSources? sources = ModNative.Read(new ModInstall { Name = "souls", PackagePath = package });

            AssertThat(sources).IsNotNull();
            AssertThat(sources!.Files.Count).IsEqual(2);
            AssertThat(sources.CompiledFiles.Count()).IsEqual(1);
            AssertThat(sources.Overrides.Count).IsEqual(2);
            AssertThat(sources.ContentHash.Length).IsEqual(64);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
