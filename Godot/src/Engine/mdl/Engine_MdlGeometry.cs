using System;
using System.Collections.Generic;

namespace Refii.Models;

public sealed class MdlGeometry
{
    public string Name { get; set; } = string.Empty;

    public float[] Positions { get; set; } = Array.Empty<float>();

    public float[] Normals { get; set; } = Array.Empty<float>();

    public float[] Uvs { get; set; } = Array.Empty<float>();

    public float[] Tangents { get; set; } = Array.Empty<float>();

    public int[] BoneIds { get; set; } = Array.Empty<int>();

    public float[] BoneWeights { get; set; } = Array.Empty<float>();

    public uint[] Indices { get; set; } = Array.Empty<uint>();

    public MdlMaterial? Material { get; set; }

    public int VertexCount => Positions.Length / 3;

    public int TriangleCount => Indices.Length / 3;

    public bool HasTangents => Tangents.Length > 0;

    public bool HasBones => BoneIds.Length > 0;
}

public static class Engine_MdlGeometry
{
    // False will disabled smoothing the normals, looks odd in the editor :p
    public static bool smooth_normals = true;

    private const int VertexStride = 28;
    private const int UvOffset = 20;

    public static bool TryBuild(byte[] data, MdlInfo info, out List<MdlGeometry> geometry)
    {
        geometry = new List<MdlGeometry>();

        foreach (MdlMeshBuffer mb in info.MeshBuffers)
        {
            if (mb.VertexCount == 0 || mb.FaceCount == 0)
            {
                continue;
            }

            if ((long)mb.FaceOffset + ((long)mb.FaceCount * 2) > data.Length)
            {
                continue;
            }

            if (!TryDecodeVertices(data, mb,
                    out float[] positions, out float[] uvs, out float[]? normals,
                    out float[]? tangents, out int[]? boneIds, out float[]? boneWeights))
            {
                continue;
            }

            if (smooth_normals && (mb.IsEngineRigid || mb.IsEngineSkinned))
            {
                normals = null;
            }

            if (boneIds is not null && boneWeights is not null)
            {
                FilterBoneWeights(boneIds, boneWeights, (int)info.Bones.Count);
            }

            ConvertZUpToYUp(positions);
            ConvertZUpToYUp(normals);
            ConvertTangentsZUpToYUp(tangents);

            var strip = new ushort[mb.FaceCount];
            bool hasRestart = false;
            for (uint i = 0; i < mb.FaceCount; ++i)
            {
                int f = mb.FaceOffset + (int)(i * 2);
                ushort w = (ushort)((data[f] << 8) | data[f + 1]);
                strip[i] = w;
                if (w == 0xFFFF)
                {
                    hasRestart = true;
                }
            }

            if (mb.SubMeshes.Count > 1)
            {
                AddSubMeshSurfaces(geometry, info, mb, positions, uvs, normals,
                    tangents, boneIds, boneWeights, strip);
                continue;
            }

            var indices = new List<uint>();
            if (hasRestart)
            {
                MdlReader.BuildTrianglesFromStrip(strip, indices);
            }
            else
            {
                int triangles = strip.Length / 3;
                for (int t = 0; t < triangles; ++t)
                {
                    indices.Add(strip[t * 3]);
                    indices.Add(strip[(t * 3) + 1]);
                    indices.Add(strip[(t * 3) + 2]);
                }
            }

            if (indices.Count < 3)
            {
                continue;
            }

            uint maxIndex = 0;
            foreach (uint i in indices)
            {
                if (i > maxIndex) maxIndex = i;
            }

            if (maxIndex >= mb.VertexCount)
            {
                continue;
            }

            var geo = new MdlGeometry
            {
                Positions = positions,
                Uvs = uvs,
                Indices = indices.ToArray(),
                Normals = normals ??
                          MdlReader.ComputeSmoothNormals((int)mb.VertexCount, indices, positions),
                Tangents = tangents ?? Array.Empty<float>(),
                BoneIds = boneIds ?? Array.Empty<int>(),
                BoneWeights = boneWeights ?? Array.Empty<float>(),
                Name = ResolveName(info, mb.MeshIndex, -1),
                Material = ResolveMaterial(info, mb.MeshIndex, 0),
            };

            geometry.Add(geo);
        }

        return geometry.Count > 0;
    }

