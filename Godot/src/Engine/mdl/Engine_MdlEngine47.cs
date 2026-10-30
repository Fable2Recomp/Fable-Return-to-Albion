using System;
using System.Collections.Generic;

namespace Refii.Models;

/// <summary>
/// Fable 3's MeshFile, versions 46 and 47: the 115-byte header from
/// <c>globals_model_headers.bnk</c> joined to its LOD payloads, read in the guest's order and
/// handed out as the same <see cref="MdlInfo"/> the version-36 reader produces, so a Fable 3
/// model builds through the same geometry, LOD and material path as a Fable 2 one.
/// Addresses are the Fable 3 disc XEX.
/// </summary>
/// <remarks>
/// VERTICES ARE REPACKED, NOT RE-ENCODED. Version 47 keeps the element formats of version 36 —
/// FLOAT16_4 position, FMT_10_11_11 SNORM normal, FLOAT16_2 UV, DEC4N tangent, UBYTE4 bone
/// indices, D3DCOLOR weights — but splits each part's vertices into planar blocks. The reader
/// copies each element's bytes into the interleaved version-36 layout the decoders read
/// (rigid 20, skinned 28, tree 36), followed by the part's u16 indices, into
/// <see cref="MdlInfo.Body"/>, and every buffer offset points into that array.
/// </remarks>
public static class Engine_MdlEngine47
{
    /// <summary>Where the LOD payloads begin: magic(8) + version(4) + header size(4) + 99.</summary>
    public const int BodyStart = 115;

    /// <summary>
    /// Header byte: non-zero means coarser LODs ship no vertex stream and share LOD 0's.
    /// <c>sub_82D70950</c> stores it at header struct +28 (0x82D70E5C); the LOD walk copies it to
    /// LOD+69 (0x82C9B0E4) and reads the stream only when it is zero or the LOD index is zero
    /// (0x82C9BD8C).
    /// </summary>
    public const int SharedVertexStreamFlag = 110;

    /// <summary>
    /// Header byte: <c>GlobalLightingMode</c>, the first field `sub_82D70950` reads into the
    /// header block (model+0x30+0x54 = model+132; `sub_82C9E5E0` 0x82C9E8E0 passes model+0x30).
    /// </summary>
    public const int GlobalLightingModeOffset = 88;

    private const uint MaxCount = 4_000_000u;

    private enum PartKind
    {
        Rigid,
        Skinned,
        Tree,
        Instanced,
    }

    private enum FieldKind
    {
        Float,
        Colour,
        TextureSet,
    }

    /// <summary>
    /// The material field registrations the property block's keys are looked up in: the base map
    /// (<c>sub_82CBB370</c>) and the Fresnel additions (<c>sub_82CBBFB0</c>). A key is
    /// crc32_lh of the field name.
    /// </summary>
    private static readonly Dictionary<uint, (string Name, FieldKind Kind, bool FresnelOnly)> Fields = BuildFields();

    /// <summary>A magic of <c>MeshFile</c> or <c>DefMeshF</c> and a version this reader takes.</summary>
    public static bool IsMeshFile47(byte[] data)
    {
        if (data.Length < 12)
        {
            return false;
        }

        string magic = System.Text.Encoding.ASCII.GetString(data, 0, 8);
        if (magic is not "MeshFile" and not "DefMeshF")
        {
            return false;
        }

        uint version = MdlReader.ReadU32BE(data, 8);
        return version is 46 or 47;
    }

    /// <summary>
    /// The 115-byte file header, without its body (<c>sub_82C9E5E0</c> → <c>sub_82D69758</c>).
    /// </summary>
    /// <remarks>
    /// +8 version, gated to 46..47 at 0x82C9E808; +12 the remaining header size, 99; +16 the model
    /// id triple; +28 the bounding sphere; +44 the bounding box; +68 the LOD count, clamped to 3
    /// at 0x82C9E908; +72 three LOD payload sizes; +84 the LOD reference size. LOD i is the byte
    /// range at 115 + the preceding sizes (<c>sub_82C9DD98</c>).
    /// </remarks>
    public static bool TryReadFileHeader(byte[] data, MdlInfo info, out long bodyBytesStated)
    {
        bodyBytesStated = 0;
        if (data.Length < BodyStart || !IsMeshFile47(data))
        {
            return false;
        }

        var r = new MdlReader(data);
        info.Magic = System.Text.Encoding.ASCII.GetString(data, 0, 8);
        info.Version = MdlReader.ReadU32BE(data, 8);

        r.Position = 12;
        if (!r.U32(out uint hdrSize) || 16 + hdrSize != BodyStart) return false;
        info.HeaderSize = hdrSize;

        r.Position = 28;
        info.FileBoundingSphere = new float[4];
        for (int k = 0; k < 4; k++)
            if (!r.F32(out info.FileBoundingSphere[k])) return false;

        info.FileBoundingBox = new float[6];
        for (int k = 0; k < 6; k++)
            if (!r.F32(out info.FileBoundingBox[k])) return false;

        if (!r.U32(out uint lodCount)) return false;
        info.LodCount = Math.Min(lodCount, 3u);

        info.LodSizes = new uint[3];
        for (int i = 0; i < 3; i++)
        {
            if (!r.U32(out info.LodSizes[i])) return false;
            if (i < info.LodCount) bodyBytesStated += info.LodSizes[i];
        }

        if (!r.F32(out float lodReference)) return false;
        info.LodReference = float.IsFinite(lodReference) && lodReference > 0f ? lodReference : 0f;
        info.GlobalLightingMode = data[GlobalLightingModeOffset];

        return true;
    }

