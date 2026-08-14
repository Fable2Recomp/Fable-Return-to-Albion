using System;
using System.Collections.Generic;

namespace Refii.Models;

public sealed class MdlBone
{
    public string Name { get; set; } = string.Empty;

    public int ParentId { get; set; } = -1;
}

public sealed class MdlMaterial
{
    // The record is nine NUL-terminated strings scattered into a 180-byte texture-set record in
    // an order that is NOT the file order: strings 1,2,3 to +4,+8,+12, then 5,6,7 to
    // +16,+20,+24, and string 4 out to +148. Strings 8 and 9 are read and immediately
    // destructed; nothing in the shipped data fills them.
    //
    //     +4    g_DiffuseTextureSampler
    //     +8    g_SpecularTextureSampler
    //     +12   g_NormalTextureSampler
    //     +16   g_SelfIllumTextureSampler
    //     +20   secondary diffuse
    //     +24   tree trunk
    //     +148  g_EnvmapSampler

    public string Diffuse { get; set; } = string.Empty;

    public string Specular { get; set; } = string.Empty;

    public string Normal { get; set; } = string.Empty;

    /// <summary>File string 4 — the ENVIRONMENT MAP, a cube map bound to s14.</summary>
    /// <remarks>
    /// It was called <c>Metallic</c>, which it is not. It lands at record+148, outside the
    /// presence mask and with no sampler block of its own, and is bound unconditionally at the
    /// tail of the mask walk through <c>g_ShaderBindingTable[84]</c>. Its presence is published
    /// to the shader as pixel bool c6 — <c>g_NeedsEnvmapping</c> — by `sub_82220CA8`:
    /// <c>82220ed8 lwz r8, 0x94(r9)</c> (record+148), tested non-zero, written with
    /// <c>li r4, 6</c> to <c>D3DDevice_SetPixelShaderConstantB</c>. 526 of 9,293 shipped material
    /// records name one.
    /// </remarks>
    public string EnvironmentMap { get; set; } = string.Empty;

    /// <summary>File string 5 — the SELF-ILLUMINATION map, bound to s13.</summary>
    /// <remarks>
    /// It was called <c>Extra</c> and bound as the emission texture on no evidence; the guess was
    /// right. It is mask bit 3, so `Render_TextureStateHelper_INFERRED_4(record, 3)` binds it
    /// through <c>g_ShaderBindingTable[211]</c>, and its presence bit becomes pixel bool c5 —
    /// <c>g_NeedsSelfIllumination</c> — at <c>82220E48 extrwi r9, r10, 1,28</c>.
    ///
    /// No shader-permutation builder tests bit 3, which is what made this slot look dead: self
    /// illumination is a runtime branch on a bool constant, not a compile-time define, unlike
    /// bits 4 and 5 which do select permutations. Only 102 of 9,293 records name one.
    /// </remarks>
    public string SelfIllumination { get; set; } = string.Empty;

    public uint ShaderType { get; set; }

    /// <summary>File strings 6, 7, 8 and 9 — NOT shader parameters.</summary>
    /// <remarks>
    /// [0] is the secondary diffuse (record+20, whose mask bit selects the
    /// <c>SpecNormalSecDifParams_SDif</c> permutation), [1] is the slot whose ABSENCE selects
    /// <c>SpecNormalSecDifParams_Trunk</c> (record+24), and [2] and [3] are the two strings the
    /// guest reads and throws away. The shipped data fills none of them: 0, 4, 0 and 0 records
    /// respectively out of 9,293. The name is kept only because nothing consumes them.
    /// </remarks>
    public string[] ShaderParams { get; set; } = Array.Empty<string>();

    public float[] MaterialFloats { get; set; } = Array.Empty<float>();

    /// <summary>
    /// The specular exponent this material uses where its specular map's alpha is 0.
    /// </summary>
    /// <remarks>
    /// The shipped pixel shader's `_sp2` variants compute the exponent per pixel:
    /// <c>power = lerp(c24.x, c24.y, specularTexture.a)</c>, built as
    /// <c>r0.z = c24.y - c24.x; r2.w = r0.z * specularTex.w + c24.x</c>. c24.x and c24.y are
    /// packed from material+60 and material+64, which <c>sub_82B243E8</c> fills from the first two
    /// floats of the material's scalar block. Engine defaults are 1.0 and 20.0
    /// (<c>sub_82B241A0</c>), but no shipped material uses them: all 9,293 records state 1.0 here
    /// and a range of 99.0, so the pair is (1, 100) everywhere and the exponent comes entirely
    /// from the specular map's alpha.
    /// </remarks>
    public float SpecularPowerMin { get; set; } = 1.0f;

