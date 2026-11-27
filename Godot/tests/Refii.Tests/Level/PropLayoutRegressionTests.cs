namespace Refii.Tests.Level;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using GdUnit4;
using Refii.Bnk;
using Refii.Core;
using Refii.Gdb;
using Refii.Level;
using Refii.Titles.Fable2;
using static GdUnit4.Assertions;

/// <summary>
/// Regression tests verifying that <see cref="Engine_LevelProps"/>:
/// 1. Does not synthesize phantom <c>.mdl.gmd</c> <c>*.Layout.Instance.*</c> doors/windows (Issues #89 and #90).
/// 2. Trusts GDB-authored and supplemental model hashes without curated overrides or rejection heuristics.
/// 3. Does not invent models for placements with no model hash and no appearance component.
/// 4. Preserves the owning record hash on Fable 2 companion parts for <c>Engine_StaticTwins</c> reconciliation.
/// </summary>
[TestSuite]
public sealed class PropLayoutRegressionTests
{
    private const uint ShellRecordHash = 0x472B1D55;
    private const uint ShellTransformHash = 0x472B1D56;
    private const uint ShellPosHash = 0x472B1D57;

    private const uint BarRecordHash = 0x9F110001;
    private const uint BarTransformHash = 0x9F110002;
    private const uint BarPosHash = 0x9F110003;
    private const uint BarParentHash = 0xB9F514CA;

    private const uint StallRecordHash = 0x9F120001;
    private const uint StallTransformHash = 0x9F120002;
    private const uint StallPosHash = 0x9F120003;

    private const uint EaselRecordHash = 0x9F130001;
    private const uint EaselTransformHash = 0x9F130002;
    private const uint EaselPosHash = 0x9F130003;

    private const uint ClockRecordHash = 0x0BDC7B88;
    private const uint ClockTransformHash = 0x9F140002;
    private const uint ClockPosHash = 0x9F140003;

    private const string ShellModelPath =
        "Art/Environment/Regions/Wraithmarsh/Buildings/dotXSI/WM_RuinedBuilding_Small/WM_RuinedBuilding_Small/exterior.mdl";

    private const string PhantomDoorModelPath =
        "Art/Environment/Shared assets/Doors_Windows/dotXSI/ESA_Door_Farmhouse/ESA_Door_Farmhouse.mdl";

    private const string BarStraightModelPath =
        "Art/Environment/Shared assets/ShopClutter/dotXSI/ESA_BarStraight/ESA_BarStraight.mdl";

    private const string ShelfSmallModelPath =
        "Art/Environment/Shared assets/ShopClutter/dotXSI/ESA_Shop_Shelf_Small/ESA_Shop_Shelf_Small.mdl";

    private const string MarketStallModelPath =
        "Art/Environment/Regions/Bowerstone/Buildings/dotXSI/BS_Market_MarketStall/BS_Market_MarketStall.mdl";

    private const string EaselModelPath =
        "Art/Environment/Shared assets/Props/dotXSI/ESA_Easel/ESA_Easel.mdl";

    private const string MarketPlatformModelPath =
        "Art/Environment/Regions/Bowerstone/Structures/dotXSI/BS_Market_Platform/BS_Market_Platform.mdl";

    [TestCase]
    public void CompactTextNormalizesCaseAndStripsSeparators()
    {
        AssertThat(Engine_LevelProps.CompactText("ESA_Door_Farmhouse")).IsEqual("esadoorfarmhouse");
        AssertThat(Engine_LevelProps.CompactText("WM_RuinedBuilding_Small_V2")).IsEqual("wmruinedbuildingsmallv2");
        AssertThat(Engine_LevelProps.CompactText("Art/Buildings\\BO_Lg_Shop/exterior.mdl"))
            .IsEqual("artbuildingsbolgshopexteriormdl");
        AssertThat(Engine_LevelProps.CompactText(string.Empty)).IsEqual(string.Empty);
    }

    [TestCase]
    public void ModelIndexEntityKeyAndExactHashLookupStayIntact()
    {
        AssertThat(Engine_PropModelIndex.EntityKey("NewObjectBuildingBloodstoneLargeShop_Tattoo"))
            .IsEqual("bloodstonelargeshoptattoo");
        AssertThat(Engine_PropModelIndex.EntityKey("ObjectBuildingRavenscarFarmhouse_Small_Ruined_2"))
            .IsEqual("ravenscarfarmhousesmallruined2");

        try
        {
            Engine_PropModelIndex.SetModelSource(() => new[]
            {
                new PropModelAsset(ShellModelPath, "test.bnk"),
            });

            uint hash = Engine_PropModelIndex.PathHash(ShellModelPath);
            AssertThat(Engine_PropModelIndex.Resolve(hash)).IsEqual(ShellModelPath);
            AssertThat(Engine_PropModelIndex.ResolveByPath(ShellModelPath)).IsEqual(ShellModelPath);
            AssertThat(Engine_PropModelIndex.Resolve(Engine_PropModelIndex.PathHash(EaselModelPath))).IsNull();
        }
        finally
        {
            Engine_PropModelIndex.SetModelSource(null);
        }
    }

