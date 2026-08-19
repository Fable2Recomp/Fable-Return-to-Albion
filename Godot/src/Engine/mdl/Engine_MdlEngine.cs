using System;
using System.Collections.Generic;

namespace Refii.Models;

public static class Engine_MdlEngine
{
    /// <summary>Where the LOD payloads begin: magic(8) + version(4) + header size(4) + 88.</summary>
    public const int BodyStart = 104;

    /// <summary>
    /// The 104-byte file header, without its body.
    ///
    /// This is the whole of what <c>globals_model_headers.bnk</c> ships, and it is a complete
    /// record in its own right: the guest opens the header alone (<c>sub_82AB4C10</c>) and only
    /// later byte-ranges the payloads out of the same logical file. So it is worth reading even
    /// when the body is missing — it states how many bytes SHOULD have followed, which is the
    /// difference between "the bank has no body entry for this model" and "our parser gave up".
    ///
    /// Field meanings are from the guest's own parse (<c>sub_82AB4DC8</c> / <c>sub_82B8F1B8</c>):
    /// +8 version, +12 the remaining header size, +56 the LOD count, +60/+64/+68 the byte size
    /// of each LOD's payload. The version gate is the guest's: <c>blt 0x21 / bgt 0x24</c>.
    /// </summary>
    public static bool TryReadFileHeader(byte[] data, MdlInfo info, out long bodyBytesStated)
    {
        bodyBytesStated = 0;
        if (data.Length < BodyStart) return false;

        string magic = System.Text.Encoding.ASCII.GetString(data, 0, 8);
        if (magic is not "MeshFile" and not "DefMeshF") return false;

        var r = new MdlReader(data);

        info.Magic = magic;
        r.Position = 8;
        if (!r.U32(out uint version)) return false;
        if (version < 33 || version > 36) return false;
        info.Version = version;
        if (!r.U32(out uint hdrSize)) return false;
        if (16 + hdrSize != BodyStart) return false;
        info.HeaderSize = hdrSize;

        info.FileBoundingSphere = new float[4];
        for (int k = 0; k < 4; k++)
            if (!r.F32(out info.FileBoundingSphere[k])) return false;

        info.FileBoundingBox = new float[6];
        for (int k = 0; k < 6; k++)
            if (!r.F32(out info.FileBoundingBox[k])) return false;

        r.Position = 0x38;
        if (!r.U32(out uint lodCount)) return false;
        if (lodCount < 1 || lodCount > 3) return false;
        info.LodCount = lodCount;

        info.LodSizes = new uint[3];
        for (uint i = 0; i < lodCount; i++)
        {
            if (!r.U32(out info.LodSizes[i])) return false;
            bodyBytesStated += info.LodSizes[i];
        }

        return true;
    }

