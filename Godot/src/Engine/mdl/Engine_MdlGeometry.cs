using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Refii.Models;

public sealed class MdlGeometry
{
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The body cluster this submesh belongs to — the model's region name its marker indexes,
    /// empty when the model states none. A dressed character's garments hide clusters by name.
    /// </summary>
    public string Region { get; set; } = string.Empty;

    /// <summary>
    /// The record index when this geometry came from a buffer with a cloth block, else -1. The
    /// guest draws such a record on its own — the cloth proxy replaces exactly that submesh
    /// draw — so it is never merged with another record's geometry, or hiding it for the
    /// cloth would take the neighbours with it (the whole coat for a coat's skirt).
    /// </summary>
    public int ClothRecord { get; set; } = -1;

    public float[] Positions { get; set; } = Array.Empty<float>();

    public float[] Normals { get; set; } = Array.Empty<float>();

    public float[] Uvs { get; set; } = Array.Empty<float>();

    public float[] Tangents { get; set; } = Array.Empty<float>();

    public int[] BoneIds { get; set; } = Array.Empty<int>();

    public float[] BoneWeights { get; set; } = Array.Empty<float>();

    public uint[] Indices { get; set; } = Array.Empty<uint>();

    public MdlMaterial? Material { get; set; }

    /// <summary>
    /// For each vertex here, which vertex of the MODEL's own concatenated buffers it came from.
    /// </summary>
    /// <remarks>
    /// Needed because our vertices are not the file's vertices. Splitting a mesh buffer into one
    /// surface per submesh keeps only the vertices that submesh's indices reach and renumbers
    /// them from zero, so a vertex two submeshes share is emitted TWICE and every index after it
    /// shifts. Anything the file states PER VERTEX — the .lmp's authored per-vertex bake is the
    /// one that matters — is indexed against the original numbering and cannot survive that
    /// without this map.
    ///
    /// It is what turns "our mesh has 770 vertices, the bake has 762" from an unexplained
    /// mismatch into an exact lookup: the bake is not short, we emit eight vertices twice.
    /// </remarks>
    public int[] SourceVertices { get; set; } = Array.Empty<int>();

    public bool AuthoredNormals { get; set; }

    /// <summary>
    /// Built from a buffer whose indices address LOD 0's vertices, so
    /// <see cref="SourceVertices"/> is in LOD 0's numbering; false for a coarser LOD's own
    /// vertices, whose numbering is that payload's alone.
    /// </summary>
    public bool SharesLod0Vertices { get; set; }

    /// <summary>
    /// Coarser levels over this surface, finest first: the level's mean triangle edge length in
    /// model units, and its triangle list indexing <see cref="Positions"/> in the file's
    /// winding. Empty for a model that ships one LOD.
    /// </summary>
    public List<(float EdgeLength, uint[] Indices)> Lods { get; } = new();

    /// <summary>
    /// A coarser piece over LOD 0's vertices (<see cref="SharesLod0Vertices"/>) is its indices and
    /// <see cref="SourceVertices"/> alone: nearly every vertex is already on the surface it joins,
    /// so its arrays stay empty and this buffer is decoded only when <see cref="Engine_MdlLods"/>
    /// must append one. Always a rigid buffer, whose layout has tangents.
    /// </summary>
    internal MdlMeshBuffer? VerticesIn { get; set; }

    public int VertexCount => VerticesIn is null ? Positions.Length / 3 : SourceVertices.Length;

    public int TriangleCount => Indices.Length / 3;

    public bool HasTangents => Tangents.Length > 0 || VerticesIn is not null;

    public bool HasBones => BoneIds.Length > 0;

    private bool? _hasExplicitBothSides;

    /// <summary>
    /// Whether this surface explicitly models both sides as coplanar opposite-wound triangle pairs
    /// at identical 3D vertex positions (such as double-sided shop posters, banners, and cutout trim),
    /// evaluated lazily once from <see cref="Positions"/> and <see cref="Indices"/>.
    /// </summary>
    public bool HasExplicitBothSides
    {
        get => _hasExplicitBothSides ??= Engine_MdlGeometry.DetectExplicitBothSides(Positions, Indices);
        set => _hasExplicitBothSides = value;
    }
}