    /// <summary>The specular exponent where the specular map's alpha is 1.</summary>
    /// <remarks>
    /// THE FILE STORES A RANGE, NOT A MAXIMUM. `sub_82B243E8` never stores the second float raw —
    /// it only ever adds it (<c>82b24ac0 fadds f13, f0, f31</c>, <c>82b24ac8 stfs f13, 0x40(r30)</c>),
    /// so this is <c>file[0] + file[4]</c>. Reading the pair as a min and a max would give a
    /// material authored (4, 60) an exponent ceiling of 60 instead of 64.
    /// </remarks>
    public float SpecularPowerMax { get; set; } = 20.0f;

    /// <summary>How much of the self-illumination texture is added: the shader's c24.z.</summary>
    /// <remarks>
    /// Block offset 29, stored to material+72 (<c>82b24954 stfs f0, 0x48(r30)</c>) and packed into
    /// c24.z, where the shader uses it as
    /// <c>colour = selfIllumTex.rgb^2 * g_StandardMaterialPSConstants(0).zzz + colour</c>.
    ///
    /// Exactly 1.0 on all 9,293 shipped records, so the map's own intensity carries the effect.
    /// </remarks>
    public float SelfIlluminationScale { get; set; } = 1.0f;
}

public sealed class MdlMesh
{
    public string Name { get; set; } = string.Empty;

    public List<MdlMaterial> Materials { get; } = new();
}

public sealed class MdlSubMesh
{
    public uint RegionIndex { get; set; }

    public uint FaceCount { get; set; }

    public uint StartIndex { get; set; }

    public byte MaterialIndex { get; set; }

    public byte PrimitiveType { get; set; }

    public float[] BoundingBox { get; set; } = Array.Empty<float>();
}

public sealed class MdlMeshBuffer
{
    public uint VertexCount { get; set; }

    public int VertexOffset { get; set; }

    public uint FaceCount { get; set; }

    public int FaceOffset { get; set; }

    public uint MeshIndex { get; set; }

    public int UvBufferOffset { get; set; }

    public uint UvBufferStride { get; set; }

    public bool IsAltPath { get; set; }

    public bool IsFoliagePath { get; set; }

    public int FoliageVertexStride { get; set; }

    public bool IsEngineSkinned { get; set; }

    public bool IsEngineRigid { get; set; }

    public List<MdlSubMesh> SubMeshes { get; } = new();
}

public sealed class MdlInfo
{
    public string Magic { get; set; } = string.Empty;

    public uint HeaderSize { get; set; }

    public List<MdlBone> Bones { get; } = new();

    public bool HasBoneTransforms { get; set; }

    public List<float[]> BoneTransforms { get; } = new();

    public uint MeshCount { get; set; }

    public List<MdlMesh> Meshes { get; } = new();

    public List<MdlMeshBuffer> MeshBuffers { get; } = new();

    public List<string> HideRegions { get; } = new();

    public bool HasTreeTag { get; set; }

    public bool IsStub { get; set; }

    /// <summary>
    /// Bytes of LOD payload the 104-byte file header says follow it, whether or not they are
    /// there. Nonzero on a stub means the header is intact and the BODY BANK ENTRY is missing —
    /// a gap in the shipped data, not a parse that went wrong.
    /// </summary>
    public long BodyBytesStated { get; set; }

    public bool IsEngineFormat { get; set; }

    public uint Version { get; set; }

    public uint LodCount { get; set; }

    public uint[] LodSizes { get; set; } = Array.Empty<uint>();

    public float[] FileBoundingSphere { get; set; } = Array.Empty<float>();

    public float[] FileBoundingBox { get; set; } = Array.Empty<float>();

    public uint RigidCount { get; set; }

    public uint SkinnedCount { get; set; }

    public byte SectionFlag { get; set; }

    public List<string> Tags { get; } = new();

    public uint[] ShDescriptor { get; set; } = Array.Empty<uint>();

    public float[] ShProbeData { get; set; } = Array.Empty<float>();

