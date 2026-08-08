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
    public string Diffuse { get; set; } = string.Empty;

    public string Specular { get; set; } = string.Empty;

    public string Normal { get; set; } = string.Empty;

    public string Metallic { get; set; } = string.Empty;

    public string Extra { get; set; } = string.Empty;

    public uint ShaderType { get; set; }

    public string[] ShaderParams { get; set; } = Array.Empty<string>();

    public float[] MaterialFloats { get; set; } = Array.Empty<float>();
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
            if (!r.F32(out _) || !r.F32(out _)) return Fail("truncated mesh floats", out error);
            if (!r.Skip(21)) return Fail("truncated mesh block", out error);
            if (!r.F32(out _)) return Fail("truncated mesh float", out error);
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
                    m.Metallic = metallic;
                    m.Extra = extra;

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
