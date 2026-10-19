namespace Refii.Tests.Modding;

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.EditorTools;
using Refii.Gdb;
using Refii.Models;
using Refii.Mods;

[TestSuite]
public class ModPbrPipelineTests
{
    private static byte[] BuildSyntheticMdl(
        string meshName,
        string diffuseTex,
        string specularTex,
        string normalTex,
        int lodCount = 1)
    {
        static void WriteU32BE(Stream s, uint val)
        {
            s.WriteByte((byte)(val >> 24));
            s.WriteByte((byte)(val >> 16));
            s.WriteByte((byte)(val >> 8));
            s.WriteByte((byte)val);
        }

        static void WriteStrz(Stream s, string text)
        {
            byte[] ascii = Encoding.ASCII.GetBytes(text ?? string.Empty);
            s.Write(ascii, 0, ascii.Length);
            s.WriteByte(0);
        }

        byte[] BuildLodPayload()
        {
            using var ms = new MemoryStream();
            ms.Write(new byte[32]); // 32-byte LOD prologue
            WriteU32BE(ms, 0u); // bc = 0
            WriteU32BE(ms, 0u); // btc = 0
            ms.Write(new byte[40]); // 40-byte counts header
            WriteU32BE(ms, 1u); // mc = 1
            WriteU32BE(ms, 1u); // rigid = 1
            WriteU32BE(ms, 0u); // skinned = 0
            WriteU32BE(ms, 0u); // ex52 = 0
            WriteU32BE(ms, 0u); // ex240 = 0
            ms.WriteByte(0);    // section flag = 0
            WriteU32BE(ms, 0u); // tagc = 0
            for (int i = 0; i < 5; i++) WriteU32BE(ms, 0u); // shdesc (sh4 = 0)
            WriteU32BE(ms, 0u); // fc = 0
            WriteU32BE(ms, 0u); // hrc = 0

            // Mesh header 0:
            WriteU32BE(ms, 0u); // sel = 0
            WriteStrz(ms, meshName);
            ms.Write(new byte[45]); // 45-byte scalar block
            WriteU32BE(ms, 1u); // matc = 1

            // Material 0:
            WriteStrz(ms, diffuseTex);  // slot 0: Diffuse
            WriteStrz(ms, specularTex); // slot 1: Specular
            WriteStrz(ms, normalTex);   // slot 2: Normal
            WriteStrz(ms, "");          // slot 3: EnvironmentMap
            WriteStrz(ms, "");          // slot 4: SelfIllumination
            for (int k = 0; k < 4; k++) WriteStrz(ms, ""); // 4 shader params
            ms.Write(new byte[8]); // 2 material floats

            // Rigid record 0:
            WriteStrz(ms, meshName);
            ms.WriteByte(0);
            WriteU32BE(ms, 0u); // partIndex
            WriteU32BE(ms, 0u); // idx1 (meshIdx)
            WriteU32BE(ms, 0u);
            WriteU32BE(ms, 3u); // idxCount = 3
            WriteU32BE(ms, 3u); // vtxCount = 3
            ms.Write(new byte[40]); // bounds + sphere
            WriteU32BE(ms, 1u); // subc = 1

            // Submesh 0:
            WriteU32BE(ms, 0xFFFFFFFFu); // marker
            WriteU32BE(ms, 0u); // matIdx = 0
            ms.WriteByte(0);    // primType = 0
            WriteU32BE(ms, 1u); // faceCount = 1
            WriteU32BE(ms, 0u); // startIdx = 0
            ms.Write(new byte[24]); // 6 floats bounding box

            ms.Write(new byte[3 * 20]); // 3 rigid vertices (20 bytes each)
            ms.Write(new byte[3 * 2]);  // 3 indices (2 bytes each)
            WriteU32BE(ms, 0u); // ext1 = 0

            return ms.ToArray();
        }

        byte[] lodPayload = BuildLodPayload();
        using var fileStream = new MemoryStream();

        byte[] header = new byte[104];
        byte[] magic = Encoding.ASCII.GetBytes("MeshFile");
        Array.Copy(magic, 0, header, 0, 8);
        header[11] = 36;
        header[15] = 88;
        header[0x3B] = (byte)lodCount;
        for (int i = 0; i < lodCount; i++)
        {
            uint sz = (uint)lodPayload.Length;
            int off = 0x3C + (i * 4);
            header[off] = (byte)(sz >> 24);
            header[off + 1] = (byte)(sz >> 16);
            header[off + 2] = (byte)(sz >> 8);
            header[off + 3] = (byte)sz;
        }

        fileStream.Write(header, 0, header.Length);
        for (int i = 0; i < lodCount; i++)
        {
            fileStream.Write(lodPayload, 0, lodPayload.Length);
        }

        return fileStream.ToArray();
    }

