namespace Refii.Tests.Level;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using GdUnit4;
using Refii.Bnk;
using Refii.Gdb;
using Refii.Level;
using static GdUnit4.Assertions;

/// <summary>
/// Regression tests verifying that <see cref="Engine_LevelProps"/> no longer synthesizes phantom
/// <c>.mdl.gmd</c> <c>*.Layout.Instance.*</c> doors/windows (Issues #89 and #90) and that
/// model compact-key indexing stays intact via <see cref="Engine_LevelProps.CompactText"/>.
/// </summary>
[TestSuite]
public sealed class PropLayoutRegressionTests
{
    private const uint ShellRecordHash = 0x472B1D55;
    private const uint ShellTransformHash = 0x472B1D56;
    private const uint ShellPosHash = 0x472B1D57;

    private const string ShellModelPath =
        "Art/Environment/Regions/Wraithmarsh/Buildings/dotXSI/WM_RuinedBuilding_Small/WM_RuinedBuilding_Small/exterior.mdl";

    private const string PhantomDoorModelPath =
        "Art/Environment/Shared assets/Doors_Windows/dotXSI/ESA_Door_Farmhouse/ESA_Door_Farmhouse.mdl";

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
    public void ModelIndexKeysForAndEntityKeyUseCompactText()
    {
        var keys = new List<string>(Engine_PropModelIndex.KeysFor(ShellModelPath, "exterior"));
        AssertThat(keys.Contains("wmruinedbuildingsmall")).IsTrue();
        AssertThat(keys.Contains("wmruinedbuildingsmallexterior")).IsTrue();

        AssertThat(Engine_PropModelIndex.EntityKey("NewObjectBuildingBloodstoneLargeShop_Tattoo"))
            .IsEqual("bloodstonelargeshoptattoo");
        AssertThat(Engine_PropModelIndex.EntityKey("ObjectBuildingRavenscarFarmhouse_Small_Ruined_2"))
            .IsEqual("ravenscarfarmhousesmallruined2");
    }

    [TestCase]
    public void PropBuildStatsOmitsObsoleteGmdLayoutCounters()
    {
        string summary = new PropBuildStats { SaveEntities = 12, Placements = 10, Instances = 8 }.ToString();
        AssertThat(summary.Contains("gmd(", StringComparison.Ordinal)).IsFalse();
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

    private static byte[] BuildShellLevelGdb(uint modelPathHash)
    {
        var db = new GdbPatchDatabase();

        var pos = new GdbPatchEntry(ShellPosHash, 0x01);
        pos.Properties.Add(new GdbPatchProperty(GdbView.HashVecX, GdbFieldType.Float, BitConverter.SingleToUInt32Bits(221.56f)));
        pos.Properties.Add(new GdbPatchProperty(GdbView.HashVecY, GdbFieldType.Float, BitConverter.SingleToUInt32Bits(43.94f)));
        pos.Properties.Add(new GdbPatchProperty(GdbView.HashVecZ, GdbFieldType.Float, BitConverter.SingleToUInt32Bits(20.39f)));
        db.Entries.Add(pos);

        var trans = new GdbPatchEntry(ShellTransformHash, 0x01);
        trans.Properties.Add(new GdbPatchProperty(GdbHash.Position, GdbFieldType.Reference, ShellPosHash));
        db.Entries.Add(trans);

        var rec = new GdbPatchEntry(ShellRecordHash, 0x01);
        rec.Properties.Add(new GdbPatchProperty(GdbHash.TransformComponent, GdbFieldType.Reference, ShellTransformHash));
        rec.Properties.Add(new GdbPatchProperty(GdbHash.ModelFile, GdbFieldType.String, modelPathHash));
        db.Entries.Add(rec);

        return GdbWriter.Serialize(db);
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