    public static bool TryParse(byte[] data, MdlInfo info)
    {
        if (!TryReadFileHeader(data, info, out long total)) return false;

        var r = new MdlReader(data);
        uint lodCount = info.LodCount;

        // THE BODY MUST COVER WHAT THE HEADER STATES, and may be longer.
        //
        // Demanding equality throws out three shipped models that are longer than their own LOD
        // table says — a 133-byte LOD 0 sitting in a 168-byte body entry — and they then fall
        // through to the legacy scanner, which finds nothing, and read as a reader defect.
        //
        // The guest never compares the two. It builds LOD i's read as base = 104 + sum of the
        // preceding sizes and length = size[i] (`sub_82AB4868`: `add r5, r9, r5` over the size
        // array at mesh+84, then `rex_lhResourceManager_Open(nameHash, &h, r5, r6, 1, 1)`), so
        // bytes past the last payload are simply never read. Trailing padding is not corruption.
        if (BodyStart + total > data.Length) return false;

        var allRecords = new List<EngRecord>();
        var allHeaders = new List<EngMeshHeader>();
        uint outBoneCount = 0;
        var outHideRegions = new List<string>();

        int off = BodyStart;
        for (uint lod = 0; lod < lodCount; lod++)
        {
            long secEnd = off + info.LodSizes[lod];
            if (secEnd > data.Length) return false;
            if (info.LodSizes[lod] == 0) { off = (int)secEnd; continue; }
            r.Position = off;

            if (!r.Skip(32)) return false;

            if (!r.U32(out uint bc)) return false;
            if (bc > 100000u) return false;
            if (lod == 0) outBoneCount = bc;

            if (lod == 0)
            {
                for (uint i = 0; i < bc; i++)
                {
                    if (!r.Strz(out string boneName)) return false;
                    if (!r.U32(out uint parentId)) return false;
                    info.Bones.Add(new MdlBone
                    {
                        Name = boneName,
                        ParentId = parentId == 0xFFFFFFFFu ? -1 : (int)parentId,
                    });
                }
            }
            else
            {
                for (uint i = 0; i < bc; i++)
                {
                    if (!r.Strz(out _) || !r.Skip(4)) return false;
                }
            }

            if (!r.U32(out uint btc)) return false;
            if (btc > 100000u) return false;

            if (lod == 0 && btc == bc && bc > 0)
            {
                info.HasBoneTransforms = true;
                for (uint i = 0; i < btc; i++)
                {
                    var tf = new float[11];
                    for (int k = 0; k < 11; k++)
                        if (!r.F32(out tf[k])) return false;
                    info.BoneTransforms.Add(tf);
                }
            }
            else
            {
                if (!r.Skip((int)(btc * 44))) return false;
            }

            if (!r.Skip(40)) return false;

            if (!r.U32(out uint mc)) return false;
            if (!r.U32(out uint rigid)) return false;
            if (!r.U32(out uint skinned)) return false;
            if (!r.U32(out uint ex52)) return false;
            if (!r.U32(out uint ex240)) return false;
            if (mc > 100000u || rigid > 100000u || skinned > 100000u) return false;
            // TypeC (instanced, 52-byte parts) is still refused, and NOT because its part
            // record is hard — it is 18 bytes then vertices then indices, same shape as TypeD.
            // The blocker is upstream: sub_82AB1868 gates the MATERIAL class on countC, and when
            // countC > 0 every material becomes a 192-byte class (off_820029D0) whose body parse
            // is a different vtable entry. The material walk below is the countC == 0 tag-switch
            // form, so an instanced model would desync in the materials long before the parts.
            // Decoding that class is the prerequisite; until then, refusing is honest.
            //
            // TypeD (tree/foliage, 240-byte parts) has no such gate and is now read properly.
            // It used to be refused here, which sent every tree model to the marker-scanning
            // fallback parser — the one that guesses at structure in a format the guest reads
            // strictly sequentially, with no seek, no offset table and no padding. A guess that
            // lands one field early decodes the next field's bytes as vertex positions.
            if (ex52 != 0) return false;

            if (lod == 0)
            {
                info.MeshCount = mc;
                info.RigidCount = rigid;
                info.SkinnedCount = skinned;
            }

            if (!r.U8(out byte flag)) return false;
            if (lod == 0) info.SectionFlag = flag;

            if (!r.U32(out uint tagc)) return false;
            if (tagc > 100000u) return false;
            for (uint t = 0; t < tagc; t++)
            {
                if (!r.Strz(out string tagName)) return false;
                if (!r.Skip(1)) return false;
                if (lod == 0) info.Tags.Add(tagName);
            }

            var shdesc = new uint[5];
            for (int i = 0; i < 5; i++)
                if (!r.U32(out shdesc[i])) return false;
            uint sh4 = shdesc[4];
            if (sh4 > 16u) return false;
            if (lod == 0) info.ShDescriptor = shdesc;

            if (!r.U32(out uint fc)) return false;
            if (fc > 1000000u) return false;
            if (lod == 0)
            {
                info.ShProbeData = new float[fc];
                for (uint i = 0; i < fc; i++)
                    if (!r.F32(out info.ShProbeData[i])) return false;
            }
            else
            {
                if (!r.Skip((int)(fc * 4))) return false;
            }

            if (!r.U32(out uint hrc)) return false;
            if (hrc > 1000000u) return false;
            for (uint i = 0; i < hrc; i++)
            {
                if (!r.Strz(out string hrName)) return false;
                if (lod == 0) outHideRegions.Add(hrName);
            }

            bool capture = lod == 0;

            for (uint m = 0; m < mc; m++)
            {
                var hdr = new EngMeshHeader();
                if (!r.U32(out uint sel)) return false;
                hdr.ShaderType = sel;

                if (sel == 2)
                {
                    if (!r.Skip(68)) return false;
                    if (capture) allHeaders.Add(hdr);
                    continue;
                }

                if (!r.Strz(out string meshName)) return false;
                hdr.Name = meshName;

                // THE 45 BYTES AFTER THE NAME ARE THE MATERIAL'S SCALAR BLOCK, and they used to be
                // skipped in four pieces (8 + 21 + 4 + 12). The total was right — `sub_82B243E8`
                // consumes exactly 45, unconditionally, with no dependence on the file version —
                // but everything inside it was thrown away, including both numbers the specular
                // response is built from.
                //
                // The layout is that function's read order, byte for byte:
                //
                //     0  f32  -> material+60   specular power MIN        (shader c24.x)
                //     4  f32  -> material+64 = +60 + this, a RANGE       (shader c24.y)
                //     8  s8   -> material+84
                //     9  f32  -> material+88                             (c24.w)
                //    13  f32  -> material+92                             (c25.x)
                //    17  f32  -> material+96
                //    21  f32  -> material+100                            (c25.y)
                //    25  f32  -> material+104                            (c25.z)
                //    29  f32  -> material+72   self-illumination scale   (c24.z)
                //    33  f32  -> material+76   |  read by sub_82A21798,
                //    37  f32  -> material+80   |  a read-two-floats helper
                //    41  u8   -> material+11
                //    42  u8   -> material+10
                //    43  u8   -> material+9
                //    44  u8   -> material+12
                //
                // Only the three the shipped shader consumes are kept. The rest still has to be
                // READ rather than skipped as a lump, because a wrong split here desynchronises
                // every material in the file rather than corrupting one field.
                if (!r.F32(out float specPowerMin)) return false;
                if (!r.F32(out float specPowerRange)) return false;
                if (!r.Skip(1)) return false;
                if (!r.Skip(20)) return false;
                if (!r.F32(out float selfIllumScale)) return false;
                if (!r.Skip(8)) return false;
                if (!r.Skip(4)) return false;

                if (!r.U32(out uint matc)) return false;
                if (matc > 100000u) return false;

                for (uint j = 0; j < matc; j++)
                {
                    var mat = new MdlMaterial();
                    if (!r.Strz(out string diffuse)) return false;
                    if (!r.Strz(out string specular)) return false;
                    if (!r.Strz(out string normal)) return false;
                    if (!r.Strz(out string metallic)) return false;
                    if (!r.Strz(out string extra)) return false;
                    mat.Diffuse = diffuse;
                    mat.Specular = specular;
                    mat.Normal = normal;
                    mat.EnvironmentMap = metallic;
                    mat.SelfIllumination = extra;
                    mat.ShaderType = sel;

                    var shaderParams = new string[4];
                    for (int k = 0; k < 4; k++)
                    {
                        if (!r.Strz(out string sp)) return false;
                        shaderParams[k] = sp;
                    }
                    mat.ShaderParams = shaderParams;

                    var matFloats = new float[2];
                    if (!r.F32(out matFloats[0])) return false;
                    if (!r.F32(out matFloats[1])) return false;
                    mat.MaterialFloats = matFloats;

                    // ONE SCALAR BLOCK, N TEXTURE SETS. The guest reads the block once per
                    // material and then a count of 180-byte texture-set records, so the specular
                    // range and the self-illumination scale belong to the parent and every record
                    // under it shares them. Copying them down keeps the consumer holding a single
                    // object instead of having to carry the parent alongside it.
                    mat.SpecularPowerMin = specPowerMin;
                    mat.SpecularPowerMax = specPowerMin + specPowerRange;
                    mat.SelfIlluminationScale = selfIllumScale;

                    hdr.Materials.Add(mat);
                }

                if (capture) allHeaders.Add(hdr);
            }

            uint vtxAccum = 0;
            for (uint i = 0; i < rigid; i++)
            {
                if (!ReadRigidRecord(r, lod == 0 || bc > 0, sh4, out EngRecord rec, info, (uint)allRecords.Count))
                    return false;
                vtxAccum += rec.VertexCount;
                if (capture) allRecords.Add(rec);
            }

            for (uint i = 0; i < skinned; i++)
            {
                if (!ReadSkinnedRecord(r, sh4, out EngRecord rec, info, (uint)allRecords.Count))
                    return false;
                if (capture) allRecords.Add(rec);
            }

            // D BEFORE C, which is the guest's order and NOT the order the counts are read in.
            // sub_82AB1868 reads countA, countB, countC, countD and then runs its loops at
            // 0x82AB25D0, 0x82AB2664, 0x82AB26F0, 0x82AB277C — A, B, D, C. Parsing in count
            // order silently mis-slices any model carrying both, which is the whole file from
            // that point on.
            for (uint i = 0; i < ex240; i++)
            {
                if (!ReadTreeRecord(r, sh4, out EngRecord rec)) return false;
                vtxAccum += rec.VertexCount;
                if (capture) allRecords.Add(rec);
            }

            if (flag != 0)
            {
                if (!r.Skip((int)(vtxAccum * 4))) return false;
            }

            if (r.Position != (int)secEnd) return false;
            off = (int)secEnd;
        }

        if (allRecords.Count == 0) return false;

        info.IsEngineFormat = true;
        info.HideRegions.AddRange(outHideRegions);

        foreach (EngMeshHeader hdr in allHeaders)
        {
            var mesh = new MdlMesh { Name = hdr.Name };
            mesh.Materials.AddRange(hdr.Materials);
            info.Meshes.Add(mesh);
        }

        foreach (EngRecord rec in allRecords)
        {
            var mb = new MdlMeshBuffer
            {
                VertexCount = rec.VertexCount,
                VertexOffset = rec.VertexOffset,
                FaceCount = rec.IndexCount,
                FaceOffset = rec.IndexOffset,
                MeshIndex = rec.MeshIdx,
                IsEngineSkinned = rec.Skinned,
                IsEngineRigid = !rec.Skinned && !rec.Tree,
                IsEngineTree = rec.Tree,
            };

            foreach (EngSubMesh esm in rec.Submeshes)
            {
                mb.SubMeshes.Add(new MdlSubMesh
                {
                    RegionIndex = esm.RegionIndex,
                    MaterialIndex = esm.MatIdx,
                    FaceCount = esm.FaceCount,
                    StartIndex = esm.StartIdx,
                    PrimitiveType = esm.PrimitiveType,
                    BoundingBox = esm.BoundingBox,
                });
            }

            info.MeshBuffers.Add(mb);
        }

        return true;
    }