    public List<MdlClothBlock> ClothBlocks { get; } = new();

    public List<MdlMorphTarget> MorphTargets { get; } = new();
}

public sealed class MdlClothBlock
{
    public uint RecordIndex { get; set; }

    public uint MeshIndex { get; set; }

    public int BlockOffset { get; set; }

    public int BlockSize { get; set; }

    public uint SimVertexCount { get; set; }

    public uint TriangleCount { get; set; }

    public uint IndexCount { get; set; }

    public uint EdgeCount { get; set; }

    public uint BonesPerVertex { get; set; }

    public uint DistConstraints { get; set; }

    public uint BendConstraintsA { get; set; }

    public uint BendConstraintsB { get; set; }

    public uint VolConstraints { get; set; }

    public uint LinkConstraints { get; set; }

    public float[] SimParams { get; set; } = new float[6];

    public int RestPosOffset { get; set; }

    public int IndexListOffset { get; set; }

    public int BoneIdOffset { get; set; }

    public int WeightOffset { get; set; }

    public int FlagOffset { get; set; }
}

public sealed class MdlMorphTarget
{
    public uint RecordIndex { get; set; }

    public uint Header0 { get; set; }

    public uint Header1 { get; set; }

    public uint Count { get; set; }

    public int DataOffset { get; set; }
}

public static class Engine_MdlData
{
    public static bool TryParseInfo(byte[] data, out MdlInfo info, out string error) =>
        TryParseInfo(data, string.Empty, out info, out error);

    public static bool TryParseInfo(
        byte[] data, string virtualPath, out MdlInfo info, out string error)
    {
        info = new MdlInfo();
        error = string.Empty;

        if (Engine_MdlEngine.TryParse(data, info))
        {
            return true;
        }

        // A HEADER WITH NO BODY IS A COMPLETE READ, NOT A FAILED ONE.
        //
        // Every .mdl is two bank entries under one name — the 104-byte header, always in
        // `globals_model_headers.bnk`, and a body in `globals_models.bnk` or a region's
        // `<scenario>_models.bnk`. The bank filesystem stitches them the way it stitches a .tex.
        // 1,111 of the 5,446 header names in the shipped data have NO body entry in any bank,
        // so what arrives here is the header alone, and no amount of parsing will find geometry
        // in it. Saying so, with the byte count the header states, is what stops the next reader
        // of this code from going looking for a decoder bug that is not there.
        info = new MdlInfo();
        if (Engine_MdlEngine.TryReadFileHeader(data, info, out long stated) &&
            data.Length <= Engine_MdlEngine.BodyStart)
        {
            info.IsStub = true;
            info.BodyBytesStated = stated;
            return true;
        }

        info = new MdlInfo();

        if (!TryParseHeader(data, virtualPath, out info, out error))
        {
            if (!Engine_MdlReparse.TryMissingBuffers(data, info) &&
                !Engine_MdlReparse.TryFoliage48(data, info))
            {
                return false;
            }

            error = string.Empty;
        }

        Engine_MdlReparse.Apply(data, info, virtualPath);
        return true;
    }

