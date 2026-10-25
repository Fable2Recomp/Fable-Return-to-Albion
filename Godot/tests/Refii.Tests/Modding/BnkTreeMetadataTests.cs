namespace Refii.Tests.Modding;

using System;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Assets;

[TestSuite]
public class BnkTreeMetadataTests
{
    [TestCase]
    public void VirtualModelDragsKeepTheirOriginalPaths()
    {
        const string mirror = "game://art/props/bs_murgo_mirror.mdl";
        const string candle = "game://art/props/candle.mdl";
        AssertThat(BnkMountPlugin.AssetDragPaths(new[] { mirror })).ContainsExactly(mirror);
        AssertThat(BnkMountPlugin.AssetDragPaths(new[] { mirror, candle, mirror })).ContainsExactly(mirror, candle);
        AssertThat(BnkMountPlugin.AssetDragPaths(new[] { mirror, "res://prop.tscn" })).ContainsExactly(mirror, "res://prop.tscn");
    }

    [TestCase]
    public void OrdinaryFileAndFolderDragsAreNotReplaced()
    {
        AssertThat(BnkMountPlugin.AssetDragPaths(new[] { "res://main.tscn" }).Length).IsEqual(0);
        AssertThat(BnkMountPlugin.AssetDragPaths(new[] { "res://" }).Length).IsEqual(0);
        AssertThat(BnkMountPlugin.AssetDragPaths(Array.Empty<string>()).Length).IsEqual(0);
        AssertThat(BnkMountPlugin.AssetDragPaths(new[] { "__bnk_levels_root__" }).Length).IsEqual(0);
        AssertThat(BnkMountPlugin.AssetDragPaths(new[] { "game://art/mirror.mdl", "bnk:art/props" }).Length).IsEqual(0);
        AssertThat(BnkMountPlugin.AssetDragPaths(new[] { "game://art/" }).Length).IsEqual(0);
    }

    [TestCase]
    public void TestBnkMetaKeyConstant()
    {
        // The metadata key used to store Game:// custom metadata on Godot Object instances
        AssertThat(BnkMountPlugin.BnkMetaKey).IsEqual("__bnk_meta__");
    }

    [TestCase]
    public void TestVirtualPathParsingFromMetadata()
    {
        string virtualPath = "art/models/weapons/sword.mdl";
        string fileMeta = "game://" + virtualPath;

        AssertThat(fileMeta.StartsWith("game://", StringComparison.Ordinal)).IsTrue();
        string extracted = fileMeta["game://".Length..];
        AssertThat(extracted).IsEqual(virtualPath);
    }

    [TestCase]
    public void TestLevelVirtualPathParsingFromMetadata()
    {
        string heightfield = "levels/b_hero.ehf";
        string levelMeta = "lvl:" + heightfield;

        AssertThat(levelMeta.StartsWith("lvl:", StringComparison.Ordinal)).IsTrue();
        string extracted = levelMeta["lvl:".Length..];
        AssertThat(extracted).IsEqual(heightfield);
    }

    [TestCase]
    public void TestBankFolderPrefixParsing()
    {
        string bnkChild = "art/textures/ui";
        string bnkMeta = "bnk:" + bnkChild;

        AssertThat(bnkMeta.StartsWith("bnk:", StringComparison.Ordinal)).IsTrue();
        string extracted = bnkMeta[4..];
        AssertThat(extracted).IsEqual(bnkChild);
    }
}