    /// <summary>
    /// Reads a joined file — header then LOD payloads — into <paramref name="info"/>, with every
    /// LOD payload consumed exactly to its stated size. False on anything else.
    /// </summary>
    public static bool TryParse(byte[] data, MdlInfo info)
    {
        if (!TryReadFileHeader(data, info, out long total)) return false;

        // Bytes past the last payload are never read by the guest; a short body cannot be read.
        if (BodyStart + total > data.Length) return false;

        bool shared = data[SharedVertexStreamFlag] != 0;
        var body = new BodyWriter(data.Length);
        Payload? lod0 = null;
        var coarser = new List<Payload>();

        int off = BodyStart;
        for (int lod = 0; lod < info.LodCount; lod++)
        {
            uint size = info.LodSizes[lod];
            if (size == 0)
            {
                continue;
            }

            long end = (long)off + size;
            if (end > data.Length) return false;

            var r = new MdlReader(data) { Position = off };
            var payload = new Payload { Index = lod };
            MdlInfo clothTarget = lod == 0 ? info : new MdlInfo { Version = info.Version };
            if (!ReadPayload(r, data, info.Version, !shared || lod == 0, payload, body, clothTarget))
            {
                return false;
            }

            if (r.Position != end) return false;
            off = (int)end;

            if (lod == 0) lod0 = payload;
            else coarser.Add(payload);
        }

        if (lod0 is null || lod0.Parts.Count == 0) return false;

        info.IsEngineFormat = true;
        info.MeshCount = lod0.MaterialCount;
        info.RigidCount = lod0.RigidCount;
        info.SkinnedCount = lod0.SkinnedCount;
        info.SectionFlag = lod0.LightmapFlag;
        info.Tags.AddRange(lod0.Tags);
        info.HasTreeTag = lod0.Tags.Contains("IsTree") || lod0.Tags.Contains("NewTree");
        info.Bones.AddRange(lod0.Bones);
        if (lod0.Transforms.Count == lod0.Bones.Count && lod0.Bones.Count > 0)
        {
            info.HasBoneTransforms = true;
            info.BoneTransforms.AddRange(lod0.Transforms);
        }

        info.HideRegions.AddRange(lod0.HideRegions);
        info.Meshes.AddRange(lod0.Meshes);
        info.LightmapUvs = lod0.LightmapUvs;

        // A coarser part without a vertex stream addresses the LOD-0 RIGID part its record names
        // (rigid record's first u32 → a1[34]); a C part shares the rigid layout but not this
        // numbering, so the list is built from rigid records alone. The base is the part's start
        // in LOD 0's own numbering over every buffer, as the geometry reader numbers them.
        var lod0Rigid = new List<(MdlMeshBuffer Buffer, int Base)>();
        int lod0Base = 0;
        foreach (Part part in lod0.Parts)
        {
            if (part.Kind == PartKind.Rigid)
            {
                lod0Rigid.Add((part.Buffer, lod0Base));
            }

            lod0Base += (int)part.Buffer.VertexCount;
            info.MeshBuffers.Add(part.Buffer);
        }

        foreach (Payload payload in coarser)
        {
            var lodOut = new MdlLod { Index = payload.Index };
            lodOut.Bones.AddRange(payload.Bones);
            lodOut.Meshes.AddRange(payload.Meshes);
            lodOut.HideRegions.AddRange(payload.HideRegions);

            foreach (Part part in payload.Parts)
            {
                MdlMeshBuffer mb = part.Buffer;
                if (part.SharesVertices)
                {
                    // Only a rigid part has a LOD-0 twin to take; a skinned part read without its
                    // stream (none ships) is dropped with the twinless rigid ones.
                    if (part.Kind != PartKind.Rigid ||
                        part.Lod0PartIndex >= lod0Rigid.Count ||
                        lod0Rigid[(int)part.Lod0PartIndex].Buffer.VertexCount != mb.VertexCount)
                    {
                        continue;
                    }

                    (MdlMeshBuffer twin, int twinBase) = lod0Rigid[(int)part.Lod0PartIndex];
                    mb.SharesLod0Vertices = true;
                    mb.SharedVertexBase = twinBase;
                    mb.VertexOffset = twin.VertexOffset;
                }

                lodOut.MeshBuffers.Add(mb);
            }

            if (lodOut.MeshBuffers.Count != 0)
            {
                info.Lods.Add(lodOut);
            }
        }

        info.Body = body.ToArray();
        return true;
    }

