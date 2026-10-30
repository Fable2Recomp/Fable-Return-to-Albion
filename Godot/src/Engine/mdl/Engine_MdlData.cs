using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

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
    /// The five floats of the scalar block between the specular range and the self-illumination
    /// scale, in file order: the guest's RC+88..+104, which it packs as c24.w and c25.xyz of
    /// its standard material block. Shared by every texture set of the material.
    /// </summary>
    public float[] BlockFloats { get; set; } = Array.Empty<float>();

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

    /// <summary>
    /// Fable 3's material property block, by the guest's field name: every float or colour field
    /// the material's class registers (<c>DiffuseColour</c>, <c>SpecularPower</c>,
    /// <c>FresnelLightStrength</c>, ...) as the floats the file states. Null for Fable 2, whose
    /// material record has no such block.
    /// </summary>
    public IReadOnlyDictionary<string, float[]>? StatedProperties { get; set; }

    /// <summary>
    /// Fable 3: the material is the Fresnel class (type 0 or 4 in a model with no instanced
    /// part; `sub_82C9B0B8` 0x82C9BC74 builds it with `sub_82CEDE20`, vtable 0x82004688). Its
    /// vtable slot 1 (0x82CEE028) answers material+368 &gt; 0, and +368 is 1.0 from the
    /// constructor and never read from the file, so every such material draws FR1.
    /// </summary>
    public bool FresnelClass { get; set; }

    /// <summary>
    /// Fable 3: class 1's trailing byte, material+340 (`sub_82CE5158`). The tree selector
    /// (`sub_8221BAF0` 0x8221BB7C <c>lbz r11, 0x154(r30)</c>) takes the TRUNK0 pair when it is set
    /// and the TRUNK1 pair when it is clear.
    /// </summary>
    public bool CanopyFlag { get; set; }
}

public sealed class MdlMesh
{
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// A fur material record (type 2), as 17 values in file order, or null: +0 vec3, +12 f32,
    /// +16 vec3, +28 discarded, +32 fur length, +36 local occlusion, +40 vec3, +52 vec3, +64 strand
    /// alpha (`sub_82B29168`). Such a mesh has no name and no other material.
    /// </summary>
    public float[]? Fur { get; set; }

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

    /// <summary>
    /// A TypeD (tree/foliage) part: stride 36, and no submesh batches.
    /// </summary>
    /// <remarks>
    /// The guest's fourth part class (sub_82B352A8). Stream 0 is the 20-byte rigid layout plus
    /// POSITION1 at +20, COLOR1 at +28 and COLOR2 at +32 — the wind lanes — and the declaration
    /// builder emits all seven elements unconditionally, so 36 is not a variant.
    /// </remarks>
    public bool IsEngineTree { get; set; }

    /// <summary>
    /// Fable 3: an instanced (C) part, the 72-byte object `sub_82CE59A8` builds. The guest draws
    /// these on the repeated family (`sub_82CE8E50` indexes the same 72-byte records for the
    /// material `sub_82CE4C78` keys REPEATEDMATERIAL from). Also <see cref="IsEngineRigid"/>: the
    /// streams are the rigid layout.
    /// </summary>
    public bool IsEngineInstanced { get; set; }

    /// <summary>
    /// A coarser LOD's rigid part under the header's shared-stream flag: it ships no vertex
    /// stream, and its indices address the LOD-0 rigid part its record names, whose
    /// <see cref="VertexOffset"/> and <see cref="VertexCount"/> this buffer carries.
    /// </summary>
    public bool SharesLod0Vertices { get; set; }

    /// <summary>
    /// For a <see cref="SharesLod0Vertices"/> buffer: where the LOD-0 part it addresses starts
    /// in the model's own (LOD 0, all buffers) vertex numbering.
    /// </summary>
    public int SharedVertexBase { get; set; } = -1;

    public List<MdlSubMesh> SubMeshes { get; } = new();
}

/// <summary>
/// One coarser LOD payload (table slots 1..2), read with its own tables: a coarser payload
/// repeats the bone, mesh-header and hide-region tables, and skinned parts index THIS table's
/// bones, not LOD 0's.
/// </summary>
public sealed class MdlLod
{
    /// <summary>The header's LOD table slot: 1 or 2.</summary>
    public int Index { get; set; }

    public List<MdlBone> Bones { get; } = new();

    public List<MdlMesh> Meshes { get; } = new();

    public List<string> HideRegions { get; } = new();

    public List<MdlMeshBuffer> MeshBuffers { get; } = new();
}

public sealed class MdlInfo
{
    public string Magic { get; set; } = string.Empty;

    public uint HeaderSize { get; set; }

    public List<MdlBone> Bones { get; } = new();