public static class Engine_MdlGeometry
{
    /// <summary>
    /// Returns <see langword="true"/> when at least half of the non-degenerate triangles in
    /// <paramref name="indices"/> pair up with an opposite-wound coplanar twin sharing the same
    /// quantized 3D vertex coordinates in <paramref name="positions"/>.
    /// </summary>
    public static bool DetectExplicitBothSides(ReadOnlySpan<float> positions, ReadOnlySpan<uint> indices)
    {
        int triCount = indices.Length / 3;
        int vertCount = positions.Length / 3;
        if (triCount < 2 || vertCount < 3)
        {
            return false;
        }

        const float quantizeScale = 2048.0f; // ~0.49 mm grid, inside IEEE half-float precision
        int[] canonical = ArrayPool<int>.Shared.Rent(vertCount);
        try
        {
            var posMap = new Dictionary<(int X, int Y, int Z), int>(vertCount);
            int uniqueCount = 0;
            for (int v = 0; v < vertCount; v++)
            {
                float px = positions[v * 3];
                float py = positions[v * 3 + 1];
                float pz = positions[v * 3 + 2];
                if (!float.IsFinite(px) || !float.IsFinite(py) || !float.IsFinite(pz))
                {
                    canonical[v] = -1;
                    continue;
                }

                var key = (
                    (int)MathF.Round(px * quantizeScale),
                    (int)MathF.Round(py * quantizeScale),
                    (int)MathF.Round(pz * quantizeScale));
                if (!posMap.TryGetValue(key, out int id))
                {
                    id = uniqueCount++;
                    posMap[key] = id;
                }

                canonical[v] = id;
            }

            if (uniqueCount < 3)
            {
                return false;
            }

            var seenDirected = new Dictionary<(int A, int B, int C), int>(triCount);
            int validTriangles = 0;
            int pairedTriangles = 0;

            for (int t = 0; t < triCount; t++)
            {
                uint i0 = indices[t * 3];
                uint i1 = indices[t * 3 + 1];
                uint i2 = indices[t * 3 + 2];
                if (i0 >= (uint)vertCount || i1 >= (uint)vertCount || i2 >= (uint)vertCount)
                {
                    continue;
                }

                int a = canonical[i0];
                int b = canonical[i1];
                int c = canonical[i2];
                if (a < 0 || b < 0 || c < 0 || a == b || b == c || c == a)
                {
                    continue;
                }

                validTriangles++;

                // Rotate cyclically so the smallest canonical vertex index leads while preserving winding.
                (int A, int B, int C) dirKey;
                (int A, int B, int C) oppKey;
                if (a < b && a < c)
                {
                    dirKey = (a, b, c);
                    oppKey = (a, c, b);
                }
                else if (b < a && b < c)
                {
                    dirKey = (b, c, a);
                    oppKey = (b, a, c);
                }
                else
                {
                    dirKey = (c, a, b);
                    oppKey = (c, b, a);
                }

                if (seenDirected.TryGetValue(oppKey, out int oppRemaining) && oppRemaining > 0)
                {
                    seenDirected[oppKey] = oppRemaining - 1;
                    pairedTriangles += 2;
                }
                else
                {
                    seenDirected.TryGetValue(dirKey, out int cur);
                    seenDirected[dirKey] = cur + 1;
                }
            }

            return validTriangles >= 2 && pairedTriangles * 2 >= validTriangles;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(canonical);
        }
    }
    // THE FILE'S NORMALS ARE THE CONTRACT. Every engine layout states a HEND3N NORMAL0 at +8
    // (sub_82A8B0E0 / sub_82A750E8 / sub_82A70510) and it is outward on props and characters
    // alike; the winding-derived recompute is inverted against that winding and is only a
    // fallback for buffers that carry no normal.
    private const int VertexStride = 28;
    private const int UvOffset = 20;
    private const int RigidStride = 20;

    public static bool TryBuild(byte[] data, MdlInfo info, out List<MdlGeometry> geometry)
    {
        var clothRecords = new HashSet<int>();
        foreach (MdlClothBlock block in info.ClothBlocks)
        {
            clothRecords.Add((int)block.RecordIndex);
        }

        return TryBuild(info.Body ?? data, info.MeshBuffers, info.Meshes, info.HideRegions,
            info.Bones.Count, out geometry, clothRecords);
    }