    private static void ConvertZUpToYUp(float[]? values)
    {
        if (values is null)
        {
            return;
        }

        for (int i = 0; i + 2 < values.Length; i += 3)
        {
            float y = values[i + 1];
            values[i + 1] = values[i + 2];
            values[i + 2] = -y;
        }
    }

    private static void ConvertTangentsZUpToYUp(float[]? tangents)
    {
        if (tangents is null)
        {
            return;
        }

        for (int i = 0; i + 3 < tangents.Length; i += 4)
        {
            float y = tangents[i + 1];
            tangents[i + 1] = tangents[i + 2];
            tangents[i + 2] = -y;
        }
    }

    private static void FilterBoneWeights(int[] boneIds, float[] boneWeights, int boneCount)
    {
        int vertexCount = boneIds.Length / 4;
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

        if (mb.IsEngineRigid || mb.IsEngineSkinned)
        {
            return TryDecodeEngineVertices(data, mb,
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
        byte[] data, MdlMeshBuffer mb,
        out float[] positions, out float[] uvs, out float[] normals,
        out float[] tangents, out int[]? boneIds, out float[]? boneWeights)
    {
        bool skinned = mb.IsEngineSkinned;
        int stride = skinned ? 28 : 20;
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
        positions = new float[vc * 3];
        uvs = new float[vc * 2];
        normals = new float[vc * 3];
        tangents = new float[vc * 4];

        if (skinned)
        {
            boneIds = new int[vc * 4];
            boneWeights = new float[vc * 4];
        }

        for (uint v = 0; v < vc; v++)
        {
            int p = mb.VertexOffset + (int)(v * stride);

            positions[v * 3] = MdlReader.HalfToFloat((ushort)((data[p] << 8) | data[p + 1]));
            positions[v * 3 + 1] = MdlReader.HalfToFloat((ushort)((data[p + 2] << 8) | data[p + 3]));
            positions[v * 3 + 2] = MdlReader.HalfToFloat((ushort)((data[p + 4] << 8) | data[p + 5]));

            uint packedNormal = MdlReader.ReadU32BE(data, p + 8);
            var (nx, ny, nz) = MdlReader.DecodeNormal_11_11_10(packedNormal);
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

                float w0 = data[p + 16] / 255f;
                float w1 = data[p + 17] / 255f;
                float w2 = data[p + 18] / 255f;
                float w3 = data[p + 19] / 255f;
                boneWeights![v * 4] = w0;
                boneWeights[v * 4 + 1] = w1;
                boneWeights[v * 4 + 2] = w2;
                boneWeights[v * 4 + 3] = w3;
            }

            int u = p + uvOff;
            uvs[v * 2] = MdlReader.HalfToFloat((ushort)((data[u] << 8) | data[u + 1]));
            uvs[v * 2 + 1] = MdlReader.HalfToFloat((ushort)((data[u + 2] << 8) | data[u + 3]));

            uint packedTangent = MdlReader.ReadU32BE(data, p + tangentOff);
            var (tx, ty, tz, tw) = MdlReader.DecodeTangent_DEC3N(packedTangent);
            float tLen = MathF.Sqrt(tx * tx + ty * ty + tz * tz);
            if (tLen > 1e-6f) { tx /= tLen; ty /= tLen; tz /= tLen; }
            tangents[v * 4] = tx;
            tangents[v * 4 + 1] = ty;
            tangents[v * 4 + 2] = tz;
            tangents[v * 4 + 3] = tw;
        }

        return true;
    }

    private static void AddSubMeshSurfaces(
        List<MdlGeometry> geometry, MdlInfo info, MdlMeshBuffer mb,
        float[] positions, float[] uvs, float[]? normals,
        float[]? tangents, int[]? boneIds, float[]? boneWeights, ushort[] strip)
    {
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

            var slice = new ushort[end - start];
            Array.Copy(strip, start, slice, 0, slice.Length);

            bool sliceHasRestart = Array.IndexOf(slice, (ushort)0xFFFF) >= 0;
            bool triangleList = !sliceHasRestart && sub.FaceCount > 0 &&
                                slice.Length == sub.FaceCount * 3;

            var subIndices = new List<uint>();
            if (triangleList)
            {
                foreach (ushort w in slice)
                {
                    subIndices.Add(w);
                }
            }
            else
            {
                MdlReader.BuildTrianglesFromStrip(slice, subIndices);
                int cap = (int)(sub.FaceCount * 3);
                if (sub.FaceCount > 0 && subIndices.Count > cap)
                {
                    subIndices.RemoveRange(cap, subIndices.Count - cap);
                }
            }

            if (subIndices.Count < 3)
            {
                continue;
            }

            var remap = new uint[mb.VertexCount];
            Array.Fill(remap, uint.MaxValue);
            uint used = 0;
            foreach (uint idx in subIndices)
            {
                if (idx < mb.VertexCount && remap[idx] == uint.MaxValue)
                {
                    remap[idx] = used++;
                }
            }

            if (used == 0)
            {
                continue;
            }

            var subPositions = new float[used * 3];
            var subUvs = new float[used * 2];
            float[]? subNormals = normals is null ? null : new float[used * 3];
            float[]? subTangents = tangents is not null ? new float[used * 4] : null;
            int[]? subBoneIds = boneIds is not null ? new int[used * 4] : null;
            float[]? subBoneWeights = boneWeights is not null ? new float[used * 4] : null;

            for (uint v = 0; v < mb.VertexCount; ++v)
            {
                uint nv = remap[v];
                if (nv == uint.MaxValue)
                {
                    continue;
                }

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

            for (int i = 0; i < subIndices.Count; ++i)
            {
                uint old = subIndices[i];
                subIndices[i] = old < mb.VertexCount && remap[old] != uint.MaxValue ? remap[old] : 0;
            }

            geometry.Add(new MdlGeometry
            {
                Positions = subPositions,
                Uvs = subUvs,
                Indices = subIndices.ToArray(),
                Normals = subNormals ??
                          MdlReader.ComputeSmoothNormals((int)used, subIndices, subPositions),
                Tangents = subTangents ?? Array.Empty<float>(),
                BoneIds = subBoneIds ?? Array.Empty<int>(),
                BoneWeights = subBoneWeights ?? Array.Empty<float>(),
                Name = ResolveName(info, mb.MeshIndex, si),
                Material = ResolveMaterial(info, mb.MeshIndex, sub.MaterialIndex),
            });
        }
    }

    private static MdlMaterial? ResolveMaterial(MdlInfo info, uint meshIndex, int materialIndex)
    {
        if (meshIndex >= info.Meshes.Count)
        {
            return null;
        }

        List<MdlMaterial> materials = info.Meshes[(int)meshIndex].Materials;
        if (materials.Count == 0)
        {
            return null;
        }

        return materialIndex >= 0 && materialIndex < materials.Count
            ? materials[materialIndex]
            : materials[0];
    }

    private static string ResolveName(MdlInfo info, uint meshIndex, int subIndex)
    {
        string baseName = meshIndex < info.Meshes.Count && info.Meshes[(int)meshIndex].Name.Length != 0
            ? info.Meshes[(int)meshIndex].Name
            : "mesh_" + meshIndex;

        return subIndex < 0 ? baseName : baseName + "_" + subIndex;
    }
}