    /// <summary>One LOD payload's tables and parts, before the payloads are joined.</summary>
    private sealed class Payload
    {
        public int Index;
        public uint MaterialCount;
        public uint RigidCount;
        public uint SkinnedCount;
        public byte LightmapFlag;
        public float[] LightmapUvs = Array.Empty<float>();
        public readonly List<MdlBone> Bones = new();
        public readonly List<float[]> Transforms = new();
        public readonly List<string> Tags = new();
        public readonly List<string> HideRegions = new();
        public readonly List<MdlMesh> Meshes = new();
        public readonly List<Part> Parts = new();
    }

    private sealed class Part
    {
        public PartKind Kind;
        public bool SharesVertices;
        public uint Lod0PartIndex;
        public MdlMeshBuffer Buffer = new();
    }

    /// <summary>
    /// <c>sub_82C9B0B8</c> (LOD vtable <c>off_820E5D78</c> slot 5): read strictly in order, no
    /// seek, no offset table, no padding.
    /// </summary>
    private static bool ReadPayload(
        MdlReader r, byte[] data, uint version, bool vertexStream, Payload payload,
        BodyWriter body, MdlInfo clothTarget)
    {
        // The model id triple, identical to header +16 (`sub_82D694A0` at 0x82C9B108).
        if (!r.Skip(12)) return false;

        if (!ReadSkeleton(r, payload)) return false;

        // The LOD's own bounding sphere and box (0x82C9B120..0x82C9B19C).
        if (!r.Skip(40)) return false;

        // Five counts → LOD+40..+56: materials, A rigid, B skinned, C instanced, D tree.
        if (!r.U32(out uint mc) || !r.U32(out uint countA) || !r.U32(out uint countB) ||
            !r.U32(out uint countC) || !r.U32(out uint countD)) return false;
        if (mc > 100_000u || countA > 100_000u || countB > 100_000u || countC > 100_000u ||
            countD > 100_000u) return false;
        payload.MaterialCount = mc;
        payload.RigidCount = countA;
        payload.SkinnedCount = countB;

        if (!r.U8(out byte lightmapFlag)) return false;
        payload.LightmapFlag = lightmapFlag;

        // Tags: the string is the set key at LOD+128, the byte its bool (0x82C9B5C8..0x82C9B718).
        if (!r.U32(out uint tagCount) || tagCount > 100_000u) return false;
        for (uint t = 0; t < tagCount; t++)
        {
            if (!r.Strz(out string tag) || !r.Skip(1)) return false;
            payload.Tags.Add(tag);
        }

        // Hide-region names; a batch's first u32 indexes this list (0x82C9BA9C).
        if (!r.U32(out uint regionCount) || regionCount > 1_000_000u) return false;
        for (uint i = 0; i < regionCount; i++)
        {
            if (!r.Strz(out string region)) return false;
            payload.HideRegions.Add(region);
        }

        for (uint m = 0; m < mc; m++)
        {
            if (!ReadMaterial(r, countC > 0, out MdlMesh mesh)) return false;
            payload.Meshes.Add(mesh);
        }

        // The lightmap tail counts rigid and tree vertices only (0x82C9BFD4).
        uint lightmapVertices = 0;

        for (uint i = 0; i < countA; i++)
        {
            int recIndex = payload.Parts.Count;
            if (!ReadRigid(r, data, vertexStream, body, clothTarget, recIndex, out Part part)) return false;
            lightmapVertices += part.Buffer.VertexCount;
            payload.Parts.Add(part);
        }

        for (uint i = 0; i < countB; i++)
        {
            int recIndex = payload.Parts.Count;
            if (!ReadSkinned(r, data, vertexStream, body, clothTarget, recIndex, out Part part)) return false;
            payload.Parts.Add(part);
        }

        // D BEFORE C: the tree loop at 0x82C9BEE0 runs before the instanced loop at 0x82C9BF68.
        for (uint i = 0; i < countD; i++)
        {
            if (!ReadTree(r, data, version, body, out Part part)) return false;
            lightmapVertices += part.Buffer.VertexCount;
            payload.Parts.Add(part);
        }

        for (uint i = 0; i < countC; i++)
        {
            if (!ReadInstanced(r, data, body, out Part part)) return false;
            payload.Parts.Add(part);
        }

        // FLOAT16_2 per rigid and tree vertex, model order (`sub_82C73690(*v172, v146, 4, ...)`).
        if (lightmapFlag != 0 && lightmapVertices > 0)
        {
            if (payload.Index == 0)
            {
                var uvs = new float[lightmapVertices * 2];
                for (uint v = 0; v < lightmapVertices; v++)
                {
                    if (!r.U16(out ushort hu) || !r.U16(out ushort hv)) return false;
                    uvs[v * 2] = (float)BitConverter.UInt16BitsToHalf(hu);
                    uvs[(v * 2) + 1] = (float)BitConverter.UInt16BitsToHalf(hv);
                }

                payload.LightmapUvs = uvs;
            }
            else if (!r.Skip((int)(lightmapVertices * 4)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// <c>sub_82D543D8</c>: u8 n × 8-byte pairs (meaning not established), then u32 n ×
    /// {u32 name hash, u32 parent}, then u32 n × 11 f32 bind transforms. The name hash function is
    /// not established, so a bone is named by its hash.
    /// </summary>
    private static bool ReadSkeleton(MdlReader r, Payload payload)
    {
        if (!r.U8(out byte pairs) || !r.Skip(pairs * 8)) return false;

        if (!r.U32(out uint boneCount) || boneCount > 100_000u) return false;
        for (uint i = 0; i < boneCount; i++)
        {
            if (!r.U32(out uint hash) || !r.U32(out uint parent)) return false;
            payload.Bones.Add(new MdlBone
            {
                Name = $"0x{hash:X8}",
                ParentId = parent == 0xFFFFFFFFu ? -1 : (int)parent,
            });
        }

        if (!r.U32(out uint transformCount) || transformCount > 100_000u) return false;
        for (uint i = 0; i < transformCount; i++)
        {
            var tf = new float[11];
            for (int k = 0; k < 11; k++)
                if (!r.F32(out tf[k])) return false;
            payload.Transforms.Add(tf);
        }

        return true;
    }

    /// <summary>
    /// One material: a u32 class type, then the class's reader (0x82C9BB20..0x82C9BD40). With
    /// any C part every material is the <c>off_820026F8</c> class, read by the base reader alone;
    /// otherwise 0 and 4 are the Fresnel class, 1 the base reader plus a u8, 3 the base reader,
    /// and 2 or above 4 have no class (the guest stores a null object). The base reader is a name
    /// and the property block. A Fable 3 material becomes a mesh of one material, which every
    /// part names by index.
    /// </summary>
    private static bool ReadMaterial(MdlReader r, bool hasInstanced, out MdlMesh mesh)
    {
        mesh = new MdlMesh();
        if (!r.U32(out uint type)) return false;
        if (!hasInstanced && type is not (0 or 1 or 3 or 4)) return false;

        bool fresnel = !hasInstanced && type is 0 or 4;
        bool trailingBool = !hasInstanced && type == 1;

        if (!r.Strz(out string name)) return false;
        var material = new MdlMaterial { ShaderType = type };
        if (!ReadPropertyBlock(r, material, fresnel)) return false;

        material.FresnelClass = fresnel;

        // `sub_82CE5158`: the class-1 bool → material+340.
        if (trailingBool)
        {
            if (!r.U8(out byte canopy)) return false;
            material.CanopyFlag = canopy != 0;
        }

        mesh.Name = name;
        mesh.Materials.Add(material);
        return true;
    }

    /// <summary>
    /// <c>sub_82C99DA8</c>: u32 tag (unused), u32 n, n × {u32 crc32_lh(field name), u32 size,
    /// payload}. A key outside the class's map is skipped by its size (0x82C9A174); a float or
    /// colour field is <c>size</c> raw bytes; the texture set is read by its own reader, which
    /// ignores <c>size</c>.
    /// </summary>
    private static bool ReadPropertyBlock(MdlReader r, MdlMaterial material, bool fresnel)
    {
        if (!r.U32(out _) || !r.U32(out uint count) || count > 4096u) return false;

        Dictionary<string, float[]>? stated = null;
        for (uint i = 0; i < count; i++)
        {
            if (!r.U32(out uint key) || !r.U32(out uint size)) return false;
            if (size > int.MaxValue) return false;

            if (!Fields.TryGetValue(key, out (string Name, FieldKind Kind, bool FresnelOnly) field) ||
                (field.FresnelOnly && !fresnel))
            {
                if (!r.Skip((int)size)) return false;
                continue;
            }

            if (field.Kind == FieldKind.TextureSet)
            {
                if (!ReadTextureSet(r, material)) return false;
                continue;
            }

            if (!r.Need((int)size)) return false;
            int start = r.Position;
            var values = new float[size / 4];
            for (int k = 0; k < values.Length; k++)
                if (!r.F32(out values[k])) return false;
            r.Position = start;
            if (!r.Skip((int)size)) return false;

            stated ??= new Dictionary<string, float[]>(StringComparer.Ordinal);
            stated[field.Name] = values;
        }

        if (stated is null)
        {
            return true;
        }

        material.StatedProperties = stated;
        if (stated.TryGetValue("SpecularPower", out float[]? power) && power.Length == 1)
        {
            material.SpecularPowerMin = power[0];
            material.SpecularPowerMax = power[0];
        }

        if (stated.TryGetValue("SelfIlluminationFactor", out float[]? selfIllum) && selfIllum.Length == 1)
        {
            material.SelfIlluminationScale = selfIllum[0];
        }

        return true;
    }

    /// <summary>
    /// <c>sub_82CBA610</c>: six NUL-terminated texture paths, then two f32. s0 diffuse, s1
    /// specular, s2 normal (the shipped names say so); s3 the environment map and s4 the
    /// self-illumination map (by the pixel booleans their slots drive; empty in every sample);
    /// s5 is read and discarded. The two floats are unidentified and kept
    /// as <see cref="MdlMaterial.MaterialFloats"/>, where the version-36 texture-set record keeps
    /// its own two.
    /// </summary>
    private static bool ReadTextureSet(MdlReader r, MdlMaterial material)
    {
        var textures = new string[6];
        for (int k = 0; k < textures.Length; k++)
            if (!r.Strz(out textures[k])) return false;

        var floats = new float[2];
        if (!r.F32(out floats[0]) || !r.F32(out floats[1])) return false;

        material.Diffuse = textures[0];
        material.Specular = textures[1];
        material.Normal = textures[2];
        // s3 lands at set[25] (material+108) and s4 at set[4] (material+24), neither with a
        // default. `sub_8220D668` publishes material+0x18 as pixel bool 4 (0x8220D7C4 insrwi 1,27)
        // and material+0x6C as pixel bool 5 (0x8220D81C insrwi 1,26): g_NeedsSelfIllumination and
        // g_NeedsEnvmapping in the title's constant tables.
        material.EnvironmentMap = textures[3];
        material.SelfIllumination = textures[4];
        material.MaterialFloats = floats;
        return true;
    }

    /// <summary>
    /// The 37-byte batch (loops at 0x82CEAB00 / 0x82CECF00): u32 hide-region index (0xFFFFFFFF
    /// none), u8 strip flag, u32 primitive count, u32 start index, vec3 min, vec3 max. The strip
    /// flag is version 36's primitive byte: zero is a list, non-zero a strip (0x82CECEA4). The
    /// per-batch material of version 36 is gone; the part names one material, which is its mesh's
    /// only one.
    /// </summary>
    private static bool ReadBatches(MdlReader r, MdlMeshBuffer buffer)
    {
        if (!r.U32(out uint count) || count > 100_000u) return false;
        for (uint i = 0; i < count; i++)
        {
            if (!r.U32(out uint region) || !r.U8(out byte strip) || !r.U32(out uint prims) ||
                !r.U32(out uint start)) return false;

            var bb = new float[6];
            for (int k = 0; k < 6; k++)
                if (!r.F32(out bb[k])) return false;

            buffer.SubMeshes.Add(new MdlSubMesh
            {
                RegionIndex = region,
                MaterialIndex = 0,
                PrimitiveType = strip,
                FaceCount = prims,
                StartIndex = start,
                BoundingBox = bb,
            });
        }

        return true;
    }

    /// <summary>
    /// A rigid part (<c>sub_82CEA5F8</c>, 152-byte object): name, u8, u32 LOD-0 twin, u32
    /// material, u32, u32 index count, u32 vertex count, the part's bounds (40 bytes), batches;
    /// with the vertex stream (<c>sub_82CEB288</c>) vc × 12 (FLOAT16_4 position, FLOAT16_2 UV)
    /// and vc × 8 (normal, DEC4N tangent); then ic × u16 and a u32 cloth flag.
    /// </summary>
    private static bool ReadRigid(
        MdlReader r, byte[] data, bool vertexStream, BodyWriter body, MdlInfo clothTarget,
        int recIndex, out Part part)
    {
        part = new Part { Kind = PartKind.Rigid };
        if (!r.Strz(out _) || !r.Skip(1)) return false;
        if (!r.U32(out uint twin) || !r.U32(out uint material) || !r.U32(out _) ||
            !r.U32(out uint ic) || !r.U32(out uint vc)) return false;
        if (ic > MaxCount || vc > MaxCount) return false;
        if (!r.Skip(40)) return false;

        MdlMeshBuffer mb = part.Buffer;
        mb.IsEngineRigid = true;
        mb.MeshIndex = material;
        mb.VertexCount = vc;
        mb.FaceCount = ic;
        part.Lod0PartIndex = twin;
        if (!ReadBatches(r, mb)) return false;

        if (vertexStream)
        {
            int s0 = r.Position;
            if (!r.Skip((int)(vc * 12))) return false;
            int s3 = r.Position;
            if (!r.Skip((int)(vc * 8))) return false;
            mb.VertexOffset = RepackRigid(data, s0, s3, (int)vc, body);
        }
        else
        {
            part.SharesVertices = true;
        }

        if (!ReadIndices(r, data, ic, body, mb)) return false;
        return ReadCloth(r, data, body, clothTarget, recIndex);
    }

    /// <summary>
    /// A skinned part (<c>sub_82CEC260</c>, 168-byte object): u8 narrow-extra flag, u32 LOD-0 twin,
    /// u32 material, u32, u32 index count, u32 vertex count, batches; with the vertex stream
    /// (<c>sub_82CED378</c>) vc × 20 (position, UBYTE4 indices, D3DCOLOR weights, UV) and vc × 8
    /// (normal, tangent); then a u8 extra flag with vc × (narrow ? 4 : 8) bytes — a second UV set
    /// no consumer reads, skipped — then ic × u16 and a u32 cloth flag. No fur block.
    /// </summary>
    private static bool ReadSkinned(
        MdlReader r, byte[] data, bool vertexStream, BodyWriter body, MdlInfo clothTarget,
        int recIndex, out Part part)
    {
        part = new Part { Kind = PartKind.Skinned };
        if (!r.U8(out byte narrow)) return false;
        if (!r.U32(out uint twin) || !r.U32(out uint material) || !r.U32(out _) ||
            !r.U32(out uint ic) || !r.U32(out uint vc)) return false;
        if (ic > MaxCount || vc > MaxCount) return false;

        MdlMeshBuffer mb = part.Buffer;
        mb.IsEngineSkinned = true;
        mb.MeshIndex = material;
        mb.VertexCount = vc;
        mb.FaceCount = ic;
        part.Lod0PartIndex = twin;
        if (!ReadBatches(r, mb)) return false;

        if (vertexStream)
        {
            int s0 = r.Position;
            if (!r.Skip((int)(vc * 20))) return false;
            int s3 = r.Position;
            if (!r.Skip((int)(vc * 8))) return false;
            mb.VertexOffset = RepackSkinned(data, s0, s3, (int)vc, body);
        }
        else
        {
            part.SharesVertices = true;
        }

        if (!r.U8(out byte extra)) return false;
        if (extra != 0 && !r.Skip((int)(vc * (narrow != 0 ? 4u : 8u)))) return false;

        if (!ReadIndices(r, data, ic, body, mb)) return false;
        return ReadCloth(r, data, body, clothTarget, recIndex);
    }

    /// <summary>
    /// A tree part (<c>sub_82CE40E8</c>, 240-byte object): u8, u8 flag, u32 material, u32, u32
    /// index count, u32 vertex count; version 47's planar vertices (<c>sub_82CE4718</c>) vc × 12
    /// (position, UV), vc × 16 (POSITION1 FLOAT16_4, COLOR1, COLOR2), vc × 8 (normal, tangent);
    /// then ic × u16. No batches. Version 46 goes through <c>sub_82CE48E0</c>, which is not
    /// decoded, so a version-46 tree refuses the model.
    /// </summary>
    /// <remarks>
    /// The 16-byte block is exactly version 36's wind lanes — POSITION1 at +20, COLOR1 at +28,
    /// COLOR2 at +32 of the 36-byte tree vertex — so it repacks into that layout and the tree
    /// stream reader takes it unchanged. No batch states a primitive, so the buffer keeps no
    /// section and the geometry reader decides as it does for a version-36 tree.
    /// </remarks>
    private static bool ReadTree(MdlReader r, byte[] data, uint version, BodyWriter body, out Part part)
    {
        part = new Part { Kind = PartKind.Tree };
        if (version < 47) return false;
        if (!r.Skip(2)) return false;
        if (!r.U32(out uint material) || !r.U32(out _) || !r.U32(out uint ic) ||
            !r.U32(out uint vc)) return false;
        if (ic > MaxCount || vc > 65_535u) return false;

        MdlMeshBuffer mb = part.Buffer;
        mb.IsEngineTree = true;
        mb.MeshIndex = material;
        mb.VertexCount = vc;
        mb.FaceCount = ic;

        int s0 = r.Position;
        if (!r.Skip((int)(vc * 12))) return false;
        int wind = r.Position;
        if (!r.Skip((int)(vc * 16))) return false;
        int s2 = r.Position;
        if (!r.Skip((int)(vc * 8))) return false;
        mb.VertexOffset = RepackTree(data, s0, wind, s2, (int)vc, body);

        return ReadIndices(r, data, ic, body, mb);
    }

    /// <summary>
    /// An instanced part (<c>sub_82CE59A8</c>, 72-byte object): u8, u8 strip flag (the index
    /// buffer is primitive <c>flag == 0 ? 4 : 6</c>, list or strip), u32 material, u32, u32 index
    /// count, u32 vertex count; vc × 12, vc × 8, ic × u16.
    /// </summary>
    /// <remarks>
    /// READ AS A RIGID PART. The 12-byte block is three half position components, one half and a
    /// half UV pair — the rigid stream 0, FLOAT16_4 position and FLOAT16_2 UV — and the 8-byte
    /// block is two u32, normal then tangent. The C declaration builder is not traced; the
    /// normal word decodes with the rigid FMT_10_11_11 decoder to unit length on all 18,442
    /// shipped C vertices. The guest's index replication for instancing does not change the file.
    /// The stated strip flag becomes the buffer's one section.
    /// </remarks>
    private static bool ReadInstanced(MdlReader r, byte[] data, BodyWriter body, out Part part)
    {
        part = new Part { Kind = PartKind.Instanced };
        if (!r.Skip(1) || !r.U8(out byte strip)) return false;
        if (!r.U32(out uint material) || !r.U32(out _) || !r.U32(out uint ic) ||
            !r.U32(out uint vc)) return false;
        if (ic > MaxCount || vc > 65_535u) return false;

        MdlMeshBuffer mb = part.Buffer;
        mb.IsEngineRigid = true;
        mb.IsEngineInstanced = true;
        mb.MeshIndex = material;
        mb.VertexCount = vc;
        mb.FaceCount = ic;
        mb.SubMeshes.Add(new MdlSubMesh
        {
            RegionIndex = uint.MaxValue,
            MaterialIndex = 0,
            PrimitiveType = strip,
            FaceCount = strip != 0 ? (ic >= 2 ? ic - 2 : 0) : ic / 3,
            StartIndex = 0,
        });

        int s0 = r.Position;
        if (!r.Skip((int)(vc * 12))) return false;
        int s1 = r.Position;
        if (!r.Skip((int)(vc * 8))) return false;
        mb.VertexOffset = RepackRigid(data, s0, s1, (int)vc, body);

        return ReadIndices(r, data, ic, body, mb);
    }

    /// <summary>ic × u16 big-endian, 0xFFFF the strip restart: version 36's encoding, copied as is.</summary>
    private static bool ReadIndices(MdlReader r, byte[] data, uint ic, BodyWriter body, MdlMeshBuffer mb)
    {
        int at = r.Position;
        if (!r.Skip((int)(ic * 2))) return false;
        mb.FaceOffset = body.Copy(data, at, (int)(ic * 2));
        return true;
    }

    /// <summary>
    /// The u32 cloth flag and, when it is non-zero, the cloth block (<c>sub_82CFD7C8</c>):
    /// version 36's block without the trailing per-vertex SH stream, read by the same reader with
    /// no SH vectors. LOD 0's blocks are copied into the body and their offsets moved with them;
    /// a coarser LOD's are read past (<paramref name="clothTarget"/> is a scratch record).
    /// </summary>
    private static bool ReadCloth(MdlReader r, byte[] data, BodyWriter body, MdlInfo clothTarget, int recIndex)
    {
        if (!r.U32(out uint flag)) return false;
        if (flag == 0) return true;

        int before = clothTarget.ClothBlocks.Count;
        if (!Engine_MdlEngine.ReadClothBlock(r, clothTarget, (uint)recIndex, 0)) return false;
        if (clothTarget.ClothBlocks.Count != before + 1) return false;

        MdlClothBlock block = clothTarget.ClothBlocks[before];
        int delta = body.Copy(data, block.BlockOffset, block.BlockSize) - block.BlockOffset;
        Rebase(block, delta);
        return true;
    }

    private static void Rebase(MdlClothBlock b, int delta)
    {
        b.BlockOffset += delta;
        b.RestPosOffset += delta;
        b.UvOffset += delta;
        b.StreamOneOffset += delta;
        b.TangentWOffset += delta;
        b.PositionWOffset += delta;
        b.IndexListOffset += delta;
        b.BoneIdOffset += delta;
        b.WeightOffset += delta;
        b.FlagOffset += delta;
        b.EdgePairOffset += delta;
        b.EdgeParamOffset += delta;
        b.SphereOffset += delta;
        b.PlaneOffset += delta;
        b.CapsuleOffset += delta;
        b.BoxOffset += delta;
        b.WeldOffset += delta;
        b.RawVertexOffset += delta;
    }

    /// <summary>
    /// Version 36's rigid vertex (20 bytes): position @0, normal @8, UV @12, tangent @16, from
    /// stream 0 (position @0, UV @8; <c>sub_82CAD518</c>) and the 8-byte block (normal @0,
    /// tangent @4).
    /// </summary>
    private static int RepackRigid(byte[] data, int s0, int s3, int vc, BodyWriter body)
    {
        int at = body.Reserve(vc * 20);
        Span<byte> dst = body.Span(at, vc * 20);
        for (int v = 0; v < vc; v++)
        {
            ReadOnlySpan<byte> a = data.AsSpan(s0 + (v * 12), 12);
            ReadOnlySpan<byte> b = data.AsSpan(s3 + (v * 8), 8);
            Span<byte> o = dst.Slice(v * 20, 20);
            a.Slice(0, 8).CopyTo(o);
            b.Slice(0, 4).CopyTo(o.Slice(8));
            a.Slice(8, 4).CopyTo(o.Slice(12));
            b.Slice(4, 4).CopyTo(o.Slice(16));
        }

        return at;
    }

    /// <summary>
    /// Version 36's skinned vertex (28 bytes): position @0, normal @8, indices @12, weights @16,
    /// UV @20, tangent @24, from stream 0 (position @0, UBYTE4 @8, D3DCOLOR @12, UV @16;
    /// <c>sub_82C93BB0</c>, read by the guest at 0x82CECF18 / 0x82CECF1C) and the 8-byte block.
    /// </summary>
    private static int RepackSkinned(byte[] data, int s0, int s3, int vc, BodyWriter body)
    {
        int at = body.Reserve(vc * 28);
        Span<byte> dst = body.Span(at, vc * 28);
        for (int v = 0; v < vc; v++)
        {
            ReadOnlySpan<byte> a = data.AsSpan(s0 + (v * 20), 20);
            ReadOnlySpan<byte> b = data.AsSpan(s3 + (v * 8), 8);
            Span<byte> o = dst.Slice(v * 28, 28);
            a.Slice(0, 8).CopyTo(o);
            b.Slice(0, 4).CopyTo(o.Slice(8));
            a.Slice(8, 12).CopyTo(o.Slice(12));
            b.Slice(4, 4).CopyTo(o.Slice(24));
        }

        return at;
    }

    /// <summary>
    /// Version 36's tree vertex (36 bytes): the rigid 20 then POSITION1 @20, COLOR1 @28, COLOR2
    /// @32, from stream 0, the 16-byte wind block and the 8-byte normal/tangent block
    /// (<c>sub_82C87D50</c>).
    /// </summary>
    private static int RepackTree(byte[] data, int s0, int wind, int s2, int vc, BodyWriter body)
    {
        int at = body.Reserve(vc * 36);
        Span<byte> dst = body.Span(at, vc * 36);
        for (int v = 0; v < vc; v++)
        {
            ReadOnlySpan<byte> a = data.AsSpan(s0 + (v * 12), 12);
            ReadOnlySpan<byte> w = data.AsSpan(wind + (v * 16), 16);
            ReadOnlySpan<byte> b = data.AsSpan(s2 + (v * 8), 8);
            Span<byte> o = dst.Slice(v * 36, 36);
            a.Slice(0, 8).CopyTo(o);
            b.Slice(0, 4).CopyTo(o.Slice(8));
            a.Slice(8, 4).CopyTo(o.Slice(12));
            b.Slice(4, 4).CopyTo(o.Slice(16));
            w.CopyTo(o.Slice(20));
        }

        return at;
    }

    /// <summary>
    /// <c>sub_82DA74A0</c>: the reflected CRC-32 table at 0x82061F60, initial value 0, no final
    /// xor. crc32_lh("StandardMeshTextureSet") = 0x351B0C30.
    /// </summary>
    public static uint Crc32Lh(string name)
    {
        uint crc = 0;
        foreach (char ch in name)
        {
            uint c = ((uint)(byte)ch ^ crc) & 0xFFu;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1u) != 0 ? (c >> 1) ^ 0xEDB88320u : c >> 1;
            }

            crc = c ^ (crc >> 8);
        }

        return crc;
    }

    private static Dictionary<uint, (string Name, FieldKind Kind, bool FresnelOnly)> BuildFields()
    {
        var fields = new (string Name, FieldKind Kind, bool FresnelOnly)[]
        {
            ("DiffuseColour", FieldKind.Colour, false),
            ("SpecularColour", FieldKind.Colour, false),
            ("EnvironmentMapColour", FieldKind.Colour, false),
            ("SpecularColourFactor", FieldKind.Float, false),
            ("SpecularPower", FieldKind.Float, false),
            ("SelfIlluminationFactor", FieldKind.Float, false),
            ("StandardMeshTextureSet", FieldKind.TextureSet, false),
            ("FresnelLightMinValue", FieldKind.Float, true),
            ("FresnelLightMaxValue", FieldKind.Float, true),
            ("FresnelLightStrength", FieldKind.Float, true),
            ("FresnelLightShininess", FieldKind.Float, true),
            ("FresnelMaterialColour", FieldKind.Colour, true),
        };

        var map = new Dictionary<uint, (string, FieldKind, bool)>();
        foreach ((string Name, FieldKind Kind, bool FresnelOnly) field in fields)
        {
            map[Crc32Lh(field.Name)] = field;
        }

        return map;
    }

    /// <summary>
    /// The repacked body. Every byte is a copy of one file byte, so it never outgrows the file.
    /// </summary>
    private sealed class BodyWriter
    {
        private byte[] _buffer;
        private int _length;

        public BodyWriter(int capacity) => _buffer = new byte[Math.Max(capacity, 16)];

        public int Reserve(int count)
        {
            if (_length + count > _buffer.Length)
            {
                Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _length + count));
            }

            int at = _length;
            _length += count;
            return at;
        }

        public Span<byte> Span(int at, int count) => _buffer.AsSpan(at, count);

        public int Copy(byte[] source, int offset, int count)
        {
            int at = Reserve(count);
            source.AsSpan(offset, count).CopyTo(_buffer.AsSpan(at, count));
            return at;
        }

        public byte[] ToArray() => _buffer.AsSpan(0, _length).ToArray();
    }
}