    /// <summary>
    /// One payload's buffers against that payload's own tables: LOD 0's through the public
    /// overload, a coarser LOD's through <see cref="MdlLod"/>.
    /// </summary>
    internal static bool TryBuild(
        byte[] data, IReadOnlyList<MdlMeshBuffer> buffers, IReadOnlyList<MdlMesh> meshes,
        IReadOnlyList<string> hideRegions, int boneCount, out List<MdlGeometry> geometry,
        IReadOnlySet<int>? clothRecords = null)
    {
        geometry = new List<MdlGeometry>();
        int bufferIndex = -1;

        // Where this buffer's vertices start in the model's own numbering. It advances for EVERY
        // buffer including the ones we go on to skip, because the file's numbering does not care
        // which of them we managed to decode — a bake is indexed against all of them.
        int vertexBase = 0;

        foreach (MdlMeshBuffer mb in buffers)
        {
            bufferIndex++;
            int clothRecord = clothRecords is not null && clothRecords.Contains(bufferIndex) ? bufferIndex : -1;

            // A buffer over LOD 0's vertices numbers them as LOD 0 does.
            int bufferBase = mb.SharesLod0Vertices ? mb.SharedVertexBase : vertexBase;
            vertexBase += (int)mb.VertexCount;

            if (mb.VertexCount == 0 || mb.FaceCount == 0)
            {
                continue;
            }

            if ((long)mb.FaceOffset + ((long)mb.FaceCount * 2) > data.Length)
            {
                continue;
            }

            int vertexCount = (int)mb.VertexCount;
            bool lazyVertices = TakesVerticesLazily(mb);

            // A buffer split into submeshes is copied out of these arrays and then done with.
            bool rented = !lazyVertices && mb.SubMeshes.Count > 1 && !mb.IsFoliagePath &&
                (mb.IsEngineRigid || mb.IsEngineSkinned || mb.IsEngineTree);

            float[] positions = Array.Empty<float>(), uvs = positions;
            float[]? normals = null, tangents = null, boneWeights = null;
            int[]? boneIds = null;
            if (lazyVertices)
            {
                if ((long)mb.VertexOffset + ((long)mb.VertexCount * RigidStride) > data.Length)
                {
                    continue;
                }
            }
            else if (rented)
            {
                if (!TryDecodeEngineVertices(data, mb, rented,
                        out positions, out uvs, out normals, out tangents, out boneIds, out boneWeights))
                {
                    continue;
                }
            }
            else if (!TryDecodeVertices(data, mb,
                    out positions, out uvs, out normals, out tangents, out boneIds, out boneWeights))
            {
                continue;
            }

            if (boneIds is not null && boneWeights is not null)
            {
                FilterBoneWeights(boneIds, boneWeights, boneCount, vertexCount);
            }

            if (!lazyVertices)
            {
                ConvertToYUp(positions, normals, tangents, vertexCount);
            }

            int words = (int)mb.FaceCount;
            ushort[] stripWords = ArrayPool<ushort>.Shared.Rent(words);
            Span<ushort> strip = stripWords.AsSpan(0, words);
            BinaryPrimitives.ReverseEndianness(
                MemoryMarshal.Cast<byte, ushort>(data.AsSpan(mb.FaceOffset, words * 2)), strip);

            if (mb.SubMeshes.Count > 1)
            {
                AddSubMeshSurfaces(geometry, meshes, hideRegions, mb, positions, uvs, normals,
                    tangents, boneIds, boneWeights, strip, bufferBase, clothRecord, lazyVertices);
                ArrayPool<ushort>.Shared.Return(stripWords);
                if (rented)
                {
                    ArrayPool<float>.Shared.Return(positions);
                    ArrayPool<float>.Shared.Return(uvs);
                    ArrayPool<float>.Shared.Return(normals!);
                    ArrayPool<float>.Shared.Return(tangents!);
                    if (boneIds is not null)
                    {
                        ArrayPool<int>.Shared.Return(boneIds);
                        ArrayPool<float>.Shared.Return(boneWeights!);
                    }
                }

                continue;
            }

            // THE SECTION STATES ITS PRIMITIVE TYPE; INFERRING IT IS A GUESS THAT CAN BE WRONG.
            //
            // The file's section record carries a strip flag — 0 is a triangle list, non-zero a
            // strip — and the shipped data backs it exactly: of 339 buffers whose sections all
            // say list, 339 have `indexCount == 3 x primCount` and NONE contains a restart index.
            // Deciding from the data instead ("no restart, therefore a list") is right almost
            // always and wrong where a single strip section happens to carry no restart:
            // `OB_PotionBottle_Java.mdl` is such a file, and reading it as a list shreds the mesh
            // (no edge is shared by two triangles, against half of them when read as a strip).
            //
            // A buffer with no section at all keeps the old inference — there is nothing to follow.
            bool statedList = mb.SubMeshes.Count == 1 && mb.SubMeshes[0].PrimitiveType == 0;
            bool asList = mb.SubMeshes.Count == 1
                ? statedList
                : ((ReadOnlySpan<ushort>)strip).IndexOf((ushort)0xFFFF) < 0;

            uint[] scratch = ArrayPool<uint>.Shared.Rent(words * 3);
            int indexCount = 0;
            if (!asList)
            {
                indexCount = MdlReader.BuildTrianglesFromStrip(strip, scratch);
            }
            else
            {
                indexCount = strip.Length / 3 * 3;
                for (int i = 0; i < indexCount; ++i)
                {
                    scratch[i] = strip[i];
                }
            }

            ArrayPool<ushort>.Shared.Return(stripWords);
            uint maxIndex = 0;
            for (int i = 0; i < indexCount; ++i)
            {
                if (scratch[i] > maxIndex) maxIndex = scratch[i];
            }

            if (indexCount < 3 || maxIndex >= mb.VertexCount)
            {
                ArrayPool<uint>.Shared.Return(scratch);
                continue;
            }

            ReadOnlySpan<uint> indices = scratch.AsSpan(0, indexCount);
            var geo = new MdlGeometry
            {
                Positions = positions,
                Uvs = uvs,
                Indices = indices.ToArray(),
                Normals = lazyVertices ? Array.Empty<float>() : normals ??
                          MdlReader.ComputeSmoothNormals(vertexCount, indices, positions),
                AuthoredNormals = normals is not null,
                Tangents = tangents ?? Array.Empty<float>(),
                BoneIds = boneIds ?? Array.Empty<int>(),
                BoneWeights = boneWeights ?? Array.Empty<float>(),
                Name = lazyVertices ? string.Empty : ResolveName(meshes, mb.MeshIndex, -1),
                Material = ResolveMaterial(meshes, mb.MeshIndex, 0),
                SourceVertices = Identity(bufferBase, vertexCount),
                SharesLod0Vertices = mb.SharesLod0Vertices,
                VerticesIn = lazyVertices ? mb : null,
                ClothRecord = clothRecord,
            };

            ArrayPool<uint>.Shared.Return(scratch);
            geometry.Add(geo);
        }

        return geometry.Count > 0;
    }