    private sealed class EngMeshHeader
    {
        public string Name { get; set; } = string.Empty;
        public uint ShaderType { get; set; }
        public List<MdlMaterial> Materials { get; } = new();
    }

    private sealed class EngSubMesh
    {
        public byte MatIdx { get; set; }
        public uint StartIdx { get; set; }
        public uint RegionIndex { get; set; }
        public uint FaceCount { get; set; }
        public byte PrimitiveType { get; set; }
        public float[] BoundingBox { get; set; } = Array.Empty<float>();
    }

    private sealed class EngRecord
    {
        public string MeshName { get; set; } = string.Empty;
        public bool Skinned { get; set; }

        /// <summary>A TypeD tree/foliage part: stride 36, no batches.</summary>
        public bool Tree { get; set; }
        public uint IndexCount { get; set; }
        public uint VertexCount { get; set; }
        public uint MeshIdx { get; set; }
        public int VertexOffset { get; set; }
        public int IndexOffset { get; set; }
        public List<EngSubMesh> Submeshes { get; } = new();
    }

    private static bool ReadSubmeshes(MdlReader r, uint count, List<EngSubMesh> submeshes)
    {
        for (uint s = 0; s < count; s++)
        {
            if (!r.U32(out uint marker)) return false;
            if (!r.U32(out uint matIdx)) return false;
            if (!r.U8(out byte primType)) return false;
            if (!r.U32(out uint faceCount)) return false;
            if (!r.U32(out uint startIdx)) return false;

            var bb = new float[6];
            for (int k = 0; k < 6; k++)
                if (!r.F32(out bb[k])) return false;

            submeshes.Add(new EngSubMesh
            {
                MatIdx = (byte)(matIdx & 0xFF),
                StartIdx = startIdx,
                RegionIndex = marker,
                FaceCount = faceCount,
                PrimitiveType = primType,
                BoundingBox = bb,
            });
        }

        return true;
    }

