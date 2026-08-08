using System;
using System.Collections.Generic;

namespace Refii.Models;

/// <summary>
/// Collapses the submesh chunking a .mdl carries for the Xbox 360 back into one surface
/// per material.
///
/// A .mdl splits its geometry into many small chunks — <c>WC_Crucible_Room_Large</c> has
/// 481 of them across just 10 materials. The split is an index-buffer artefact, not a
/// material boundary, and the original engine treated it that way: its draw loop
/// (<c>Render_DrawIndexedMesh</c>, 0x82b27728) walks the 64-byte submesh records issuing
/// one DrawIndexedVertices each, but only rebinds the material when the submesh's
/// material index DIFFERS from the previous one. That redundant-bind check is a plain
/// "not equal to last", with no lookup behind it, which only works because consecutive
/// submeshes sharing a material are already adjacent. Merging them is therefore not an
/// optimisation layered on top of the format — it is the grouping the format already
/// assumed.
///
/// Without this, four room models silently LOSE geometry: Godot's ArrayMesh stops
/// accepting surfaces at 256, so surface 257 onward is dropped with an error per surface
/// and a mesh that still reports success. Holes in a dungeon, no failed load to explain
/// them.
///
/// Chunks are merged only when their vertex FORMAT also matches. Godot fixes the array
/// layout per surface, so a skinned chunk and an unskinned one cannot share a surface
/// even if they share a material.
/// </summary>
internal static class Engine_MdlMerge
{
    /// <summary>
    /// Identity of a material for merge purposes: the texture set plus the shader. Two
    /// chunks with the same key produce the same <see cref="Godot.StandardMaterial3D"/>,
    /// so they draw identically and can share a surface.
    ///
    /// This deliberately keys on the RESOLVED texture set rather than the material index
    /// the file stores. Chunks carrying different indices into the material table that
    /// nonetheless resolve to identical textures and shader still merge, and — more
    /// importantly — chunks are never merged on an index match alone if the resolved
    /// materials somehow differ.
    /// </summary>
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

    /// <summary>
    /// Returns geometry grouped to one entry per (material, vertex format), preserving
    /// the order in which each group first appears.
    ///
    /// The input list is returned UNCHANGED when nothing can merge, so the overwhelming
    /// majority of models — which have a handful of chunks and no duplicates — pay a
    /// dictionary walk and no copying.
    /// </summary>
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

            if (result.Tangents.Length != 0 && part.HasTangents)
            {
                Array.Copy(part.Tangents, 0, result.Tangents, vertexBase * 4, count * 4);
            }

            if (result.BoneIds.Length != 0 && part.HasBones)
            {
                Array.Copy(part.BoneIds, 0, result.BoneIds, vertexBase * 4, count * 4);
                Array.Copy(part.BoneWeights, 0, result.BoneWeights, vertexBase * 4, count * 4);
            }

            // Every chunk indexes from zero into its own vertex range, so concatenating
            // the vertices makes every index after the first chunk point at the wrong one.
            foreach (uint index in part.Indices)
            {
                result.Indices[indexCursor++] = (uint)(index + vertexBase);
            }

            vertexBase += count;
        }

        return result;
    }
}