    /// <summary>A buffer built as <see cref="MdlGeometry.VerticesIn"/> says, without decoding its vertices.</summary>
    private static bool TakesVerticesLazily(MdlMeshBuffer mb) =>
        mb.SharesLod0Vertices && mb.IsEngineRigid && !mb.IsFoliagePath;

    /// <summary>
    /// The vertices of a buffer over LOD 0's, in Y-up, for a piece that must append one.
    /// The arrays are rented; hand them back with <see cref="ReturnRigidVertices"/>.
    /// </summary>
    internal static bool TryRentRigidVertices(
        byte[] data, MdlMeshBuffer mb,
        out float[] positions, out float[] uvs, out float[] normals, out float[] tangents)
    {
        if (!TryDecodeEngineVertices(data, mb, rent: true,
                out positions, out uvs, out normals, out tangents, out _, out _))
        {
            return false;
        }

        ConvertToYUp(positions, normals, tangents, (int)mb.VertexCount);
        return true;
    }

    internal static void ReturnRigidVertices(float[] positions, float[] uvs, float[] normals, float[] tangents)
    {
        ArrayPool<float>.Shared.Return(positions);
        ArrayPool<float>.Shared.Return(uvs);
        ArrayPool<float>.Shared.Return(normals);
        ArrayPool<float>.Shared.Return(tangents);
    }

    private static void ConvertToYUp(float[] positions, float[]? normals, float[]? tangents, int vertexCount)
    {
        ConvertZUpToYUp(positions.AsSpan(0, vertexCount * 3));
        if (normals is not null)
        {
            ConvertZUpToYUp(normals.AsSpan(0, vertexCount * 3));
        }

        if (tangents is not null)
        {
            ConvertTangentsZUpToYUp(tangents.AsSpan(0, vertexCount * 4));
        }
    }

    private static void ConvertZUpToYUp(Span<float> values)
    {
        for (int i = 0; i + 2 < values.Length; i += 3)
        {
            float y = values[i + 1];
            values[i + 1] = values[i + 2];
            values[i + 2] = -y;
        }
    }

    private static void ConvertTangentsZUpToYUp(Span<float> tangents)
    {
        for (int i = 0; i + 3 < tangents.Length; i += 4)
        {
            float y = tangents[i + 1];
            tangents[i + 1] = tangents[i + 2];
            tangents[i + 2] = -y;
        }
    }