    private static bool ReadClothBlock(MdlReader r, MdlInfo info, uint recIndex)
    {
        int start = r.Position;

        if (!r.U32(out uint a16) || !r.U32(out uint u16c) || !r.U32(out uint nv) ||
            !r.U32(out uint a28) || !r.U32(out uint a32) || !r.U32(out uint a24) ||
            !r.U32(out uint n77)) return false;
        if (nv > 1000000u || u16c > 4000000u || n77 > 4000000u) return false;

        if (!r.U32(out uint boneVtxCount) || !r.U32(out uint bonesPerVtx)) return false;
        if (boneVtxCount > 1000000u || bonesPerVtx > 64u) return false;

        int boneIdOff = r.Position;
        if (!r.Skip((int)(boneVtxCount * bonesPerVtx))) return false;
        int weightOff = r.Position;
        if (!r.Skip((int)(boneVtxCount * 16))) return false;

        if (!r.U32(out uint nDist) || !r.U32(out uint nBendA) || !r.U32(out uint nBendB) ||
            !r.U32(out uint nVol) || !r.U32(out uint nLink)) return false;

        var sp = new float[6];
        for (int k = 0; k < 6; k++)
            if (!r.F32(out sp[k])) return false;
        if (!r.Skip(2)) return false;

        int restPosOff = r.Position;
        if (!r.Skip((int)(nv * 12))) return false;
        if (!r.Skip((int)(nv * 8))) return false;
        if (!r.Skip((int)(nv * 4))) return false;
        if (!r.Skip((int)(nv * 16))) return false;
        if (!r.Skip((int)(nv * 4))) return false;

        int idxListOff = r.Position;
        if (!r.Skip((int)(u16c * 2))) return false;
        int edgePairOff = r.Position;
        if (!r.Skip((int)(n77 * 4))) return false;
        int edgeParamOff = r.Position;
        if (!r.Skip((int)(n77 * 16))) return false;

        int flagOff = r.Position;
        if (!r.Skip((int)(nv * 1))) return false;
        if (!r.Skip((int)(nDist * 32))) return false;
        if (!r.Skip((int)(nBendA * 52))) return false;
        if (!r.Skip((int)(nBendB * 56))) return false;
        if (!r.Skip((int)(nVol * 148))) return false;
        if (!r.Skip((int)(nLink * 8))) return false;
        if (!r.Skip((int)(nv * 16))) return false;

        info.ClothBlocks.Add(new MdlClothBlock
        {
            RecordIndex = recIndex,
            MeshIndex = 0,
            BlockOffset = start,
            BlockSize = r.Position - start,
            SimVertexCount = nv,
            TriangleCount = a24,
            IndexCount = u16c,
            EdgeCount = n77,
            BonesPerVertex = bonesPerVtx,
            DistConstraints = nDist,
            BendConstraintsA = nBendA,
            BendConstraintsB = nBendB,
            VolConstraints = nVol,
            LinkConstraints = nLink,
            SimParams = sp,
            RestPosOffset = restPosOff,
            IndexListOffset = idxListOff,
            BoneIdOffset = boneIdOff,
            WeightOffset = weightOff,
            FlagOffset = flagOff,
            EdgePairOffset = edgePairOff,
            EdgeParamOffset = edgeParamOff,
            BoneVertexCount = boneVtxCount,
        });

        return true;
    }

