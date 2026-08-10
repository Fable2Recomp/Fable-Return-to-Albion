using System.Collections.Generic;
using Godot;

namespace Refii.Models;

public static class Engine_MdlMesh
{
    /// <summary>
    /// Metadata key: for each vertex of the mesh, surface by surface in order, which vertex of
    /// the MODEL's own buffers it came from. -1 where it is not known.
    /// </summary>
    public const string SourceVertexMapMeta = "mdl_source_vertices";

    /// <summary>
    /// Metadata key: how many vertices the model's buffers hold in total, which is the count
    /// anything indexed against the file — a per-vertex bake — will have been built for.
    /// </summary>
    public const string SourceVertexTotalMeta = "mdl_source_vertex_total";

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

        // WHICH kind of "nothing", quoting the header's own numbers.
        //
        // A stub is not a parse failure: the header is a complete record that states how many
        // bytes of LOD payload follow it, and when the banks carry no body entry for the name
        // there is nothing for any reader to decode. Print the shortfall so the next reader can
        // tell a hole in the shipped data from a decoder that gave up.
        if (info.IsStub)
        {
            isStub = true;
            error = info.BodyBytesStated > 0
                ? $"no body entry in any bank: the {Engine_MdlEngine.BodyStart}-byte header " +
                  $"states {info.BodyBytesStated:N0} byte(s) of geometry across " +
                  $"{info.LodCount} LOD(s)"
                : "header only, no geometry";
            return null;
        }

        if (!Engine_MdlGeometry.TryBuild(data, info, out List<MdlGeometry> geometry))
        {
            // A model that declares no meshes was read correctly and holds nothing. Three
            // shipped models are like this — an all-but-empty 133-byte LOD 0 — and counting
            // them as undecodable pointed at the reader instead of at the data.
            isStub = info.MeshCount == 0;
            error = info.MeshCount == 0
                ? "the model declares no meshes"
                : "no decodable geometry";
            return null;
        }

        var mesh = new ArrayMesh();
        int added = 0;

        // The model's own vertex numbering, surface by surface in the order they are added, so
        // anything the file states per vertex can still be looked up after splitting and
        // merging have moved everything. Carried as resource metadata because an ArrayMesh has
        // nowhere else to put a per-surface fact, and because the streamer needs it long after
        // this function has returned.
        var sourceVertices = new List<int>();
        int sourceVertexTotal = 0;
        foreach (MdlMeshBuffer buffer in info.MeshBuffers)
        {
            sourceVertexTotal += (int)buffer.VertexCount;
        }

        foreach (MdlGeometry geo in Engine_MdlMerge.ByMaterial(geometry))
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

            // Winding reversed per triangle: the guest's front faces are the other way round.
            //
            // The tail is copied rather than left behind. A count that is not a multiple of
            // three is malformed for a triangle list, but striding past it leaves those indices
            // at the array's default of ZERO — degenerate triangles all anchored on vertex 0,
            // which is a silent corruption rather than a complaint.
            var indices = new int[geo.Indices.Length];
            int triangles = geo.Indices.Length - (geo.Indices.Length % 3);
            for (int i = 0; i < triangles; i += 3)
            {
                indices[i] = (int)geo.Indices[i];
                indices[i + 1] = (int)geo.Indices[i + 2];
                indices[i + 2] = (int)geo.Indices[i + 1];
            }

            for (int i = triangles; i < geo.Indices.Length; ++i)
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

            if (geo.SourceVertices.Length == geo.VertexCount)
            {
                sourceVertices.AddRange(geo.SourceVertices);
            }
            else
            {
                for (int v = 0; v < geo.VertexCount; v++)
                {
                    sourceVertices.Add(-1);
                }
            }

            Material? material =
                Engine_MdlMaterial.TryCreate(geo.Material) ?? Engine_MdlMaterial.Fallback();
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

        mesh.SetMeta(SourceVertexMapMeta, sourceVertices.ToArray());
        mesh.SetMeta(SourceVertexTotalMeta, sourceVertexTotal);

        // THE SKELETON THE BONE INDICES REFER TO, carried on the mesh.
        //
        // A skinned mesh's BONES array indexes THIS model's bone table, and the live pose comes
        // from the guest, which numbers its own. Nothing downstream could check that those two
        // agree because the table was parsed and then thrown away — so a rig built in the wrong
        // order attaches every vertex to the wrong bone and looks like a broken character rather
        // than like a mismatched index space.
        if (info.Bones.Count != 0)
        {
            var parents = new int[info.Bones.Count];
            var names = new string[info.Bones.Count];
            for (int i = 0; i < info.Bones.Count; i++)
            {
                parents[i] = info.Bones[i].ParentId;
                names[i] = info.Bones[i].Name;
            }

            mesh.SetMeta(BoneParentsMeta, parents);
            mesh.SetMeta(BoneNamesMeta, names);

            // AND THE MODEL'S OWN REST POSE, which the reader has always parsed and nothing has
            // ever read.
            //
            // It is the missing half of binding a skin. The bone table alone says what the
            // indices MEAN; this says where those bones are, so a skeleton can be built in the
            // model's numbering without asking the guest for anything. That matters because the
            // guest numbers its rig differently — 161 bones against this model's 143 for the
            // child hero — and a skeleton built in the guest's order binds every vertex to the
            // wrong bone.
            //
            // Flat, stride 11, exactly as the file stores it: quaternion xyzw at 0, translation
            // at 4, scale at 7, one pad float at 10. Reshaping it here would be a second layout
            // to keep in agreement with the reader, and the reader is the one that matches the
            // 44-byte record on disk.
            if (info.HasBoneTransforms && info.BoneTransforms.Count == info.Bones.Count)
            {
                var rest = new float[info.Bones.Count * BoneRestStride];
                for (int i = 0; i < info.Bones.Count; i++)
                {
                    System.Array.Copy(
                        info.BoneTransforms[i], 0, rest, i * BoneRestStride, BoneRestStride);
                }

                mesh.SetMeta(BoneRestMeta, rest);
            }
        }

        error = string.Empty;
        return mesh;
    }

    /// <summary>Metadata key: the model's bone parent indices, in the order BONES indexes.</summary>
    public const string BoneParentsMeta = "bone_parents";

    /// <summary>Metadata key: the model's bone names, same order.</summary>
    public const string BoneNamesMeta = "bone_names";

    /// <summary>
    /// Metadata key: the model's LOCAL rest transforms, same order, <see cref="BoneRestStride"/>
    /// floats each. Absent when the model ships a bone table with no transforms to go with it.
    /// </summary>
    public const string BoneRestMeta = "bone_rest";

    /// <summary>
    /// Floats per bone in <see cref="BoneRestMeta"/>: quaternion xyzw, translation, scale, pad.
    /// </summary>
    public const int BoneRestStride = 11;
}