    private static void FilterBoneWeights(int[] boneIds, float[] boneWeights, int boneCount, int vertexCount)
    {
        for (int v = 0; v < vertexCount; v++)
        {
            int baseIdx = v * 4;
            int outI = 0;
            float sum = 0f;

            for (int k = 0; k < 4; k++)
            {
                int id = boneIds[baseIdx + k];
                float w = boneWeights[baseIdx + k];
                if (id >= 255) continue;
                if (boneCount > 0 && id >= boneCount) continue;
                if (w <= 0f) continue;
                boneIds[baseIdx + outI] = id;
                boneWeights[baseIdx + outI] = w;
                sum += w;
                outI++;
            }

            for (int k = outI; k < 4; k++)
            {
                boneIds[baseIdx + k] = 0;
                boneWeights[baseIdx + k] = 0f;
            }

            if (outI > 0 && sum > 1e-6f)
            {
                for (int k = 0; k < outI; k++)
                    boneWeights[baseIdx + k] /= sum;
            }
            else
            {
                boneIds[baseIdx] = 0;
                boneWeights[baseIdx] = 1f;
            }
        }
    }

    private static bool TryDecodeVertices(
        byte[] data, MdlMeshBuffer mb,
        out float[] positions, out float[] uvs, out float[]? normals)
    {
        return TryDecodeVertices(data, mb, out positions, out uvs, out normals,
            out _, out _, out _);
    }

    private static bool TryDecodeVertices(
        byte[] data, MdlMeshBuffer mb,
        out float[] positions, out float[] uvs, out float[]? normals,
        out float[]? tangents, out int[]? boneIds, out float[]? boneWeights)
    {
        positions = Array.Empty<float>();
        uvs = Array.Empty<float>();
        normals = null;
        tangents = null;
        boneIds = null;
        boneWeights = null;

        if (mb.IsFoliagePath)
        {
            return Engine_MdlFoliage.TryDecodeVertices(data, mb, out positions, out uvs, out normals);
        }

        if (mb.IsEngineRigid || mb.IsEngineSkinned || mb.IsEngineTree)
        {
            return TryDecodeEngineVertices(data, mb, rent: false,
                out positions, out uvs, out normals, out tangents,
                out boneIds, out boneWeights);
        }

        int stride = mb.IsAltPath ? 20 : VertexStride;
        int uvOffset = mb.IsAltPath ? 12 : UvOffset;
        if ((long)mb.VertexOffset + ((long)mb.VertexCount * stride) > data.Length)
        {
            return false;
        }

        positions = new float[mb.VertexCount * 3];
        uvs = new float[mb.VertexCount * 2];

        for (uint v = 0; v < mb.VertexCount; ++v)
        {
            int p = mb.VertexOffset + (int)(v * stride);
            positions[v * 3] = MdlReader.HalfToFloat((ushort)((data[p] << 8) | data[p + 1]));
            positions[(v * 3) + 1] = MdlReader.HalfToFloat((ushort)((data[p + 2] << 8) | data[p + 3]));
            positions[(v * 3) + 2] = MdlReader.HalfToFloat((ushort)((data[p + 4] << 8) | data[p + 5]));

            int u = p + uvOffset;
            uvs[v * 2] = MdlReader.HalfToFloat((ushort)((data[u] << 8) | data[u + 1]));
            uvs[(v * 2) + 1] = MdlReader.HalfToFloat((ushort)((data[u + 2] << 8) | data[u + 3]));
        }

        return true;
    }

