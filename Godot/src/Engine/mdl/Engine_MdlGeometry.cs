using System;
using System.Collections.Generic;

namespace Refii.Models;

public sealed class MdlGeometry
{
    public string Name { get; set; } = string.Empty;

    public float[] Positions { get; set; } = Array.Empty<float>();

    public float[] Normals { get; set; } = Array.Empty<float>();

    public float[] Uvs { get; set; } = Array.Empty<float>();

    public uint[] Indices { get; set; } = Array.Empty<uint>();

    public MdlMaterial? Material { get; set; }

    public int VertexCount => Positions.Length / 3;

    public int TriangleCount => Indices.Length / 3;
}

public static class Engine_MdlGeometry
{
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

            if (!TryDecodeVertices(data, mb, out float[] positions, out float[] uvs, out float[]? normals))
            {
                continue;
            }

            ConvertZUpToYUp(positions);
            ConvertZUpToYUp(normals);

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
                AddSubMeshSurfaces(geometry, info, mb, positions, uvs, normals, strip);
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

    private static bool TryDecodeVertices(
        byte[] data, MdlMeshBuffer mb,
        out float[] positions, out float[] uvs, out float[]? normals)
    {
        if (mb.IsFoliagePath)
        {
            return Engine_MdlFoliage.TryDecodeVertices(data, mb, out positions, out uvs, out normals);
        }

        positions = Array.Empty<float>();
        uvs = Array.Empty<float>();
        normals = null;

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

    private static void AddSubMeshSurfaces(
        List<MdlGeometry> geometry, MdlInfo info, MdlMeshBuffer mb,
        float[] positions, float[] uvs, float[]? normals, ushort[] strip)
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