    public bool HasBoneTransforms { get; set; }

    public List<float[]> BoneTransforms { get; } = new();

    public uint MeshCount { get; set; }

    /// <summary>
    /// The lightmap UV channel — the LOD prologue's flag-gated tail: one FLOAT16_2 per rigid
    /// vertex, appended after the last part, in the same model-global vertex order the bake
    /// buffers use. Two floats per vertex; empty when the model does not carry the channel.
    /// Only building interiors ship it (the amb3 texture-lightmap surfaces).
    /// </summary>
    public float[] LightmapUvs { get; set; } = System.Array.Empty<float>();

    public List<MdlMesh> Meshes { get; } = new();

    public List<MdlMeshBuffer> MeshBuffers { get; } = new();

    public List<string> HideRegions { get; } = new();

    public bool HasTreeTag { get; set; }

    public bool IsStub { get; set; }

    /// <summary>
    /// Bytes of LOD payload the file header says follow it, whether or not they are
    /// there. Nonzero on a stub means the header is intact and the BODY BANK ENTRY is missing —
    /// a gap in the shipped data, not a parse that went wrong.
    /// </summary>
    public long BodyBytesStated { get; set; }

    public bool IsEngineFormat { get; set; }

    /// <summary>
    /// The bytes the buffer offsets point into when the reader repacked them; null when they
    /// point into the file itself. Fable 3's planar vertex blocks are repacked into the version-36
    /// interleaved layouts here (<see cref="Engine_MdlEngine47"/>), so a consumer pairing
    /// <see cref="MeshBuffers"/> or <see cref="ClothBlocks"/> with bytes takes <c>Body ?? file</c>.
    /// </summary>
    public byte[]? Body { get; set; }

    public uint Version { get; set; }

    /// <summary>
    /// The mesh KIND from the header. 4 means grass, which the guest lights differently.
    /// </summary>
    /// <remarks>
    /// Only the value 4 is stated anywhere in the binary — it is compared twice and never
    /// enumerated — so the other values are unnamed and this stays a raw byte rather than an enum
    /// we would have to invent names for.
    /// </remarks>
    public byte MeshKind { get; set; }

    /// <summary>
    /// Fable 3's header byte 88, the field the guest registers as <c>GlobalLightingMode</c>
    /// (string 0x820EB2DC): `sub_82D70950` reads it first into the header block at model+132.
    /// `sub_82C88C88` turns it into the draw's ambient source (owner+0x2F0): 0 and 1 take AMB2, 2
    /// takes AMB3 when the level's lightmap table holds the placement and AMB0 otherwise; the
    /// repeated selector `sub_82CE4C78` takes GRASS1 when it is 4. Zero for Fable 2.
    /// </summary>
    public byte GlobalLightingMode { get; set; }

    /// <summary>How the mesh moves in wind: 0 still, 1 a low plant, 2 a tree.</summary>
    public byte MovementMode { get; set; }

    /// <summary>Whether the mesh is flattened by things colliding with it.</summary>
    public bool SphereCollisions { get; set; }

    /// <summary>Wind skew amplitude; 0.05 across every shipped mesh that has one.</summary>
    public float WindAmplitude { get; set; }

    /// <summary>Wind skew speed; 1.0 across every shipped mesh that has one.</summary>
    public float WindSpeed { get; set; }

    /// <summary>The repeated family's normal gradient at the mesh's top (.mdl +90).</summary>
    public float NormalGradientTop { get; set; }

    /// <summary>The repeated family's normal gradient at the mesh's bottom (.mdl +94).</summary>
    public float NormalGradientBottom { get; set; }

    /// <summary>True when the header says this mesh bends in wind at all.</summary>
    public bool SwaysInWind => MovementMode is 1 or 2;

    public uint LodCount { get; set; }

    public uint[] LodSizes { get; set; } = Array.Empty<uint>();

    /// <summary>Header +72 → model+96: the model's reference size for the guest's LOD pick, model units; 0 when unset.</summary>
    public float LodReference { get; set; }

    /// <summary>
    /// The coarser LOD payloads, in table order, each with its own tables. Everything else on
    /// this object — <see cref="MeshBuffers"/>, <see cref="Bones"/>, counts, tags — is LOD 0.
    /// </summary>
    public List<MdlLod> Lods { get; } = new();

    public float[] FileBoundingSphere { get; set; } = Array.Empty<float>();

    public float[] FileBoundingBox { get; set; } = Array.Empty<float>();

    public uint RigidCount { get; set; }

    public uint SkinnedCount { get; set; }

    public byte SectionFlag { get; set; }

    public List<string> Tags { get; } = new();

    public uint[] ShDescriptor { get; set; } = Array.Empty<uint>();