    private static bool ReadRigidRecord(
        MdlReader r, bool full, uint sh4, out EngRecord rec, MdlInfo info, uint recIndex)
    {
        rec = new EngRecord { Skinned = false };

        if (!r.Strz(out string meshName)) return false;
        rec.MeshName = meshName;
        if (!r.U8(out _)) return false;

        if (!r.U32(out _) || !r.U32(out uint idx1) || !r.U32(out _) ||
            !r.U32(out uint idxCount) || !r.U32(out uint vtxCount)) return false;
        rec.MeshIdx = idx1;
        rec.IndexCount = idxCount;
        rec.VertexCount = vtxCount;
        if (idxCount > 4000000u || vtxCount > 4000000u) return false;

        if (!r.Skip(40)) return false;

        if (!r.U32(out uint subc)) return false;
        if (subc > 100000u) return false;
        if (!ReadSubmeshes(r, subc, rec.Submeshes)) return false;

        if (full)
        {
            rec.VertexOffset = r.Position;
            if (!r.Skip((int)(vtxCount * 20))) return false;
            rec.IndexOffset = r.Position;
            if (!r.Skip((int)(idxCount * 2))) return false;
            if (!r.Skip((int)(vtxCount * 16 * sh4))) return false;
        }
        else
        {
            rec.VertexOffset = r.Position;
            if (!r.Skip((int)(vtxCount * 16 * sh4))) return false;
            rec.IndexOffset = r.Position;
            if (!r.Skip((int)(idxCount * 2))) return false;
        }

        if (!r.U32(out uint ext1)) return false;
        if (ext1 == 1)
        {
            if (!ReadClothBlock(r, info, recIndex)) return false;
        }
        else if (ext1 != 0) return false;

        return true;
    }