    private static bool TryDecodeEngineVertices(
        byte[] data, MdlMeshBuffer mb, bool rent,
        out float[] positions, out float[] uvs, out float[] normals,
        out float[] tangents, out int[]? boneIds, out float[]? boneWeights)
    {
        // STRIDES AND OFFSETS ARE THE GUEST'S, read out of its vertex declaration builders
        // (sub_82A8B0E0 rigid, sub_82A750E8 skinned, sub_82A70510 tree) against the D3DDECLTYPE
        // table at 0x83316CC0. Tree is the rigid layout with three wind lanes appended —
        // POSITION1 at +20, COLOR1 at +28, COLOR2 at +32 — so its UV and tangent sit where the
        // rigid ones do and only the stride differs.
        bool skinned = mb.IsEngineSkinned;
        bool tree = mb.IsEngineTree;
        int stride = skinned ? 28 : tree ? 36 : RigidStride;
        int uvOff = skinned ? 20 : 12;
        int tangentOff = skinned ? 24 : 16;

        positions = Array.Empty<float>();
        uvs = Array.Empty<float>();
        normals = Array.Empty<float>();
        tangents = Array.Empty<float>();
        boneIds = null;
        boneWeights = null;

        if ((long)mb.VertexOffset + ((long)mb.VertexCount * stride) > data.Length)
        {
            return false;
        }

        uint vc = mb.VertexCount;
        positions = rent ? ArrayPool<float>.Shared.Rent((int)vc * 3) : new float[vc * 3];
        uvs = rent ? ArrayPool<float>.Shared.Rent((int)vc * 2) : new float[vc * 2];
        normals = rent ? ArrayPool<float>.Shared.Rent((int)vc * 3) : new float[vc * 3];
        tangents = rent ? ArrayPool<float>.Shared.Rent((int)vc * 4) : new float[vc * 4];

        if (skinned)
        {
            boneIds = rent ? ArrayPool<int>.Shared.Rent((int)vc * 4) : new int[vc * 4];
            boneWeights = rent ? ArrayPool<float>.Shared.Rent((int)vc * 4) : new float[vc * 4];
        }

        float[] snorm11 = MdlReader.Snorm11, snorm10 = MdlReader.Snorm10;
        for (uint v = 0; v < vc; v++)
        {
            int p = mb.VertexOffset + (int)(v * stride);

            positions[v * 3] = MdlReader.HalfToFloat((ushort)((data[p] << 8) | data[p + 1]));
            positions[v * 3 + 1] = MdlReader.HalfToFloat((ushort)((data[p + 2] << 8) | data[p + 3]));
            positions[v * 3 + 2] = MdlReader.HalfToFloat((ushort)((data[p + 4] << 8) | data[p + 5]));

            // MdlReader.DecodeNormal_11_11_10.
            uint packedNormal = MdlReader.ReadU32BE(data, p + 8);
            float nx = snorm11[packedNormal & 0x7FF];
            float ny = snorm11[(packedNormal >> 11) & 0x7FF];
            float nz = snorm10[packedNormal >> 22];
            float nLen = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
            if (nLen > 1e-6f) { nx /= nLen; ny /= nLen; nz /= nLen; }
            normals[v * 3] = nx;
            normals[v * 3 + 1] = ny;
            normals[v * 3 + 2] = nz;

            if (skinned)
            {
                boneIds![v * 4] = data[p + 12];
                boneIds[v * 4 + 1] = data[p + 13];
                boneIds[v * 4 + 2] = data[p + 14];
                boneIds[v * 4 + 3] = data[p + 15];

                // INDEX k PAIRS WITH WEIGHT k, IN MEMORY ORDER. BLENDINDICES is UBYTE4 and
                // BLENDWEIGHT D3DCOLOR, but the shipped data pairs them straight: every
                // single-influence vertex (12,045 across ch_herochild_male,
                // ch_heromale_morph_body and chickenblack, no counterexamples) holds its bone
                // at +15 and its 255 at +19. Rotating the weights pins those vertices to
                // bone 0, the `*_Rig_Asset` root the guest never poses.
                boneWeights![v * 4] = data[p + 16] / 255f;
                boneWeights[v * 4 + 1] = data[p + 17] / 255f;
                boneWeights[v * 4 + 2] = data[p + 18] / 255f;
                boneWeights[v * 4 + 3] = data[p + 19] / 255f;
            }

            int u = p + uvOff;
            uvs[v * 2] = MdlReader.HalfToFloat((ushort)((data[u] << 8) | data[u + 1]));
            uvs[v * 2 + 1] = MdlReader.HalfToFloat((ushort)((data[u + 2] << 8) | data[u + 3]));

            // MdlReader.DecodeTangent_DEC3N.
            uint packedTangent = MdlReader.ReadU32BE(data, p + tangentOff);
            float tx = snorm10[packedTangent & 0x3FF];
            float ty = snorm10[(packedTangent >> 10) & 0x3FF];
            float tz = snorm10[(packedTangent >> 20) & 0x3FF];
            float tw = (packedTangent >> 31) == 0 ? 1f : -1f;
            float tLen = MathF.Sqrt(tx * tx + ty * ty + tz * tz);
            if (tLen > 1e-6f) { tx /= tLen; ty /= tLen; tz /= tLen; }
            tangents[v * 4] = tx;
            tangents[v * 4 + 1] = ty;
            tangents[v * 4 + 2] = tz;
            tangents[v * 4 + 3] = tw;
        }

        return true;
    }

