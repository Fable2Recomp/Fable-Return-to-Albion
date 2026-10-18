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
    public void DerivesReplacementsFromHooksThatNeverCallTheOriginal()
    {
        List<string> overrides = ModNative.DeriveOverrides(Hook).ToList();
        HashSet<string> replaces = ModNative.DeriveReplacements(overrides, new[] { Hook });

        AssertThat(replaces.Count).IsEqual(1);
        AssertThat(replaces.Contains("sub_826F57E8")).IsTrue();
        AssertThat(replaces.Contains("rex_StaminaGate")).IsFalse();
    }

    [TestCase]
    public void WrappingHooksChainAndOnlyReplacersConflict()
    {
        // souls is highest priority (outermost); other replaces rex_C, which refii also hooks.
        var first = new ModNativeSources { ModName = "souls", Overrides = { "rex_A", "rex_B" } };
        var second = new ModNativeSources { ModName = "other", Overrides = { "rex_B", "rex_C" }, Replaces = { "rex_C" } };
        var refii = new Dictionary<string, string> { ["rex_B"] = ModNative.RefiiOwner, ["rex_C"] = ModNative.RefiiOwner };
        var mods = new[] { first, second };

        List<ModNativeConflict> conflicts = ModNative.FindConflicts(mods, refii);
        AssertThat(conflicts.Count).IsEqual(1);
        AssertThat(conflicts[0].Function).IsEqual("rex_C");
        AssertThat(conflicts[0].First).IsEqual("other");
        AssertThat(conflicts[0].Second).IsEqual(ModNative.RefiiOwner);

        List<ModNativeChain> chains = ModNative.PlanChains(mods, refii);
        AssertThat(chains.Count).IsEqual(2);
        ModNativeChain b = chains.First(c => c.Function == "rex_B");
        AssertThat(b.Owners.SequenceEqual(new[] { "souls", "other", ModNative.RefiiOwner })).IsTrue();

        List<string> defines = ModNative.ChainDefines(chains, "other");
        AssertThat(defines.Contains("-Drex_B=rex_B__other")).IsTrue();
        AssertThat(defines.Contains("-D__imp__rex_B=rex_B__refii")).IsTrue();
        AssertThat(defines.Contains("-Drex_C=rex_C__other")).IsTrue();
        AssertThat(defines.Contains("-D__imp__rex_C=rex_C__refii")).IsTrue();

        List<string> outer = ModNative.ChainDefines(chains, "souls");
        AssertThat(outer.Contains("-Drex_B=rex_B__souls")).IsTrue();
        AssertThat(outer.Contains("-D__imp__rex_B=rex_B__other")).IsTrue();
        AssertThat(outer.Any(d => d.Contains("rex_A"))).IsFalse();

        List<string> innermost = ModNative.ChainDefines(chains, ModNative.RefiiOwner);
        AssertThat(innermost.Contains("-Drex_B=rex_B__refii")).IsTrue();
        AssertThat(innermost.Any(d => d.StartsWith("-D__imp__"))).IsFalse();

        string source = ModNative.ChainSource(chains);
        AssertThat(source.Contains("REX_EXTERN(rex_B__souls);")).IsTrue();
        AssertThat(source.Contains("REX_EXTERN(rex_B) {")).IsTrue();
        AssertThat(source.Contains("return rex_B__souls(ctx, base);")).IsTrue();
        AssertThat(source.Contains("return rex_C__other(ctx, base);")).IsTrue();
        AssertThat(ModNative.ChainSource(new List<ModNativeChain>())).IsEqual(string.Empty);
    }

    [TestCase]
    public void ChainSymbolsAreIdentifiers()
    {
        AssertThat(ModNative.ChainSymbol("rex_X", "hd-ui 2")).IsEqual("rex_X__hd_ui_2");
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
