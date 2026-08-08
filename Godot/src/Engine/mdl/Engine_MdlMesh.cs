using System.Collections.Generic;
using Godot;

namespace Refii.Models;

public static class Engine_MdlMesh
{
    public static ArrayMesh? TryCreateMesh(byte[] data, out string error) =>
        TryCreateMesh(data, string.Empty, out error);

    public static ArrayMesh? TryCreateMesh(byte[] data, string virtualPath, out string error) =>
        TryCreateMesh(data, virtualPath, out error, out _);

    public static ArrayMesh? TryCreateMesh(
        byte[] data, string virtualPath, out string error, out bool isStub)
    {
        isStub = false;

        if (!Engine_MdlData.TryParseInfo(data, virtualPath, out MdlInfo info, out error))
        {
            return null;
        }

        if (info.IsStub)
        {
            isStub = true;
            error = "header-only stub, no geometry";
            return null;
        }

        if (!Engine_MdlGeometry.TryBuild(data, info, out List<MdlGeometry> geometry))
        {
            error = "no decodable geometry";
            return null;
        }

        var mesh = new ArrayMesh();
        int added = 0;

        foreach (MdlGeometry geo in geometry)
        {
            if (geo.VertexCount == 0 || geo.TriangleCount == 0)
            {
                continue;
            }

            var vertices = new Vector3[geo.VertexCount];
            var normals = new Vector3[geo.VertexCount];
            var uvs = new Vector2[geo.VertexCount];

            for (int v = 0; v < geo.VertexCount; ++v)
            {
                vertices[v] = new Vector3(
                    geo.Positions[v * 3], geo.Positions[v * 3 + 1], geo.Positions[v * 3 + 2]);
                normals[v] = new Vector3(
                    geo.Normals[v * 3], geo.Normals[v * 3 + 1], geo.Normals[v * 3 + 2]);
                uvs[v] = new Vector2(geo.Uvs[v * 2], geo.Uvs[v * 2 + 1]);
            }

            var indices = new int[geo.Indices.Length];
            for (int i = 0; i < geo.Indices.Length; ++i)
            {
                indices[i] = (int)geo.Indices[i];
            }

            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = vertices;
            arrays[(int)Mesh.ArrayType.Normal] = normals;
            arrays[(int)Mesh.ArrayType.TexUV] = uvs;
            arrays[(int)Mesh.ArrayType.Index] = indices;

            if (geo.HasTangents)
            {
                var tangents = new float[geo.VertexCount * 4];
                System.Array.Copy(geo.Tangents, tangents, tangents.Length);
                arrays[(int)Mesh.ArrayType.Tangent] = tangents;
            }

            if (geo.HasBones)
            {
                var bones = new int[geo.VertexCount * 4];
                System.Array.Copy(geo.BoneIds, bones, bones.Length);
                arrays[(int)Mesh.ArrayType.Bones] = bones;

                var weights = new float[geo.VertexCount * 4];
                System.Array.Copy(geo.BoneWeights, weights, weights.Length);
                arrays[(int)Mesh.ArrayType.Weights] = weights;
            }

            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            if (geo.Name.Length != 0)
            {
                mesh.SurfaceSetName(added, geo.Name);
            }

            StandardMaterial3D? material = Engine_MdlMaterial.TryCreate(geo.Material);
            if (material is not null)
            {
                mesh.SurfaceSetMaterial(added, material);
            }

            ++added;
        }

        if (added == 0)
        {
            error = "no surfaces produced";
            return null;
        }

        error = string.Empty;
        return mesh;
    }
}