    private static bool TryParseHeader(
        byte[] data, string virtualPath, out MdlInfo info, out string error)
    {
        info = new MdlInfo();
        error = string.Empty;
        bool isFoliage = Engine_MdlFoliage.IsFoliagePath(virtualPath);

        if (data.Length < 8)
        {
            error = "too small for an .mdl header";
            return false;
        }

        var r = new MdlReader(data);

        string magic = System.Text.Encoding.ASCII.GetString(data, 0, 8);
        if (magic == "MeshFile")
        {
            info.Magic = magic;
            r.Position = 8;
            if (!r.U32(out _)) return Fail("truncated after magic", out error);
            if (!r.U32(out uint headerSize)) return Fail("truncated header size", out error);
            info.HeaderSize = headerSize;
            if (!r.Skip(88)) return Fail("truncated header block", out error);
        }
        else
        {
            r.Position = 0;
        }

        if (r.Position >= data.Length)
        {
            info.IsStub = true;
            return true;
        }

        if (!r.Skip(8 * 4)) return Fail("truncated pre-bone block", out error);

        if (!r.U32(out uint boneCount)) return Fail("truncated bone count", out error);
        for (uint i = 0; i < boneCount; ++i)
        {
            if (!r.Strz(out string name)) return Fail("truncated bone name", out error);
            if (!r.U32(out uint parent)) return Fail("truncated bone parent", out error);
            info.Bones.Add(new MdlBone
            {
                Name = name,
                ParentId = parent == 0xFFFFFFFFu ? -1 : (int)parent,
            });
        }

        if (!r.U32(out uint transformCount)) return Fail("truncated transform count", out error);
        if (transformCount == boneCount && boneCount > 0)
        {
            for (uint i = 0; i < transformCount; ++i)
            {
                var tf = new float[11];
                for (int k = 0; k < 11; ++k)
                {
                    if (!r.F32(out tf[k])) return Fail("truncated bone transform", out error);
                }

                info.BoneTransforms.Add(tf);
            }

            info.HasBoneTransforms = true;
        }
        else
        {
            uint m = Math.Min(transformCount, 65535u);
            if (!r.Skip((int)(m * 44))) return Fail("truncated transform block", out error);
            info.HasBoneTransforms = false;
        }

        for (int k = 0; k < 10; ++k)
        {
            if (!r.F32(out _)) return Fail("truncated bounds block", out error);
        }

        if (!r.U32(out uint meshCount)) return Fail("truncated mesh count", out error);
        info.MeshCount = meshCount;
        if (!r.Skip(2 * 4)) return Fail("truncated post-mesh-count", out error);

        if (!r.Skip(12)) return Fail("truncated pre-tag block", out error);
        ReadOptionalTagBlock(r, info);

        if (!r.Skip(5 * 4)) return Fail("truncated post-tag block", out error);

        if (!r.U32(out uint unk6)) return Fail("truncated unk6 count", out error);
        if (unk6 > 0 && unk6 < 65535u)
        {
            for (uint i = 0; i < unk6; ++i)
            {
                if (!r.F32(out _)) return Fail("truncated unk6 block", out error);
            }
        }

        if (!r.U32(out uint stringBlockCount)) return Fail("truncated string block count", out error);
        if (stringBlockCount > 0 && stringBlockCount < 1000000u)
        {
            for (uint i = 0; i < stringBlockCount; ++i)
            {
                if (!r.Strz(out string s)) return Fail("truncated hide region", out error);
                info.HideRegions.Add(s);
            }
        }

        for (uint mi = 0; mi < meshCount; ++mi)
        {
            if (!r.U32(out _)) return Fail("truncated mesh record", out error);
            if (!r.Strz(out string meshName)) return Fail("truncated mesh name", out error);

            // THE SAME 45-BYTE SCALAR BLOCK the engine parser reads, and this path was already
            // splitting it on the right boundaries — it just threw the values away. 1,312 of the
            // 5,446 shipped models answer through this fallback rather than the engine format, so
            // discarding them left roughly a quarter of the asset set on the constructor's
            // specular range instead of its own.
            //
            // The layout is `sub_82B243E8`'s read order; see Engine_MdlEngine for the full map.
            // Offsets 0 and 4 are the specular power minimum and RANGE, offset 29 the
            // self-illumination scale.
            if (!r.F32(out float specPowerMin)) return Fail("truncated mesh floats", out error);
            if (!r.F32(out float specPowerRange)) return Fail("truncated mesh floats", out error);
            if (!r.Skip(21)) return Fail("truncated mesh block", out error);
            if (!r.F32(out float selfIllumScale)) return Fail("truncated mesh float", out error);
            for (int k = 0; k < 3; ++k)
            {
                if (!r.U32(out _)) return Fail("truncated mesh words", out error);
            }

            if (!r.U32(out uint materialCount)) return Fail("truncated material count", out error);

            var mesh = new MdlMesh { Name = meshName };
            if (materialCount > 0 && materialCount < 65535u)
            {
                for (uint j = 0; j < materialCount; ++j)
                {
                    var m = new MdlMaterial();
                    if (!r.Strz(out string diffuse)) return Fail("truncated diffuse name", out error);
                    if (!r.Strz(out string specular)) return Fail("truncated specular name", out error);
                    if (!r.Strz(out string normal)) return Fail("truncated normal name", out error);
                    if (!r.Strz(out string metallic)) return Fail("truncated metallic name", out error);
                    if (!r.Strz(out string extra)) return Fail("truncated extra name", out error);
                    m.Diffuse = diffuse;
                    m.Specular = specular;
                    m.Normal = normal;
                    m.EnvironmentMap = metallic;
                    m.SelfIllumination = extra;
                    m.SpecularPowerMin = specPowerMin;
                    m.SpecularPowerMax = specPowerMin + specPowerRange;
                    m.SelfIlluminationScale = selfIllumScale;

                    if (!SkipMaterialTail(r, info, mi, meshCount, j, materialCount))
                    {
                        return Fail("truncated material tail", out error);
                    }

                    mesh.Materials.Add(m);
                }
            }

            info.Meshes.Add(mesh);
        }

        if (isFoliage)
        {
            Engine_MdlFoliage.ParseBuffers(r, info);
        }
        else if (stringBlockCount > 0)
        {
            Engine_MdlBuffers.ParseStandardBuffers(r, info);
        }
        else
        {
            Engine_MdlBuffers.ParseAltBuffers(r, info);
        }

        return true;
    }