    /// <summary>An unsplit buffer's vertices, numbered as the file numbers them.</summary>
    private static int[] Identity(int start, int count)
    {
        var map = new int[count];
        for (int i = 0; i < count; i++)
        {
            map[i] = start + i;
        }

        return map;
    }

    private static void AddSubMeshSurfaces(
        List<MdlGeometry> geometry, IReadOnlyList<MdlMesh> meshes,
        IReadOnlyList<string> hideRegions, MdlMeshBuffer mb,
        float[] positions, float[] uvs, float[]? normals,
        float[]? tangents, int[]? boneIds, float[]? boneWeights, ReadOnlySpan<ushort> strip,
        int bufferBase, int clothRecord, bool lazyVertices)
    {
        // One of each for the whole buffer: `remap` is put back after each submesh by walking
        // `order`, the buffer's vertices the submesh reached, in the order it reached them.
        int vertexCount = (int)mb.VertexCount;
        uint[] remap = ArrayPool<uint>.Shared.Rent(vertexCount);
        uint[] order = ArrayPool<uint>.Shared.Rent(vertexCount);
        uint[] scratch = ArrayPool<uint>.Shared.Rent(strip.Length * 3);
        Array.Fill(remap, uint.MaxValue, 0, vertexCount);

        for (int si = 0; si < mb.SubMeshes.Count; ++si)
        {
            MdlSubMesh sub = mb.SubMeshes[si];

            uint start = sub.StartIndex;
            uint end = si + 1 < mb.SubMeshes.Count
                ? mb.SubMeshes[si + 1].StartIndex
                : (uint)strip.Length;

            if (start >= strip.Length || start >= end)
            {
                continue;
            }

            if (end > strip.Length)
            {
                end = (uint)strip.Length;
            }

            ReadOnlySpan<ushort> slice = strip.Slice((int)start, (int)(end - start));

            // Same rule as the single-section path above: the section says which it is.
            bool sliceHasRestart = slice.IndexOf((ushort)0xFFFF) >= 0;
            bool triangleList = sub.PrimitiveType == 0
                ? sub.FaceCount > 0 && slice.Length >= sub.FaceCount * 3
                : false;
            if (sub.PrimitiveType != 0 && !sliceHasRestart && sub.FaceCount > 0 &&
                slice.Length == sub.FaceCount * 3 && sub.FaceCount == 1)
            {
                // A one-triangle section is three indices either way.
                triangleList = true;
            }

            int indexCount;
            if (triangleList)
            {
                indexCount = slice.Length;
                for (int i = 0; i < indexCount; ++i)
                {
                    scratch[i] = slice[i];
                }
            }
            else
            {
                indexCount = MdlReader.BuildTrianglesFromStrip(slice, scratch);
                int cap = (int)(sub.FaceCount * 3);
                if (sub.FaceCount > 0 && indexCount > cap)
                {
                    indexCount = cap;
                }
            }

            if (indexCount < 3)
            {
                continue;
            }

            uint used = 0;
            for (int i = 0; i < indexCount; ++i)
            {
                uint idx = scratch[i];
                if (idx < mb.VertexCount && remap[idx] == uint.MaxValue)
                {
                    order[used] = idx;
                    remap[idx] = used++;
                }
            }

            if (used == 0)
            {
                continue;
            }

            var subSource = new int[used];
            for (uint nv = 0; nv < used; ++nv)
            {
                subSource[nv] = bufferBase + (int)order[nv];
            }

            // The piece's indices, in the submesh's own numbering.
            var subIndices = new uint[indexCount];
            for (int i = 0; i < indexCount; ++i)
            {
                uint old = scratch[i];
                subIndices[i] = old < mb.VertexCount && remap[old] != uint.MaxValue ? remap[old] : 0;
            }

            for (uint nv = 0; nv < used; ++nv)
            {
                remap[order[nv]] = uint.MaxValue;
            }

            if (lazyVertices)
            {
                geometry.Add(new MdlGeometry
                {
                    Indices = subIndices,
                    ClothRecord = clothRecord,
                    Region = RegionName(hideRegions, sub),
                    Material = ResolveMaterial(meshes, mb.MeshIndex, sub.MaterialIndex),
                    SourceVertices = subSource,
                    SharesLod0Vertices = mb.SharesLod0Vertices,
                    VerticesIn = mb,
                });
                continue;
            }

            var subPositions = new float[used * 3];
            var subUvs = new float[used * 2];
            float[]? subNormals = normals is null ? null : new float[used * 3];
            float[]? subTangents = tangents is not null ? new float[used * 4] : null;
            int[]? subBoneIds = boneIds is not null ? new int[used * 4] : null;
            float[]? subBoneWeights = boneWeights is not null ? new float[used * 4] : null;

            for (uint nv = 0; nv < used; ++nv)
            {
                uint v = order[nv];
                subPositions[nv * 3] = positions[v * 3];
                subPositions[(nv * 3) + 1] = positions[(v * 3) + 1];
                subPositions[(nv * 3) + 2] = positions[(v * 3) + 2];
                subUvs[nv * 2] = uvs[v * 2];
                subUvs[(nv * 2) + 1] = uvs[(v * 2) + 1];

                if (subNormals is not null && normals is not null)
                {
                    subNormals[nv * 3] = normals[v * 3];
                    subNormals[(nv * 3) + 1] = normals[(v * 3) + 1];
                    subNormals[(nv * 3) + 2] = normals[(v * 3) + 2];
                }

                if (subTangents is not null && tangents is not null)
                {
                    subTangents[nv * 4] = tangents[v * 4];
                    subTangents[nv * 4 + 1] = tangents[v * 4 + 1];
                    subTangents[nv * 4 + 2] = tangents[v * 4 + 2];
                    subTangents[nv * 4 + 3] = tangents[v * 4 + 3];
                }

                if (subBoneIds is not null && boneIds is not null &&
                    subBoneWeights is not null && boneWeights is not null)
                {
                    subBoneIds[nv * 4] = boneIds[v * 4];
                    subBoneIds[nv * 4 + 1] = boneIds[v * 4 + 1];
                    subBoneIds[nv * 4 + 2] = boneIds[v * 4 + 2];
                    subBoneIds[nv * 4 + 3] = boneIds[v * 4 + 3];
                    subBoneWeights[nv * 4] = boneWeights[v * 4];
                    subBoneWeights[nv * 4 + 1] = boneWeights[v * 4 + 1];
                    subBoneWeights[nv * 4 + 2] = boneWeights[v * 4 + 2];
                    subBoneWeights[nv * 4 + 3] = boneWeights[v * 4 + 3];
                }
            }

            geometry.Add(new MdlGeometry
            {
                Positions = subPositions,
                Uvs = subUvs,
                Indices = subIndices,
                Normals = subNormals ??
                          MdlReader.ComputeSmoothNormals((int)used, subIndices, subPositions),
                AuthoredNormals = subNormals is not null,
                Tangents = subTangents ?? Array.Empty<float>(),
                BoneIds = subBoneIds ?? Array.Empty<int>(),
                BoneWeights = subBoneWeights ?? Array.Empty<float>(),
                Name = ResolveName(meshes, mb.MeshIndex, si),
                ClothRecord = clothRecord,
                Region = RegionName(hideRegions, sub),
                Material = ResolveMaterial(meshes, mb.MeshIndex, sub.MaterialIndex),
                SourceVertices = subSource,
                SharesLod0Vertices = mb.SharesLod0Vertices,
            });
        }

        ArrayPool<uint>.Shared.Return(remap);
        ArrayPool<uint>.Shared.Return(order);
        ArrayPool<uint>.Shared.Return(scratch);
    }

    private static string RegionName(IReadOnlyList<string> hideRegions, MdlSubMesh sub) =>
        sub.RegionIndex < hideRegions.Count
            ? hideRegions[(int)sub.RegionIndex]
            : sub.RegionIndex == uint.MaxValue ? string.Empty : "#" + sub.RegionIndex;

    private static MdlMaterial? ResolveMaterial(
        IReadOnlyList<MdlMesh> meshes, uint meshIndex, int materialIndex)
    {
        if (meshIndex >= meshes.Count)
        {
            return null;
        }

        List<MdlMaterial> materials = meshes[(int)meshIndex].Materials;
        if (materials.Count == 0)
        {
            return null;
        }

        return materialIndex >= 0 && materialIndex < materials.Count
            ? materials[materialIndex]
            : materials[0];
    }

    private static string ResolveName(IReadOnlyList<MdlMesh> meshes, uint meshIndex, int subIndex)
    {
        string baseName = meshIndex < meshes.Count && meshes[(int)meshIndex].Name.Length != 0
            ? meshes[(int)meshIndex].Name
            : "mesh_" + meshIndex;

        return subIndex < 0 ? baseName : baseName + "_" + subIndex;
    }
}