    public float[] ShProbeData { get; set; } = Array.Empty<float>();

    public List<MdlClothBlock> ClothBlocks { get; } = new();

    /// <summary>The skinned primitives that carry fur, one block each.</summary>
    public List<MdlFurBlock> FurBlocks { get; } = new();
}

/// <summary>
/// One mesh record's cloth block, as the guest's <c>sub_82B36E50</c> parses it. Offsets are into
/// the model's bytes. The block carries the sim mesh's own vertex set (<see cref="SimVertexCount"/>
/// vertices, its own u16 index list and UVs), the distance-constraint edges, the skin driver, the
/// four collider tables with their bone indices, the weld pairs and the seven per-item parameters.
/// </summary>
public sealed class MdlClothBlock
{
    public const int SphereRecordSize = 32;

    public const int PlaneRecordSize = 52;

    public const int CapsuleRecordSize = 56;

    public const int BoxRecordSize = 148;

    public const int WeldRecordSize = 8;

    public uint RecordIndex { get; set; }

    public uint MeshIndex { get; set; }

    public int BlockOffset { get; set; }

    public int BlockSize { get; set; }

    /// <summary>The first header word; stored by the guest at Data+16 and never read.</summary>
    public uint Unknown16 { get; set; }

    public uint SimVertexCount { get; set; }

    /// <summary>First vertex the solver integrates and collides (Data+28).</summary>
    public int SimBegin { get; set; }

    /// <summary>End (exclusive) of the integrated range (Data+32); the vertex count before file version 36.</summary>
    public int SimEnd { get; set; }

    public uint TriangleCount { get; set; }

    public uint IndexCount { get; set; }

    public uint EdgeCount { get; set; }

    public uint BonesPerVertex { get; set; }

    public uint SphereCount { get; set; }

    public uint PlaneCount { get; set; }

    public uint CapsuleCount { get; set; }

    public uint BoxCount { get; set; }

    public uint WeldCount { get; set; }

    /// <summary>
    /// Data+244..+264: attribute scale, an unread 25.0, damping, wind strength, gravity
    /// strength, inertia. See <see cref="Refii.Physics.ClothAsset"/> for the runtime meaning.
    /// </summary>
    public float[] SimParams { get; set; } = new float[6];

    /// <summary>Data+268: enables the root-motion follow together with <see cref="FollowByte1"/>.</summary>
    public byte FollowByte0 { get; set; }

    /// <summary>Data+269: the follow keeps the rotation of the root delta; clear, the pass is a no-op.</summary>
    public byte FollowByte1 { get; set; }

    public int RestPosOffset { get; set; }

    /// <summary>Per sim vertex float2 UV (Data+136); the guest's tangent build reads it.</summary>
    public int UvOffset { get; set; }

    /// <summary>Per sim vertex 4 bytes bound as GPU vertex stream 1 (Data+164); never read on the CPU.</summary>
    public int StreamOneOffset { get; set; }

    /// <summary>Per sim vertex float4 added to the recomputed tangent (Data+120).</summary>
    public int TangentWOffset { get; set; }

    /// <summary>Per sim vertex 4 bytes written into the render position's w (Data+304).</summary>
    public int PositionWOffset { get; set; }

    public int IndexListOffset { get; set; }

    public int BoneIdOffset { get; set; }

    public int WeightOffset { get; set; }

    public int FlagOffset { get; set; }

    public int EdgePairOffset { get; set; }

    public int EdgeParamOffset { get; set; }

    public int SphereOffset { get; set; }

    public int PlaneOffset { get; set; }

    public int CapsuleOffset { get; set; }

    public int BoxOffset { get; set; }

    public int WeldOffset { get; set; }

    /// <summary>The sim vertex set in the mesh's own vertex format (Data+272), nv * 16 * sh4 bytes.</summary>
    public int RawVertexOffset { get; set; }

    public uint BoneVertexCount { get; set; }
}

/// <summary>
/// A skinned primitive's fur block, the record's last field (`sub_82B26460` 0x82B27474 ->
/// `sub_82B8FD28`): the primitive's own material, the FUR material (the nameless type-2 record in
/// the material list, which no primitive names as its material), and one 20-byte record
/// {u32, 4 x f32} per vertex of the primitive. The shells extrude this primitive.
/// </summary>
public sealed class MdlFurBlock
{
    /// <summary>The primitive (skinned record) it belongs to.</summary>
    public uint RecordIndex { get; set; }

    /// <summary>furdata+0: the primitive's skin material index.</summary>
    public uint BaseMaterial { get; set; }

    /// <summary>furdata+0x10: the fur material index.</summary>
    public uint FurMaterial { get; set; }