    private static bool SkipMaterialTail(
        MdlReader r, MdlInfo info, uint meshIndex, uint meshCount, uint matIndex, uint matCount)
    {
        bool usedScan = false;
        if (info.HasTreeTag && matIndex + 1 == matCount)
        {
            int endSearch = Math.Min(r.Length, r.Position + 1024);
            if (meshIndex + 1 < meshCount)
            {
                for (int p = r.Position; p + 4 < endSearch; ++p)
                {
                    if (r[p] == 0 && r[p + 1] == 0 && r[p + 2] == 0 && r[p + 3] == 0x01)
                    {
                        byte next = r[p + 4];
                        bool alpha = (next >= 'A' && next <= 'Z') || (next >= 'a' && next <= 'z');
                        if (alpha)
                        {
                            r.Position = p;
                            usedScan = true;
                            break;
                        }
                    }
                }
            }
            else
            {
                for (int p = r.Position; p + 7 <= endSearch; ++p)
                {
                    if (r[p + 2] == 0x01 && r[p + 3] == 0 && r[p + 4] == 0 &&
                        r[p + 5] == 0 && r[p + 6] == 0)
                    {
                        r.Position = p;
                        usedScan = true;
                        break;
                    }
                }
            }
        }

        if (usedScan)
        {
            return true;
        }

        if (!r.U32(out _) || !r.U32(out _) || !r.U32(out _))
        {
            return false;
        }

        int keep = r.Position;
        if (r.U8(out byte peek))
        {
            if (peek != 0x01)
            {
                r.Position = keep;
            }
        }
        else
        {
            r.Position = keep;
        }

        return true;
    }

    private static void ReadOptionalTagBlock(MdlReader r, MdlInfo info)
    {
        int save = r.Position;
        if (!r.U8(out byte tagCount))
        {
            r.Position = save;
            return;
        }

        if (tagCount == 0)
        {
            return;
        }

        if (tagCount >= 32)
        {
            r.Position = save;
            return;
        }

        var tags = new List<string>(tagCount);
        bool ok = true;

        for (byte k = 0; k < tagCount && ok; ++k)
        {
            int scanLimit = Math.Min(r.Length, r.Position + 64);
            bool foundNull = false;
            for (int p = r.Position; p < scanLimit; ++p)
            {
                byte b = r[p];
                if (b == 0)
                {
                    foundNull = p > r.Position;
                    break;
                }

                bool isAlpha = (b >= 'A' && b <= 'Z') || (b >= 'a' && b <= 'z');
                bool isAlnum = isAlpha || (b >= '0' && b <= '9') || b == '_';
                if (p == r.Position)
                {
                    if (!isAlpha) { foundNull = false; break; }
                }
                else if (!isAlnum)
                {
                    foundNull = false;
                    break;
                }
            }

            if (!foundNull) { ok = false; break; }
            if (!r.Strz(out string s, 64)) { ok = false; break; }
            if (s.Length == 0 || s.Length > 32) { ok = false; break; }
            if (!r.U8(out _)) { ok = false; break; }
            tags.Add(s);
        }

        if (!ok)
        {
            r.Position = save;
            return;
        }

        foreach (string t in tags)
        {
            if (t is "IsTree" or "NewTree")
            {
                info.HasTreeTag = true;
            }
        }
    }

    private static bool Fail(string message, out string error)
    {
        error = message;
        return false;
    }
}