    private static byte[] BuildGlbWithPbr(bool includeMr, bool includeOcclusion, bool includeEmissive)
    {
        byte[] dummyAlbedoPng = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4 };
        byte[] dummyMrPng = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 5, 6, 7, 8 };
        byte[] dummyOccPng = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 9, 10, 11, 12 };
        byte[] dummyEmissPng = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 13, 14, 15, 16 };

        var images = new List<byte[]> { dummyAlbedoPng };
        int mrImgIdx = -1;
        int occImgIdx = -1;
        int emissImgIdx = -1;

        if (includeMr)
        {
            mrImgIdx = images.Count;
            images.Add(dummyMrPng);
        }
        if (includeOcclusion)
        {
            occImgIdx = images.Count;
            images.Add(dummyOccPng);
        }
        if (includeEmissive)
        {
            emissImgIdx = images.Count;
            images.Add(dummyEmissPng);
        }

        var bufferViewsSb = new StringBuilder();
        var imagesSb = new StringBuilder();
        var texturesSb = new StringBuilder();

        int currentOffset = 0;
        for (int i = 0; i < images.Count; i++)
        {
            if (i > 0)
            {
                bufferViewsSb.Append(",");
                imagesSb.Append(",");
                texturesSb.Append(",");
            }
            bufferViewsSb.Append("{\"buffer\":0,\"byteOffset\":" + currentOffset + ",\"byteLength\":" + images[i].Length + "}");
            imagesSb.Append("{\"bufferView\":" + i + ",\"mimeType\":\"image/png\"}");
            texturesSb.Append("{\"source\":" + i + "}");
            currentOffset += images[i].Length;
        }

        var matPbrSb = new StringBuilder();
        matPbrSb.Append("\"baseColorTexture\":{\"index\":0}");
        if (includeMr)
        {
            matPbrSb.Append(",\"metallicRoughnessTexture\":{\"index\":" + mrImgIdx + "}");
        }

        var matSb = new StringBuilder();
        matSb.Append("{\"name\":\"PbrTestMat\",\"pbrMetallicRoughness\":{" + matPbrSb.ToString() + "}");
        if (includeOcclusion)
        {
            matSb.Append(",\"occlusionTexture\":{\"index\":" + occImgIdx + "}");
        }
        if (includeEmissive)
        {
            matSb.Append(",\"emissiveTexture\":{\"index\":" + emissImgIdx + "}");
        }
        matSb.Append("}");

        string gltfJson =
            "{" +
            "\"asset\":{\"version\":\"2.0\"}," +
            "\"bufferViews\":[" + bufferViewsSb + "]," +
            "\"images\":[" + imagesSb + "]," +
            "\"textures\":[" + texturesSb + "]," +
            "\"materials\":[" + matSb + "]," +
            "\"meshes\":[{\"primitives\":[{\"material\":0}]}]" +
            "}";

        byte[] jsonBytes = Encoding.UTF8.GetBytes(gltfJson);
        int jsonPad = (4 - (jsonBytes.Length % 4)) % 4;
        int jsonChunkLen = jsonBytes.Length + jsonPad;

        int binLen = images.Sum(img => img.Length);
        int binPad = (4 - (binLen % 4)) % 4;
        int binChunkLen = binLen + binPad;

        int totalLen = 12 + 8 + jsonChunkLen + 8 + binChunkLen;
        byte[] glbBytes = new byte[totalLen];
        BinaryPrimitives.WriteUInt32LittleEndian(glbBytes.AsSpan(0, 4), 0x46546C67u); // "glTF"
        BinaryPrimitives.WriteUInt32LittleEndian(glbBytes.AsSpan(4, 4), 2u);
        BinaryPrimitives.WriteUInt32LittleEndian(glbBytes.AsSpan(8, 4), (uint)totalLen);

        // Chunk 0: JSON
        BinaryPrimitives.WriteUInt32LittleEndian(glbBytes.AsSpan(12, 4), (uint)jsonChunkLen);
        BinaryPrimitives.WriteUInt32LittleEndian(glbBytes.AsSpan(16, 4), 0x4E4F534Au); // "JSON"
        Array.Copy(jsonBytes, 0, glbBytes, 20, jsonBytes.Length);
        for (int i = 0; i < jsonPad; i++)
        {
            glbBytes[20 + jsonBytes.Length + i] = (byte)' ';
        }

        // Chunk 1: BIN
        int binChunkPos = 20 + jsonChunkLen;
        BinaryPrimitives.WriteUInt32LittleEndian(glbBytes.AsSpan(binChunkPos, 4), (uint)binChunkLen);
        BinaryPrimitives.WriteUInt32LittleEndian(glbBytes.AsSpan(binChunkPos + 4, 4), 0x004E4942u); // "BIN\0"

        int writePos = binChunkPos + 8;
        foreach (byte[] img in images)
        {
            Array.Copy(img, 0, glbBytes, writePos, img.Length);
            writePos += img.Length;
        }

        return glbBytes;
    }

    [TestCase]
    public void TestGltfEmbeddedPbrExtractionAndOrmPacking()
    {
        byte[] glbWithPbr = BuildGlbWithPbr(includeMr: true, includeOcclusion: true, includeEmissive: true);
        byte[] baseMdl = BuildSyntheticMdl("PbrMesh", @"art\weapons\test_d.tex", @"art\weapons\test_s.tex", @"art\weapons\test_n.tex");

        var packageAdditions = new List<(string Path, byte[] Bytes)>();
        bool extractedOk = EditorModValueService.TryExtractAndApplyGltfEmbeddedTextures(
            glbWithPbr,
            baseDir: null,
            compiledMdl: baseMdl,
            modSlug: "pbrtestmod",
            nodeSlug: "pbr_item",
            packageAdditions: packageAdditions,
            out byte[] patchedMdl);

        AssertThat(extractedOk).IsTrue();

        // Must extract diffuse, normal (if present), emissive, AND pack mat0_diffuse_orm.png + mat0_orm.png
        AssertThat(packageAdditions.Any(p => p.Path == "mods/pbrtestmod/textures/pbr_item/mat0_diffuse.tex")).IsTrue();
        AssertThat(packageAdditions.Any(p => p.Path == "mods/pbrtestmod/textures/pbr_item/mat0_diffuse_orm.png")).IsTrue();
        AssertThat(packageAdditions.Any(p => p.Path == "mods/pbrtestmod/textures/pbr_item/mat0_orm.png")).IsTrue();
        AssertThat(packageAdditions.Any(p => p.Path == "mods/pbrtestmod/textures/pbr_item/mat0_selfillumination.tex")).IsTrue();

        // Check PNG signature on synthesized ORM
        var ormEntry = packageAdditions.First(p => p.Path == "mods/pbrtestmod/textures/pbr_item/mat0_diffuse_orm.png");
        AssertThat(ormEntry.Bytes.Length).IsGreaterEqual(8);
        AssertThat(ormEntry.Bytes[0]).IsEqual((byte)0x89);
        AssertThat(ormEntry.Bytes[1]).IsEqual((byte)0x50);
        AssertThat(ormEntry.Bytes[2]).IsEqual((byte)0x4E);
        AssertThat(ormEntry.Bytes[3]).IsEqual((byte)0x47);

        // Verify mdl table Diffuse was patched to scoped mod texture
        var matsAfter = Engine_MdlWriter.ExtractMaterials(patchedMdl);
        AssertThat(matsAfter.Count).IsEqual(1);
        AssertThat(matsAfter[0].Material.Diffuse).IsEqual(@"mods\pbrtestmod\textures\pbr_item\mat0_diffuse.tex");
    }

    [TestCase]
    public void TestGlbWithoutOrmDoesNotProduceOrmEntries()
    {
        byte[] glbWithoutPbr = BuildGlbWithPbr(includeMr: false, includeOcclusion: false, includeEmissive: false);
        byte[] baseMdl = BuildSyntheticMdl("CleanMesh", @"art\weapons\test_d.tex", @"art\weapons\test_s.tex", @"art\weapons\test_n.tex");

        var packageAdditions = new List<(string Path, byte[] Bytes)>();
        bool extractedOk = EditorModValueService.TryExtractAndApplyGltfEmbeddedTextures(
            glbWithoutPbr,
            baseDir: null,
            compiledMdl: baseMdl,
            modSlug: "cleanmod",
            nodeSlug: "clean_item",
            packageAdditions: packageAdditions,
            out byte[] patchedMdl);

        AssertThat(extractedOk).IsTrue();
        AssertThat(packageAdditions.Count).IsEqual(1);
        AssertThat(packageAdditions[0].Path).IsEqual("mods/cleanmod/textures/clean_item/mat0_diffuse.tex");
        AssertThat(packageAdditions.Any(p => p.Path.Contains("_orm"))).IsFalse();
    }

    [TestCase]
    public void TestResolveCompanionOrmPathProbing()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "refii_pbr_probe_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        string? prevModsRoot = EditorModValueService.ModsRootOverride;

        try
        {
            EditorModValueService.ModsRootOverride = tempRoot;
            string modName = "probemod";
            string pkgPath = Path.Combine(tempRoot, modName + ".f2mod");

            byte[] dummyOrmBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4 };
            var initialEntries = new List<(string Path, byte[] Bytes)>
            {
                ("mods/probemod/textures/sword/mat0_diffuse.tex", new byte[16]),
                ("mods/probemod/textures/sword/mat0_diffuse_orm.png", dummyOrmBytes)
            };
            F2ModPackage.Create(pkgPath, new ModMetadata { Title = "ProbeMod" }, initialEntries, out _);

            // Probing for companion ORM of mat0_diffuse.tex should find mat0_diffuse_orm.png
            string resolved = EditorModValueService.ResolveCompanionOrmPath(
                @"mods\probemod\textures\sword\mat0_diffuse.tex",
                modName);
            AssertThat(resolved).IsEqual(@"mods\probemod\textures\sword\mat0_diffuse_orm.png");

            // Probing for an unmodded vanilla texture without companion ORM should return empty
            string vanillaResolved = EditorModValueService.ResolveCompanionOrmPath(
                @"art\weapons\vanilla_d.tex",
                modName);
            AssertThat(vanillaResolved).IsEmpty();
        }
        finally
        {
            EditorModValueService.ModsRootOverride = prevModsRoot;
            try { Directory.Delete(tempRoot, true); } catch { }
        }
    }

    [TestCase]
    public void TestGetModelMaterialSlotsIncludesSlot5PbrOrm()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "refii_pbr_slots_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        string? prevModsRoot = EditorModValueService.ModsRootOverride;

        try
        {
            EditorModValueService.ModsRootOverride = tempRoot;
            string modName = "slotsmod";
            string pkgPath = Path.Combine(tempRoot, modName + ".f2mod");

            byte[] mdlBytes = BuildSyntheticMdl("SlotMesh", @"art\weapons\test_d.tex", @"art\weapons\test_s.tex", @"art\weapons\test_n.tex");
            string mdlVirtPath = @"mods\slotsmod\models\dagger\modelfile.mdl";
            var entries = new List<(string Path, byte[] Bytes)>
            {
                (mdlVirtPath.Replace('\\', '/'), mdlBytes)
            };
            F2ModPackage.Create(pkgPath, new ModMetadata { Title = "SlotsMod" }, entries, out _);

            var slots = EditorModValueService.GetModelMaterialSlots(mdlVirtPath, null, modName);

            // Must include SlotIndex == 5 ("PBR ORM")
            var pbrSlot = slots.FirstOrDefault(s => s.SlotIndex == 5);
            AssertThat(pbrSlot).IsNotNull();
            AssertThat(pbrSlot!.SlotName).IsEqual("PBR ORM");
            AssertThat(pbrSlot.VanillaTexturePath).IsEmpty();
            AssertThat(pbrSlot.CurrentTexturePath).IsEmpty();
            AssertThat(pbrSlot.IsModified).IsFalse();
        }
        finally
        {
            EditorModValueService.ModsRootOverride = prevModsRoot;
            try { Directory.Delete(tempRoot, true); } catch { }
        }
    }

    [TestCase]
    public void TestLinkSurfaceTextureToModelSlotPbrOrmAddAndRevert()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "refii_pbr_link_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        string? prevModsRoot = EditorModValueService.ModsRootOverride;
        string prevActiveMod = EditorModExporter.ActiveModName;

        try
        {
            EditorModValueService.ModsRootOverride = tempRoot;
            string modName = "linkpbrmod";
            EditorModExporter.ActiveModName = modName;
            string pkgPath = Path.Combine(tempRoot, modName + ".f2mod");

            byte[] mdlBytes = BuildSyntheticMdl("ShieldMesh", @"art\weapons\shield_d.tex", @"art\weapons\shield_s.tex", @"art\weapons\shield_n.tex");
            string mdlVirtPath = @"mods\linkpbrmod\models\shield\modelfile.mdl";
            var entries = new List<(string Path, byte[] Bytes)>
            {
                (mdlVirtPath.Replace('\\', '/'), mdlBytes)
            };
            F2ModPackage.Create(pkgPath, new ModMetadata { Title = "LinkPbrMod" }, entries, out _);

            // Create a loose _orm.png on disk
            string looseOrmDiskPath = Path.Combine(tempRoot, "authored_shield_orm.png");
            byte[] ormPng = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 255, 128, 64, 255 };
            File.WriteAllBytes(looseOrmDiskPath, ormPng);

            var modelProp = new ModItemProperty
            {
                PropertyName = "ModelFile",
                CurrentValue = mdlVirtPath,
                VanillaValue = @"art\weapons\shield.mdl",
                Type = GdbFieldType.String
            };

            // 1. Link authored ORM to slot 5 (PBR ORM)
            bool linked = EditorModValueService.LinkSurfaceTextureToModelSlot(
                targetNodeHash: 0x12345678,
                nodeLabel: "shield_node",
                modelProperty: modelProp,
                allComponentProperties: new[] { modelProp },
                flatMaterialIndex: 0,
                slotIndex: 5,
                slotName: "PBR ORM",
                sourceFilePath: looseOrmDiskPath,
                activeMod: modName,
                out string linkedTexVPath,
                out string linkedModelVPath,
                out string err);

            AssertThat(linked).IsTrue();
            AssertThat(err).IsEmpty();
            AssertThat(linkedTexVPath).EndsWith("mat0_diffuse_orm.png");

            // Verify ORM files are in package
            AssertThat(F2ModPackage.TryOpen(pkgPath, out var pkgAfterAdd, out _)).IsTrue();
            using (pkgAfterAdd)
            {
                AssertThat(pkgAfterAdd!.Entries.Any(e => e.Path.EndsWith("mat0_diffuse_orm.png"))).IsTrue();
                AssertThat(pkgAfterAdd.Entries.Any(e => e.Path.EndsWith("mat0_orm.png"))).IsTrue();
            }

            // Verify GetModelMaterialSlots reflects modified PBR ORM slot
            var slotsAfterAdd = EditorModValueService.GetModelMaterialSlots(modelProp.CurrentValue, null, modName);
            var ormSlotAfterAdd = slotsAfterAdd.First(s => s.SlotIndex == 5);
            AssertThat(ormSlotAfterAdd.CurrentTexturePath).IsEqual(linkedTexVPath);
            AssertThat(ormSlotAfterAdd.IsModified).IsTrue();

            // 2. Revert slot 5 by passing empty sourceFilePath
            bool reverted = EditorModValueService.LinkSurfaceTextureToModelSlot(
                targetNodeHash: 0x12345678,
                nodeLabel: "shield_node",
                modelProperty: modelProp,
                allComponentProperties: new[] { modelProp },
                flatMaterialIndex: 0,
                slotIndex: 5,
                slotName: "PBR ORM",
                sourceFilePath: string.Empty,
                activeMod: modName,
                out string revertedTexVPath,
                out _,
                out string revertErr);

            AssertThat(reverted).IsTrue();
            AssertThat(revertErr).IsEmpty();
            AssertThat(revertedTexVPath).IsEmpty();

            // Verify ORM files were removed from package
            AssertThat(F2ModPackage.TryOpen(pkgPath, out var pkgAfterRevert, out _)).IsTrue();
            using (pkgAfterRevert)
            {
                AssertThat(pkgAfterRevert!.Entries.Any(e => e.Path.EndsWith("mat0_diffuse_orm.png"))).IsFalse();
                AssertThat(pkgAfterRevert.Entries.Any(e => e.Path.EndsWith("mat0_orm.png"))).IsFalse();
            }

            // Verify GetModelMaterialSlots reflects cleared PBR ORM slot
            var slotsAfterRevert = EditorModValueService.GetModelMaterialSlots(modelProp.CurrentValue, null, modName);
            var ormSlotAfterRevert = slotsAfterRevert.First(s => s.SlotIndex == 5);
            AssertThat(ormSlotAfterRevert.CurrentTexturePath).IsEmpty();
            AssertThat(ormSlotAfterRevert.IsModified).IsFalse();
        }
        finally
        {
            EditorModValueService.ModsRootOverride = prevModsRoot;
            EditorModExporter.ActiveModName = prevActiveMod;
            try { Directory.Delete(tempRoot, true); } catch { }
        }
    }

    [TestCase]
    public void TestDynamicMaterialSlotExpansionAndMultiSectionWriting()
    {
        byte[] baseMdl = BuildSyntheticMdl("ToyGun", @"art\weapons\toy_d.tex", @"art\weapons\toy_s.tex", @"art\weapons\toy_n.tex", lodCount: 1);
        var baseMats = Engine_MdlWriter.ExtractMaterials(baseMdl);
        AssertThat(baseMats.Count).IsEqual(1);

        // 1. Ensure material slots expands from 1 to 3:
        bool ok = Engine_MdlWriter.TryEnsureMaterialSlots(baseMdl, 3, out byte[] expMdl, out string expandErr);
        AssertThat(ok).IsTrue();
        AssertThat(expandErr).IsEmpty();

        var expInfo = new MdlInfo();
        AssertThat(Engine_MdlEngine.TryParse(expMdl, expInfo)).IsTrue();
        AssertThat(expInfo.Meshes[0].Materials.Count).IsEqual(3);

        var expMats = Engine_MdlWriter.ExtractMaterials(expMdl);
        AssertThat(expMats.Count).IsEqual(3);
        AssertThat(expMats[0].FlatIndex).IsEqual(0);
        AssertThat(expMats[1].FlatIndex).IsEqual(1);
        AssertThat(expMats[2].FlatIndex).IsEqual(2);

        // Verify slot 0 preserved its texture paths, and slot 1/2 are clean:
        AssertThat(expMats[0].Material.Diffuse).IsEqual(@"art\weapons\toy_d.tex");
        AssertThat(expMats[1].Material.Diffuse).IsEmpty();
        AssertThat(expMats[2].Material.Diffuse).IsEmpty();

        // 2. Test multi-section mesh replacement:
        float[] positions = new float[]
        {
            // Section 0 (surface 0, 3 vertices)
            0f, 0f, 0f,   1f, 0f, 0f,   0f, 1f, 0f,
            // Section 1 (surface 1, 3 vertices)
            2f, 0f, 0f,   3f, 0f, 0f,   2f, 1f, 0f,
        };
        float[] uvs = new float[]
        {
            0f, 0f,  1f, 0f,  0f, 1f,
            0f, 0f,  1f, 0f,  0f, 1f,
        };
        float[] normals = new float[]
        {
            0f, 0f, 1f,  0f, 0f, 1f,  0f, 0f, 1f,
            0f, 0f, 1f,  0f, 0f, 1f,  0f, 0f, 1f,
        };
        float[] tangents = new float[]
        {
            1f, 0f, 0f, 1f,  1f, 0f, 0f, 1f,  1f, 0f, 0f, 1f,
            1f, 0f, 0f, 1f,  1f, 0f, 0f, 1f,  1f, 0f, 0f, 1f,
        };
        ushort[] indices = new ushort[] { 0, 1, 2, 3, 4, 5 };

        var sections = new List<MdlSectionSpec>
        {
            new() { MaterialIndex = 0, StartIndex = 0, TriangleCount = 1 },
            new() { MaterialIndex = 1, StartIndex = 3, TriangleCount = 1 },
        };

        bool meshOk = Engine_MdlWriter.TryReplaceMesh(
            expMdl, expInfo, 0,
            positions, uvs, normals, tangents, indices, sections,
            out byte[] replacedMdl, out string meshErr);

        AssertThat(meshOk).IsTrue();
        AssertThat(meshErr).IsEmpty();

        var repInfo = new MdlInfo();
        AssertThat(Engine_MdlEngine.TryParse(replacedMdl, repInfo)).IsTrue();
        AssertThat(repInfo.MeshBuffers[0].SubMeshes.Count).IsEqual(2);
        AssertThat(repInfo.MeshBuffers[0].SubMeshes[0].MaterialIndex).IsEqual(0);
        AssertThat(repInfo.MeshBuffers[0].SubMeshes[0].FaceCount).IsEqual(1u);
        AssertThat(repInfo.MeshBuffers[0].SubMeshes[1].MaterialIndex).IsEqual(1);
        AssertThat(repInfo.MeshBuffers[0].SubMeshes[1].FaceCount).IsEqual(1u);
        AssertThat(repInfo.MeshBuffers[0].SubMeshes[1].StartIndex).IsEqual(3u);

        // 3. Test patching textures on slot 1:
        var patches = new Dictionary<int, MdlMaterialTexturePatch>
        {
            [1] = new MdlMaterialTexturePatch
            {
                Diffuse = @"mods\testmod\textures\mat1_diffuse.tex",
                Normal = @"mods\testmod\textures\mat1_normal.tex"
            }
        };

        bool patchOk = Engine_MdlWriter.TryRewriteMaterialTextures(
            replacedMdl, patches, out byte[] patchedMdl, out string patchErr);
        AssertThat(patchOk).IsTrue();
        AssertThat(patchErr).IsEmpty();

        var patchedMats = Engine_MdlWriter.ExtractMaterials(patchedMdl);
        AssertThat(patchedMats[1].Material.Diffuse).IsEqual(@"mods\testmod\textures\mat1_diffuse.tex");
        AssertThat(patchedMats[1].Material.Normal).IsEqual(@"mods\testmod\textures\mat1_normal.tex");
    }

    [TestCase]
    public void TestAddMaterialSlotToModel()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "refii_add_slot_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        string? prevModsRoot = EditorModValueService.ModsRootOverride;
        string prevActiveMod = EditorModExporter.ActiveModName;

        try
        {
            EditorModValueService.ModsRootOverride = tempRoot;
            string modName = "addslotmod";
            EditorModExporter.ActiveModName = modName;
            string pkgPath = Path.Combine(tempRoot, modName + ".f2mod");

            byte[] mdlBytes = BuildSyntheticMdl("PistolMesh", @"art\weapons\pistol_d.tex", @"art\weapons\pistol_s.tex", @"art\weapons\pistol_n.tex");
            string mdlVirtPath = @"art\weapons\pistol.mdl";

            var modelProp = new ModItemProperty
            {
                PropertyName = "ModelFile",
                CurrentValue = mdlVirtPath,
                VanillaValue = mdlVirtPath,
                Type = GdbFieldType.String
            };

            var fs = new Refii.Bnk.BnkFileSystem();
            fs.AddVirtualFile(mdlVirtPath.Replace('\\', '/'), "base", mdlBytes.Length, () => mdlBytes);

            bool added = EditorModValueService.AddMaterialSlotToModel(
                targetNodeHash: 0x99887766,
                nodeLabel: "pistol_node",
                modelProperty: modelProp,
                allComponentProperties: new[] { modelProp },
                out string linkedModelPath,
                out string err,
                activeMod: modName,
                filesystem: fs);

            AssertThat(added).IsTrue();
            AssertThat(err).IsEmpty();
            AssertThat(linkedModelPath).StartsWith(@"mods\addslotmod\models\pistol_node\");

            // Verify GetModelMaterialSlots now has 2 material slots (8 texture slots total):
            var slots = EditorModValueService.GetModelMaterialSlots(modelProp.CurrentValue, modelProp.VanillaValue, modName, fs);
            AssertThat(slots.Any(s => s.FlatMaterialIndex == 0)).IsTrue();
            AssertThat(slots.Any(s => s.FlatMaterialIndex == 1)).IsTrue();
            AssertThat(slots.Count(s => s.SlotIndex == 5)).IsEqual(2); // Mat 0 and Mat 1 PBR ORM slots
        }
        finally
        {
            EditorModValueService.ModsRootOverride = prevModsRoot;
            EditorModExporter.ActiveModName = prevActiveMod;
            try { Directory.Delete(tempRoot, true); } catch { }
        }
    }

    [TestCase]
    public void TestMultiMaterialGltfEmbeddedTextureExtractionAndPatching()
    {
        byte[] dummyAlbedo0 = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 10, 20, 30, 40 };
        byte[] dummyMr0 = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 50, 60, 70, 80 };
        byte[] dummyAlbedo1 = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 11, 21, 31, 41 };
        byte[] dummyMr1 = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 51, 61, 71, 81 };

        var images = new List<byte[]> { dummyAlbedo0, dummyMr0, dummyAlbedo1, dummyMr1 };

        var bufferViewsSb = new StringBuilder();
        var imagesSb = new StringBuilder();
        var texturesSb = new StringBuilder();

        int currentOffset = 0;
        for (int i = 0; i < images.Count; i++)
        {
            if (i > 0)
            {
                bufferViewsSb.Append(",");
                imagesSb.Append(",");
                texturesSb.Append(",");
            }
            bufferViewsSb.Append("{\"buffer\":0,\"byteOffset\":" + currentOffset + ",\"byteLength\":" + images[i].Length + "}");
            imagesSb.Append("{\"bufferView\":" + i + ",\"mimeType\":\"image/png\"}");
            texturesSb.Append("{\"source\":" + i + "}");
            currentOffset += images[i].Length;
        }

        string mat0Json = "{\"name\":\"Mat0\",\"pbrMetallicRoughness\":{\"baseColorTexture\":{\"index\":0},\"metallicRoughnessTexture\":{\"index\":1}}}";
        string mat1Json = "{\"name\":\"Mat1\",\"pbrMetallicRoughness\":{\"baseColorTexture\":{\"index\":2},\"metallicRoughnessTexture\":{\"index\":3}}}";

        string gltfJson =
            "{" +
            "\"asset\":{\"version\":\"2.0\"}," +
            "\"bufferViews\":[" + bufferViewsSb + "]," +
            "\"images\":[" + imagesSb + "]," +
            "\"textures\":[" + texturesSb + "]," +
            "\"materials\":[" + mat0Json + "," + mat1Json + "]," +
            "\"meshes\":[{\"primitives\":[{\"material\":0},{\"material\":1}]}]" +
            "}";

        byte[] jsonBytes = Encoding.UTF8.GetBytes(gltfJson);
        int jsonPad = (4 - (jsonBytes.Length % 4)) % 4;
        int jsonChunkLen = jsonBytes.Length + jsonPad;

        int binLen = images.Sum(img => img.Length);
        int binPad = (4 - (binLen % 4)) % 4;
        int binChunkLen = binLen + binPad;

        int totalLen = 12 + 8 + jsonChunkLen + 8 + binChunkLen;
        byte[] glbBytes = new byte[totalLen];
        BinaryPrimitives.WriteUInt32LittleEndian(glbBytes.AsSpan(0, 4), 0x46546C67u);
        BinaryPrimitives.WriteUInt32LittleEndian(glbBytes.AsSpan(4, 4), 2u);
        BinaryPrimitives.WriteUInt32LittleEndian(glbBytes.AsSpan(8, 4), (uint)totalLen);
        BinaryPrimitives.WriteUInt32LittleEndian(glbBytes.AsSpan(12, 4), (uint)jsonChunkLen);
        BinaryPrimitives.WriteUInt32LittleEndian(glbBytes.AsSpan(16, 4), 0x4E4F534Au);
        Array.Copy(jsonBytes, 0, glbBytes, 20, jsonBytes.Length);
        for (int i = 0; i < jsonPad; i++) glbBytes[20 + jsonBytes.Length + i] = (byte)' ';

        int binChunkPos = 20 + jsonChunkLen;
        BinaryPrimitives.WriteUInt32LittleEndian(glbBytes.AsSpan(binChunkPos, 4), (uint)binChunkLen);
        BinaryPrimitives.WriteUInt32LittleEndian(glbBytes.AsSpan(binChunkPos + 4, 4), 0x004E4942u);
        int writePos = binChunkPos + 8;
        foreach (byte[] img in images)
        {
            Array.Copy(img, 0, glbBytes, writePos, img.Length);
            writePos += img.Length;
        }

        // Test with a 1-material template .mdl (like vanilla toy gun)
        byte[] baseMdl1Mat = BuildSyntheticMdl("ToyGun1Mat", @"art\weapons\vanilla_d.tex", @"art\weapons\vanilla_s.tex", @"art\weapons\vanilla_n.tex", lodCount: 1);
        AssertThat(Engine_MdlWriter.ExtractMaterials(baseMdl1Mat).Count).IsEqual(1);

        var additions = new List<(string Path, byte[] Bytes)>();
        bool ok = EditorModValueService.TryExtractAndApplyGltfEmbeddedTextures(
            glbBytes, null, baseMdl1Mat, "multimatmod", "toygun_node", additions, out byte[] patchedMdl);

        AssertThat(ok).IsTrue();
        // Verify patchedMdl was dynamically expanded to 2 materials:
        var patchedMats = Engine_MdlWriter.ExtractMaterials(patchedMdl);
        AssertThat(patchedMats.Count).IsEqual(2);
        AssertThat(patchedMats[0].Material.Diffuse).EndsWith("mat0_diffuse.tex");
        AssertThat(patchedMats[1].Material.Diffuse).EndsWith("mat1_diffuse.tex");

        // Verify package additions include both mat0 and mat1 diffuse and ORMs:
        AssertThat(additions.Any(a => a.Path.EndsWith("mat0_diffuse.tex"))).IsTrue();
        AssertThat(additions.Any(a => a.Path.EndsWith("mat0_diffuse_orm.png"))).IsTrue();
        AssertThat(additions.Any(a => a.Path.EndsWith("mat1_diffuse.tex"))).IsTrue();
        AssertThat(additions.Any(a => a.Path.EndsWith("mat1_diffuse_orm.png"))).IsTrue();
    }

    [TestCase]
    public void TestGltfEmbeddedEmissionTextureExtraction()
    {
        byte[] dummyPng = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 255, 128, 0, 255 }; // Orange emissive glow
        byte[] diffusePng = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 50, 50, 50, 255 };
        byte[] ormPng = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 255, 128, 0, 255 };

        var images = new List<byte[]> { diffusePng, ormPng, dummyPng };
        string gltfJson =
            "{\"asset\":{\"version\":\"2.0\"}," +
            "\"buffers\":[{\"byteLength\":" + images.Sum(i => i.Length) + "}]," +
            "\"bufferViews\":[" +
            "{\"buffer\":0,\"byteOffset\":0,\"byteLength\":" + diffusePng.Length + "}," +
            "{\"buffer\":0,\"byteOffset\":" + diffusePng.Length + ",\"byteLength\":" + ormPng.Length + "}," +
            "{\"buffer\":0,\"byteOffset\":" + (diffusePng.Length + ormPng.Length) + ",\"byteLength\":" + dummyPng.Length + "}" +
            "]," +
            "\"images\":[{\"bufferView\":0,\"mimeType\":\"image/png\"},{\"bufferView\":1,\"mimeType\":\"image/png\"},{\"bufferView\":2,\"mimeType\":\"image/png\"}]," +
            "\"textures\":[{\"source\":0},{\"source\":1},{\"source\":2}]," +
            "\"materials\":[{" +
            "\"name\":\"EmissiveMat\"," +
            "\"pbrMetallicRoughness\":{\"baseColorTexture\":{\"index\":0},\"metallicRoughnessTexture\":{\"index\":1}}," +
            "\"emissiveTexture\":{\"index\":2}," +
            "\"emissiveFactor\":[1.0,0.5,0.0]" +
            "}]," +
            "\"meshes\":[{\"primitives\":[{\"material\":0}]}]" +
            "}";

        byte[] jsonBytes = Encoding.UTF8.GetBytes(gltfJson);
        int jsonPad = (4 - (jsonBytes.Length % 4)) % 4;
        int jsonChunkLen = jsonBytes.Length + jsonPad;

        int binLen = images.Sum(img => img.Length);
        int binPad = (4 - (binLen % 4)) % 4;
        int binChunkLen = binLen + binPad;

        int totalLen = 12 + 8 + jsonChunkLen + 8 + binChunkLen;
        byte[] glbBytes = new byte[totalLen];
        BinaryPrimitives.WriteUInt32LittleEndian(glbBytes.AsSpan(0, 4), 0x46546C67u);
        BinaryPrimitives.WriteUInt32LittleEndian(glbBytes.AsSpan(4, 4), 2u);
        BinaryPrimitives.WriteUInt32LittleEndian(glbBytes.AsSpan(8, 4), (uint)totalLen);
        BinaryPrimitives.WriteUInt32LittleEndian(glbBytes.AsSpan(12, 4), (uint)jsonChunkLen);
        BinaryPrimitives.WriteUInt32LittleEndian(glbBytes.AsSpan(16, 4), 0x4E4F534Au);
        Array.Copy(jsonBytes, 0, glbBytes, 20, jsonBytes.Length);
        for (int i = 0; i < jsonPad; i++) glbBytes[20 + jsonBytes.Length + i] = (byte)' ';

        int binChunkPos = 20 + jsonChunkLen;
        BinaryPrimitives.WriteUInt32LittleEndian(glbBytes.AsSpan(binChunkPos, 4), (uint)binChunkLen);
        BinaryPrimitives.WriteUInt32LittleEndian(glbBytes.AsSpan(binChunkPos + 4, 4), 0x004E4942u);
        int writePos = binChunkPos + 8;
        foreach (byte[] img in images)
        {
            Array.Copy(img, 0, glbBytes, writePos, img.Length);
            writePos += img.Length;
        }

        byte[] baseMdl = BuildSyntheticMdl("GlowItem", @"art\weapons\vanilla_d.tex", @"art\weapons\vanilla_s.tex", @"art\weapons\vanilla_n.tex", lodCount: 1);
        var additions = new List<(string Path, byte[] Bytes)>();
        bool ok = EditorModValueService.TryExtractAndApplyGltfEmbeddedTextures(
            glbBytes, null, baseMdl, "glowmod", "glowitem_node", additions, out byte[] patchedMdl);

        AssertThat(ok).IsTrue();
        var patchedMats = Engine_MdlWriter.ExtractMaterials(patchedMdl);
        AssertThat(patchedMats.Count).IsEqual(1);
        AssertThat(patchedMats[0].Material.SelfIllumination).EndsWith("mat0_emission.tex");

        // Verify package additions contain emission stream:
        AssertThat(additions.Any(a => a.Path.EndsWith("mat0_emission.tex"))).IsTrue();
        AssertThat(additions.Any(a => a.Path.EndsWith("mat0_selfillumination.tex"))).IsTrue();
    }

    [TestCase]
    public void TestLinkAndRevertEmissionTextureSlot()
    {
        byte[] baseMdl = BuildSyntheticMdl("ToyGun", @"art\weapons\vanilla_d.tex", @"art\weapons\vanilla_s.tex", @"art\weapons\vanilla_n.tex", lodCount: 1);
        var vanillaMats = Engine_MdlWriter.ExtractMaterials(baseMdl);
        AssertThat(string.IsNullOrEmpty(vanillaMats[0].Material.SelfIllumination)).IsTrue();

        // 1. Patch Emission slot (slot 4) with a new virtual texture path:
        var patch = new Dictionary<int, MdlMaterialTexturePatch>
        {
            [0] = new MdlMaterialTexturePatch { SelfIllumination = @"mods\testmod\textures\toygun\mat0_emission.tex" }
        };
        bool patchOk = Engine_MdlWriter.TryRewriteMaterialTextures(baseMdl, patch, out byte[] patchedMdl, out string patchErr);
        AssertThat(patchOk).IsTrue();

        var patchedMats = Engine_MdlWriter.ExtractMaterials(patchedMdl);
        AssertThat(patchedMats[0].Material.SelfIllumination).IsEqual(@"mods\testmod\textures\toygun\mat0_emission.tex");

        // 2. Clear / unbind Emission slot with empty string:
        var revertPatch = new Dictionary<int, MdlMaterialTexturePatch>
        {
            [0] = new MdlMaterialTexturePatch { SelfIllumination = string.Empty }
        };
        bool revertOk = Engine_MdlWriter.TryRewriteMaterialTextures(patchedMdl, revertPatch, out byte[] revertedMdl, out string revertErr);
        AssertThat(revertOk).IsTrue();

        var revertedMats = Engine_MdlWriter.ExtractMaterials(revertedMdl);
        AssertThat(string.IsNullOrEmpty(revertedMats[0].Material.SelfIllumination)).IsTrue();
    }
}
