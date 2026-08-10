using System;
using System.Collections.Generic;

namespace Refii.Models;

internal static class Engine_MdlMerge
{
    internal static string MaterialKey(MdlMaterial? material) =>
        material is null
            ? "<none>"
            : string.Join(
                '|',
                material.Diffuse,
                material.Normal,
                material.Specular,
                material.Metallic,
                material.Extra,
                material.ShaderType.ToString());

    internal static List<MdlGeometry> ByMaterial(List<MdlGeometry> geometry)
    {
        if (geometry.Count < 2)
        {
            return geometry;
        }

        var order = new List<string>();
        var groups = new Dictionary<string, List<MdlGeometry>>();

        foreach (MdlGeometry geo in geometry)
        {
            if (geo.VertexCount == 0 || geo.TriangleCount == 0)
            {
                continue;
            }

            string key = string.Concat(
                MaterialKey(geo.Material),
                geo.HasTangents ? "|t" : "|-",
                geo.HasBones ? "|b" : "|-");

            if (!groups.TryGetValue(key, out List<MdlGeometry>? bucket))
            {
                bucket = new List<MdlGeometry>();
                groups[key] = bucket;
                order.Add(key);
            }

            bucket.Add(geo);
        }

        if (order.Count == geometry.Count)
        {
            return geometry;
        }

        var merged = new List<MdlGeometry>(order.Count);
        foreach (string key in order)
        {
            List<MdlGeometry> bucket = groups[key];
            merged.Add(bucket.Count == 1 ? bucket[0] : Concatenate(bucket));
        }

        return merged;
    }

    private static MdlGeometry Concatenate(List<MdlGeometry> parts)
    {
        int vertices = 0;
        int indices = 0;
        foreach (MdlGeometry part in parts)
        {
            vertices += part.VertexCount;
            indices += part.Indices.Length;
        }

        MdlGeometry first = parts[0];
        var result = new MdlGeometry
        {
            Name = first.Name,
            Material = first.Material,
            Positions = new float[vertices * 3],
            Normals = new float[vertices * 3],
            Uvs = new float[vertices * 2],
            Indices = new uint[indices],
            SourceVertices = new int[vertices],
        };

        if (first.HasTangents)
        {
            result.Tangents = new float[vertices * 4];
        }

        if (first.HasBones)
        {
            result.BoneIds = new int[vertices * 4];
            result.BoneWeights = new float[vertices * 4];
        }

        int vertexBase = 0;
        int indexCursor = 0;
        foreach (MdlGeometry part in parts)
        {
            int count = part.VertexCount;
            Array.Copy(part.Positions, 0, result.Positions, vertexBase * 3, count * 3);
            Array.Copy(part.Normals, 0, result.Normals, vertexBase * 3, count * 3);
            Array.Copy(part.Uvs, 0, result.Uvs, vertexBase * 2, count * 2);

            // Merging by material reorders vertices too, so the map has to travel with them or
            // a bake lands on the wrong surface entirely rather than merely being rejected.
            if (part.SourceVertices.Length == count)
            {
                Array.Copy(part.SourceVertices, 0, result.SourceVertices, vertexBase, count);
            }
            else
            {
                Array.Fill(result.SourceVertices, -1, vertexBase, count);
            }

            if (result.Tangents.Length != 0 && part.HasTangents)
            {
                Array.Copy(part.Tangents, 0, result.Tangents, vertexBase * 4, count * 4);
            }

            if (result.BoneIds.Length != 0 && part.HasBones)
            {
                Array.Copy(part.BoneIds, 0, result.BoneIds, vertexBase * 4, count * 4);
                Array.Copy(part.BoneWeights, 0, result.BoneWeights, vertexBase * 4, count * 4);
            }

            foreach (uint index in part.Indices)
            {
                result.Indices[indexCursor++] = (uint)(index + vertexBase);
            }

            vertexBase += count;
        }

        return result;
    }
}