    /// <summary>furdata+0x18: per-vertex records, the primitive's vertex count.</summary>
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

        // FABLE 3's MESH FILE, versions 46 and 47, has its own reader and never reaches the ones
        // below: a header without a body is a stub, as for version 36, and a body that does not
        // walk to its stated LOD sizes is refused rather than handed to the heuristics.
        if (Engine_MdlEngine47.IsMeshFile47(data))
        {
            if (Engine_MdlEngine47.TryParse(data, info))
            {
                return true;
            }

            info = new MdlInfo();
            if (Engine_MdlEngine47.TryReadFileHeader(data, info, out long stated47) &&
                data.Length <= Engine_MdlEngine47.BodyStart)
            {
                info.IsStub = true;
                info.BodyBytesStated = stated47;
                return true;
            }

            info = new MdlInfo();
            error = $"MeshFile version {BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(8, 4))} " +
                    "does not walk to the LOD sizes its header states";
            return false;
        }

        if (Engine_MdlEngine.TryParse(data, info))
        {
            return true;
        }

        // A MESH FILE NEWER THAN ANY READER HERE IS REFUSED, NOT GUESSED AT. The heuristic readers
        // below size their buffers from counts they find in the bytes; handed a mesh version they
        // do not know they read garbage counts and allocated without bound — Fable 3's version-47
        // meshes, before they had a reader, grew one boot texture sweep to 20 GB.
        if (data.Length >= 12 &&
            (Encoding.ASCII.GetString(data, 0, 8) is "MeshFile" or "DefMeshF") &&
            BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(8, 4)) is uint version && version > 36)
        {
            error = $"MeshFile version {version} is not read yet";
            return false;
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

            // AND READ WHAT THOSE 88 BYTES SAY. They were skipped wholesale, which threw away the
            // only statement in the shipped data about which props are vegetation.
            ReadMovementBlock(data, info);
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

    /// <summary>
    /// The last 28 bytes of the 104-byte header: how a mesh moves, and how it is lit.
    /// </summary>
    /// <remarks>
    /// DECODED FROM THE GUEST'S OWN READER, then confirmed against the shipped data. `sub_82B8F1B8`
    /// parses the header block and hands the tail to `sub_82B8F568`, whose stream order and widths
    /// are u8, u8, u8, u8, f32, f32, u8, u8, f32, f32, u8, u8, f32 — 28 bytes, which is exactly the
    /// end of the header. `sub_8222B600` then reads the first of those as a KIND (`== 4` is grass)
    /// and `sub_82B201D0` reads the second as the wind gate (`== 1`), with the two floats as its
    /// amplitude and speed.
    ///
    /// CONFIRMED BY THE DATA, because an offset derived by arithmetic is a hypothesis until
    /// something independent agrees with it. Over 1,117 shipped headers, byte 76 is 4 for exactly
    /// 12 models and every one of them is a plant — dandelion, nettle, forget-me-nots, nightshade,
    /// grass_feather, grass_short, wheat. Byte 77 separates as cleanly: 1 is low plants (turnip,
    /// bracken, bramble) and 2 is trees (beech, oak, silver birch, saplings). The names were used
    /// to CHECK the decode, never to make it — §3.1 forbids classifying by file name, and this is
    /// the difference between deriving a fact and inventing one.
    ///
    /// The guest tests the mode against 1 on the repeated-mesh path only because trees are drawn by
    /// `treematerial` instead. We have one surface shader, so both modes reach it here.
    /// </remarks>
    private static void ReadMovementBlock(byte[] data, MdlInfo info)
    {
        const int Block = 76;
        if (data.Length < Block + 12)
        {
            return;
        }

        info.MeshKind = data[Block];
        info.MovementMode = data[Block + 1];
        info.SphereCollisions = data[Block + 3] != 0;
        info.WindAmplitude = BigEndianSingle(data, Block + 4);
        info.WindSpeed = BigEndianSingle(data, Block + 8);
        // The stream's second float pair (+90, +94; `sub_82B8F568` -> resource +120, +124): the
        // normal gradient's values at the top and the bottom of the mesh (`sub_82233570`).
        if (data.Length >= Block + 22)
        {
            info.NormalGradientTop = BigEndianSingle(data, Block + 14);
            info.NormalGradientBottom = BigEndianSingle(data, Block + 18);
        }
    }

    /// <summary>The file is big endian, as everything shipped for this console is.</summary>
    private static float BigEndianSingle(byte[] data, int at) =>
        System.BitConverter.Int32BitsToSingle(
            (data[at] << 24) | (data[at + 1] << 16) | (data[at + 2] << 8) | data[at + 3]);

    private static bool Fail(string message, out string error)
    {
        error = message;
        return false;
    }
}