    /// <summary>
    /// Reads one TypeD (tree/foliage) part — <c>sub_82B352A8</c>.
    /// </summary>
    /// <remarks>
    /// THE SIMPLEST OF THE FOUR PART CLASSES: an 18-byte prologue, the vertex blob, the index
    /// blob, and an optional second stream. It has NO name string, NO bounding box, NO submesh
    /// batch array and NONE of the trailing cloth/extra flags that TypeA and TypeB carry — and
    /// those absences are exactly what a marker-scanning parser goes looking for and never
    /// finds, which is how tree models used to end up decoded from the wrong offset.
    ///
    /// Prologue, in stream order:
    ///   +0x00 u8   consumed and discarded by the guest
    ///   +0x01 u8   flag -> obj+224
    ///   +0x02 u32  material index
    ///   +0x06 u32  unknown, 4 bytes
    ///   +0x0A u32  index count
    ///   +0x0E u32  vertex count
    ///
    /// There is no batch array, so the whole index buffer is ONE draw: the guest hard-codes
    /// primitive 4 at 0x82B35738, which is TRIANGLELIST on this hardware (Xbox 360 inverts the
    /// PC D3D9 values — strip is 6 there, which is why `? 4 : 6` reads as list/strip
    /// throughout this engine).
    ///
    /// The second stream is the shared one: 16 bytes per vector, <paramref name="sh4"/> vectors
    /// per vertex, present only when the model declared any.
    /// </remarks>
    private static bool ReadTreeRecord(MdlReader r, uint sh4, out EngRecord rec)
    {
        rec = new EngRecord { Tree = true };

        if (!r.U8(out _)) return false;
        if (!r.U8(out _)) return false;

        if (!r.U32(out uint matIdx) || !r.U32(out _) ||
            !r.U32(out uint idxCount) || !r.U32(out uint vtxCount)) return false;

        rec.MeshIdx = matIdx;
        rec.IndexCount = idxCount;
        rec.VertexCount = vtxCount;

        // The same bounds the rigid and skinned readers use: a vertex count cannot exceed the
        // 16-bit index space below the 0xFFFF restart sentinel, while an index COUNT is not
        // bounded that way at all.
        if (vtxCount > 65_535u || idxCount > 4_000_000u) return false;

        rec.VertexOffset = r.Position;
        if (!r.Skip((int)(vtxCount * 36))) return false;

        rec.IndexOffset = r.Position;
        if (!r.Skip((int)(idxCount * 2))) return false;

        if (!r.Skip((int)(vtxCount * 16 * sh4))) return false;

        return true;
    }

    private static bool ReadSkinnedRecord(
        MdlReader r, uint sh4, out EngRecord rec, MdlInfo info, uint recIndex)
    {
        rec = new EngRecord { Skinned = true };

        if (r.Position < r.Length && r[r.Position] == 0x01) r.Position++;

        if (!r.U32(out _) || !r.U32(out uint idx1) || !r.U32(out _) ||
            !r.U32(out uint idxCount) || !r.U32(out uint vtxCount)) return false;
        rec.MeshIdx = idx1;
        rec.IndexCount = idxCount;
        rec.VertexCount = vtxCount;
        if (idxCount > 4000000u || vtxCount > 4000000u) return false;

        if (!r.U32(out uint subc)) return false;
        if (subc > 100000u) return false;
        if (!ReadSubmeshes(r, subc, rec.Submeshes)) return false;

        rec.VertexOffset = r.Position;
        if (!r.Skip((int)(vtxCount * 28))) return false;
        rec.IndexOffset = r.Position;
        if (!r.Skip((int)(idxCount * 2))) return false;
        if (!r.Skip((int)(vtxCount * 16 * sh4))) return false;

        if (!r.U32(out uint ext1)) return false;
        if (ext1 == 1)
        {
            if (!ReadClothBlock(r, info, recIndex)) return false;
        }
        else if (ext1 != 0) return false;

        if (!r.U32(out uint ext2)) return false;
        if (ext2 == 1)
        {
            if (!r.U32(out uint a0) || !r.U32(out uint a1v) || !r.U32(out uint mcount)) return false;
            if (mcount > 4000000u) return false;

            int dataOff = r.Position;
            if (!r.Skip((int)(mcount * 20))) return false;

            info.MorphTargets.Add(new MdlMorphTarget
            {
                RecordIndex = recIndex,
                Header0 = a0,
                Header1 = a1v,
                Count = mcount,
                DataOffset = dataOff,
            });
        }
        else if (ext2 != 0) return false;

        return true;
    }
}