    [TestCase]
    public void PropBuildStatsOmitsObsoleteLayoutAndHeuristicCounters()
    {
        string summary = new PropBuildStats { SaveEntities = 12, Placements = 10, Instances = 8 }.ToString();
        AssertThat(summary.Contains("gmd(", StringComparison.Ordinal)).IsFalse();
        AssertThat(summary.Contains("namematched=", StringComparison.Ordinal)).IsFalse();
        AssertThat(summary.Contains("curated=", StringComparison.Ordinal)).IsFalse();
        AssertThat(summary.Contains("bridge=", StringComparison.Ordinal)).IsFalse();
        AssertThat(summary.Contains("shells=", StringComparison.Ordinal)).IsFalse();
    }

    [TestCase]
    public void TryCollectStaticGeometryAndLightsDoesNotSynthesizeGmdLayoutDoorsOrWindows()
    {
        const string gdbPath = "worlds/albion/testregion/defaultscenario/defaultscenario.gdb";
        const string savePath = "worlds/albion/testregion/defaultscenario/defaultscenario.save";

        uint shellModelHash = Engine_PropModelIndex.PathHash(ShellModelPath);
        byte[] gdbBytes = BuildShellLevelGdb(shellModelHash);
        byte[] saveBytes = Encoding.UTF8.GetBytes("""
            <?xml version="1.0" encoding="utf-8"?>
            <Level>
              <id_5A4CBED4 name="DefaultLayer" load="AlwaysOn">
                <Entity name="ObjectBuildingRavenscarFarmhouse_Small_Ruined">0x472B1D55</Entity>
              </id_5A4CBED4>
            </Level>
            """);
        byte[] gmdSidecarBytes = BuildLayoutGmdBytes(
            "Prop.Layout.Instance.Art\\Environment\\Shared assets\\Doors_Windows\\dotXSI\\ESA_Door_Farmhouse\\ESA_Door_Farmhouse_Asset.emdl");

        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            [gdbPath] = gdbBytes,
            [savePath] = saveBytes,
            [ShellModelPath + ".gmd"] = gmdSidecarBytes,
        };

        var level = new LevelEntry
        {
            World = "Albion",
            LevelPath = "testregion",
            MainEhfPath = "worlds/albion/testregion/defaultscenario/defaultscenario.ehf",
        };

