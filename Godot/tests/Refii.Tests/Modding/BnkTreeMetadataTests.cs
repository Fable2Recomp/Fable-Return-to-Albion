namespace Refii.Tests.Modding;

using System;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Assets;

[TestSuite]
public class BnkTreeMetadataTests
{
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