        try
        {
            Engine_PropModelIndex.SetModelSource(() => new[]
            {
                new PropModelAsset(ShellModelPath, "test.bnk"),
                new PropModelAsset(PhantomDoorModelPath, "other_region.bnk"),
            });

            bool ok = Engine_LevelProps.TryCollectStaticGeometryAndLights(
                level,
                path => files.TryGetValue(path.Replace('\\', '/'), out byte[]? data) ? data : null,
                out List<Engine_LevelProps.StaticShadowPropBlock> propBlocks,
                out _,
                out _,
                out _,
                out string resolvedGdbPath);

            AssertThat(ok).IsTrue();
            AssertThat(resolvedGdbPath).IsEqual(gdbPath);
            AssertThat(propBlocks.Count).IsEqual(1);
            AssertThat(propBlocks[0].ModelPath).IsEqual(ShellModelPath);
            foreach (Engine_LevelProps.StaticShadowPropBlock block in propBlocks)
            {
                AssertThat(block.ModelPath.Contains("door", StringComparison.OrdinalIgnoreCase)).IsFalse();
                AssertThat(block.ModelPath.Contains("window", StringComparison.OrdinalIgnoreCase)).IsFalse();
            }
        }
        finally
        {
            Engine_PropModelIndex.SetModelSource(null);
        }
    }

    [TestCase]
    public void TryCollectStaticGeometryAndLightsTrustsSupplementalGdbWithoutCuratedOverridesOrRejections()
    {
        const string gdbPath = "worlds/albion/testmarket/defaultscenario/defaultscenario.gdb";
        const string savePath = "worlds/albion/testmarket/defaultscenario/defaultscenario.save";
        const string globalsGdbPath = "globals/globals.gdb";

        var levelDb = new GdbPatchDatabase();
        AddPlacedEntity(levelDb, BarRecordHash, BarTransformHash, BarPosHash, 100f, 200f, 10f, parentHash: BarParentHash);
        AddPlacedEntity(levelDb, StallRecordHash, StallTransformHash, StallPosHash, 120f, 200f, 10f);
        AddPlacedEntity(levelDb, EaselRecordHash, EaselTransformHash, EaselPosHash, 140f, 200f, 10f);

        var globalsDb = new GdbPatchDatabase();
        var barGlobal = new GdbPatchEntry(BarParentHash, 0x01);
        barGlobal.Properties.Add(new GdbPatchProperty(GdbHash.ModelFile, GdbFieldType.String, Engine_PropModelIndex.PathHash(BarStraightModelPath)));
        globalsDb.Entries.Add(barGlobal);

        var stallGlobal = new GdbPatchEntry(StallRecordHash, 0x01);
        stallGlobal.Properties.Add(new GdbPatchProperty(GdbHash.ModelFile, GdbFieldType.String, Engine_PropModelIndex.PathHash(MarketStallModelPath)));
        globalsDb.Entries.Add(stallGlobal);

        byte[] saveBytes = Encoding.UTF8.GetBytes("""
            <?xml version="1.0" encoding="utf-8"?>
            <Level>
              <id_5A4CBED4 name="DefaultLayer" load="AlwaysOn">
                <Entity name="NewObjectFurnitureStaticBarStraight">0x9F110001</Entity>
                <Entity name="MarketStallGeneral">0x9F120001</Entity>
                <Entity name="Easel_3">0x9F130001</Entity>
              </id_5A4CBED4>
            </Level>
            """);

        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            [gdbPath] = GdbWriter.Serialize(levelDb),
            [savePath] = saveBytes,
            [globalsGdbPath] = GdbWriter.Serialize(globalsDb),
        };

        var level = new LevelEntry
        {
            World = "Albion",
            LevelPath = "testmarket",
            MainEhfPath = "worlds/albion/testmarket/defaultscenario/defaultscenario.ehf",
        };

        Engine_Title previous = Engine_Title.Active;
        try
        {
            Engine_Title.Active = Engine_Title.Fable2;
            Engine_PropModelIndex.SetModelSource(() => new[]
            {
                new PropModelAsset(BarStraightModelPath, "test.bnk"),
                new PropModelAsset(ShelfSmallModelPath, "test.bnk"),
                new PropModelAsset(MarketStallModelPath, "test.bnk"),
                new PropModelAsset(EaselModelPath, "test.bnk"),
            });

            bool ok = Engine_LevelProps.TryCollectStaticGeometryAndLights(
                level,
                path => files.TryGetValue(path.Replace('\\', '/'), out byte[]? data) ? data : null,
                out List<Engine_LevelProps.StaticShadowPropBlock> propBlocks,
                out _,
                out _,
                out _,
                out _);

            AssertThat(ok).IsTrue();
            var resolvedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Engine_LevelProps.StaticShadowPropBlock block in propBlocks)
            {
                resolvedPaths.Add(block.ModelPath);
            }

            AssertThat(resolvedPaths.Contains(BarStraightModelPath)).IsTrue();
            AssertThat(resolvedPaths.Contains(MarketStallModelPath)).IsTrue();
            AssertThat(resolvedPaths.Contains(ShelfSmallModelPath)).IsFalse();
            AssertThat(resolvedPaths.Contains(EaselModelPath)).IsFalse();
            AssertThat(propBlocks.Count).IsEqual(2);
        }
        finally
        {
            Engine_Title.Active = previous;
            Engine_PropModelIndex.SetModelSource(null);
        }
    }

    [TestCase]
    public void ClocktowerCompanionPartsPreserveOwningPlacementRecordHash()
    {
        const string gdbPath = "worlds/albion/testclock/defaultscenario/defaultscenario.gdb";
        const string savePath = "worlds/albion/testclock/defaultscenario/defaultscenario.save";

        var levelDb = new GdbPatchDatabase();
        AddPlacedEntity(
            levelDb,
            ClockRecordHash,
            ClockTransformHash,
            ClockPosHash,
            117.8f,
            261.9f,
            39.8f,
            parentHash: Fable2Props.ClocktowerParentHash,
            modelPathHash: Engine_PropModelIndex.PathHash(MarketPlatformModelPath));

        byte[] saveBytes = Encoding.UTF8.GetBytes("""
            <?xml version="1.0" encoding="utf-8"?>
            <Level>
              <id_5A4CBED4 name="DefaultLayer" load="AlwaysOn">
                <Entity name="QO700_ClockTowerBase">0x0BDC7B88</Entity>
              </id_5A4CBED4>
            </Level>
            """);

        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            [gdbPath] = GdbWriter.Serialize(levelDb),
            [savePath] = saveBytes,
        };

        var level = new LevelEntry
        {
            World = "Albion",
            LevelPath = "testclock",
            MainEhfPath = "worlds/albion/testclock/defaultscenario/defaultscenario.ehf",
        };

        Engine_Title previous = Engine_Title.Active;
        try
        {
            Engine_Title.Active = Engine_Title.Fable2;
            Engine_PropModelIndex.SetModelSource(() => new[]
            {
                new PropModelAsset(MarketPlatformModelPath, "test.bnk"),
                new PropModelAsset(Fable2Props.ClocktowerBasePath, "test.bnk"),
                new PropModelAsset(Fable2Props.ClocktowerPartPaths[0], "test.bnk"),
                new PropModelAsset(Fable2Props.ClocktowerPartPaths[1], "test.bnk"),
                new PropModelAsset(Fable2Props.ClocktowerPartPaths[2], "test.bnk"),
            });

            bool ok = Engine_LevelProps.TryCollectStaticGeometryAndLights(
                level,
                path => files.TryGetValue(path.Replace('\\', '/'), out byte[]? data) ? data : null,
                out List<Engine_LevelProps.StaticShadowPropBlock> propBlocks,
                out _,
                out _,
                out _,
                out _);

            AssertThat(ok).IsTrue();
            AssertThat(propBlocks.Count).IsEqual(5);
            foreach (Engine_LevelProps.StaticShadowPropBlock block in propBlocks)
            {
                AssertThat(block.InstanceHashes.Count).IsEqual(1);
                AssertThat(block.InstanceHashes[0]).IsEqual(ClockRecordHash);
            }
        }
        finally
        {
            Engine_Title.Active = previous;
            Engine_PropModelIndex.SetModelSource(null);
        }
    }

    private static byte[] BuildShellLevelGdb(uint modelPathHash)
    {
        var db = new GdbPatchDatabase();
        AddPlacedEntity(db, ShellRecordHash, ShellTransformHash, ShellPosHash, 221.56f, 43.94f, 20.39f, modelPathHash: modelPathHash);
        return GdbWriter.Serialize(db);
    }

    private static void AddPlacedEntity(
        GdbPatchDatabase db,
        uint recordHash,
        uint transformHash,
        uint posHash,
        float x,
        float y,
        float z,
        uint parentHash = 0,
        uint modelPathHash = 0)
    {
        var pos = new GdbPatchEntry(posHash, 0x01);
        pos.Properties.Add(new GdbPatchProperty(GdbView.HashVecX, GdbFieldType.Float, BitConverter.SingleToUInt32Bits(x)));
        pos.Properties.Add(new GdbPatchProperty(GdbView.HashVecY, GdbFieldType.Float, BitConverter.SingleToUInt32Bits(y)));
        pos.Properties.Add(new GdbPatchProperty(GdbView.HashVecZ, GdbFieldType.Float, BitConverter.SingleToUInt32Bits(z)));
        db.Entries.Add(pos);

        var trans = new GdbPatchEntry(transformHash, 0x01);
        trans.Properties.Add(new GdbPatchProperty(GdbHash.Position, GdbFieldType.Reference, posHash));
        db.Entries.Add(trans);

        var rec = new GdbPatchEntry(recordHash, 0x01);
        if (parentHash != 0)
        {
            rec.Properties.Add(new GdbPatchProperty(GdbView.HashParent, GdbFieldType.Reference, parentHash));
        }
        rec.Properties.Add(new GdbPatchProperty(GdbHash.TransformComponent, GdbFieldType.Reference, transformHash));
        if (modelPathHash != 0)
        {
            rec.Properties.Add(new GdbPatchProperty(GdbHash.ModelFile, GdbFieldType.String, modelPathHash));
        }
        db.Entries.Add(rec);
    }

    private static byte[] BuildLayoutGmdBytes(string marker)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);
        bw.Write(Encoding.ASCII.GetBytes("GameMesh\0\0\0\x03"));
        bw.Write(Encoding.ASCII.GetBytes(marker));
        bw.Write((byte)0);
        // 7 big-endian floats: identity quaternion (0,0,0,1) + offset (1,2,0)
        WriteBeFloat(bw, 0f);
        WriteBeFloat(bw, 0f);
        WriteBeFloat(bw, 0f);
        WriteBeFloat(bw, 1f);
        WriteBeFloat(bw, 1f);
        WriteBeFloat(bw, 2f);
        WriteBeFloat(bw, 0f);
        bw.Write(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF });
        return ms.ToArray();
    }

    private static void WriteBeFloat(BinaryWriter bw, float value)
    {
        Span<byte> buf = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteSingleBigEndian(buf, value);
        bw.Write(buf);
    }
}
