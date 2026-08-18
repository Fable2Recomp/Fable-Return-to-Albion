using System;
using System.Collections.Generic;
using Godot;
using Refii.Models;

namespace Refii.Hero;

public static class Engine_HeroBodyMorph
{
    public const int TransformStride = 11;

    public const int TranslationOffset = 4;

    public static bool Enabled { get; set; } = true;

    public static bool CanMorph(MdlInfo info) =>
        info.HasBoneTransforms &&
        info.Bones.Count != 0 &&
        info.BoneTransforms.Count == info.Bones.Count;

    public static ArrayMesh? Apply(
        byte[] modelBytes,
        ArrayMesh source,
        IReadOnlyDictionary<string, BoneMorphOffset> offsets)
    {
        if (!Enabled || offsets.Count == 0 ||
            !Engine_MdlData.TryParseInfo(modelBytes, out MdlInfo info, out _) ||
            !CanMorph(info))
        {
            return null;
        }

        Transform3D[]? deltas = BuildDeltas(info, offsets);
        if (deltas is null)
        {
            return null;
        }

        Basis[] normalDeltas = NormalDeltas(deltas);

        var result = new ArrayMesh();
        bool moved = false;

        for (int s = 0; s < source.GetSurfaceCount(); s++)
        {
            Godot.Collections.Array arrays = source.SurfaceGetArrays(s);

            Vector3[] positions = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            bool hasBones = arrays[(int)Mesh.ArrayType.Bones].VariantType ==
                Variant.Type.PackedInt32Array;
            bool hasWeights = arrays[(int)Mesh.ArrayType.Weights].VariantType ==
                Variant.Type.PackedFloat32Array;

            if (positions.Length != 0 && hasBones && hasWeights)
            {
                int[] bones = arrays[(int)Mesh.ArrayType.Bones].AsInt32Array();
                float[] weights = arrays[(int)Mesh.ArrayType.Weights].AsFloat32Array();

                Vector3[] normals =
                    arrays[(int)Mesh.ArrayType.Normal].VariantType ==
                        Variant.Type.PackedVector3Array
                        ? arrays[(int)Mesh.ArrayType.Normal].AsVector3Array()
                        : Array.Empty<Vector3>();

                if (Skin(positions, normals, bones, weights, deltas, normalDeltas))
                {
                    moved = true;
                    arrays[(int)Mesh.ArrayType.Vertex] = positions;
                    if (normals.Length != 0)
                    {
                        arrays[(int)Mesh.ArrayType.Normal] = normals;
                    }
                }
            }

            result.AddSurfaceFromArrays(source.SurfaceGetPrimitiveType(s), arrays);
            result.SurfaceSetName(s, source.SurfaceGetName(s));
            result.SurfaceSetMaterial(s, source.SurfaceGetMaterial(s));
        }

        if (!moved || result.GetSurfaceCount() == 0)
        {
            return null;
        }

        foreach (StringName meta in source.GetMetaList())
        {
            result.SetMeta(meta, source.GetMeta(meta));
        }

        return result;
    }

    private static bool Skin(
        Vector3[] positions,
        Vector3[] normals,
        int[] bones,
        float[] weights,
        Transform3D[] deltas,
        Basis[] normalDeltas)
    {
        int perVertex = bones.Length / Math.Max(1, positions.Length);
        if (perVertex <= 0 || weights.Length < positions.Length * perVertex)
        {
            return false;
        }

        bool moved = false;

        for (int v = 0; v < positions.Length; v++)
        {
            Vector3 accumulated = Vector3.Zero;
            Vector3 accumulatedNormal = Vector3.Zero;
            float total = 0.0f;

            for (int b = 0; b < perVertex; b++)
            {
                int slot = (v * perVertex) + b;
                float weight = weights[slot];
                if (weight <= 0.0f)
                {
                    continue;
                }

                int bone = bones[slot];
                if (bone < 0 || bone >= deltas.Length)
                {
                    continue;
                }

                accumulated += deltas[bone] * positions[v] * weight;
                if (normals.Length != 0)
                {
                    accumulatedNormal += normalDeltas[bone] * normals[v] * weight;
                }

                total += weight;
            }

            if (total <= 0.0f)
            {
                continue;
            }

            Vector3 morphed = accumulated / total;
            if (!morphed.IsEqualApprox(positions[v]))
            {
                moved = true;
            }

            positions[v] = morphed;

            if (normals.Length != 0 && accumulatedNormal.LengthSquared() > 1e-12f)
            {
                normals[v] = accumulatedNormal.Normalized();
            }
        }

        return moved;
    }

    public static readonly Transform3D ZUpToYUp = new(
        new Basis(
            new Vector3(1.0f, 0.0f, 0.0f),
            new Vector3(0.0f, 0.0f, -1.0f),
            new Vector3(0.0f, 1.0f, 0.0f)),
        Vector3.Zero);

    public const int MinimumTransformLength = 10;

    public const int ScaleOffset = 7;

    public static Transform3D BoneMatrix(float[] transform)
    {
        if (transform.Length < MinimumTransformLength)
        {
            return Transform3D.Identity;
        }

        var rotation = new Quaternion(
            transform[0], transform[1], transform[2], transform[3]);

        rotation = rotation.LengthSquared() > 1.0e-14f
            ? rotation.Normalized()
            : Quaternion.Identity;

        var scale = new Vector3(
            transform[ScaleOffset],
            transform[ScaleOffset + 1],
            transform[ScaleOffset + 2]);

        var translation = new Vector3(
            transform[TranslationOffset],
            transform[TranslationOffset + 1],
            transform[TranslationOffset + 2]);

        return new Transform3D(new Basis(rotation) * Basis.FromScale(scale), translation);
    }

    public static Transform3D[] WorldMatrices(MdlInfo info, Transform3D[] local)
    {
        int count = local.Length;
        var world = new Transform3D[count];
        var done = new bool[count];

        for (int i = 0; i < count; i++)
        {
            if (done[i])
            {
                continue;
            }

            var chain = new List<int>();
            int current = i;

            while (current >= 0 && current < count && !done[current])
            {
                chain.Add(current);
                current = info.Bones[current].ParentId;
            }

            Transform3D accumulated = current >= 0 && current < count
                ? world[current]
                : Transform3D.Identity;

            for (int c = chain.Count - 1; c >= 0; c--)
            {
                int bone = chain[c];
                accumulated *= local[bone];
                world[bone] = accumulated;
                done[bone] = true;
            }
        }

        return world;
    }

    private static Transform3D[]? BuildDeltas(
        MdlInfo info, IReadOnlyDictionary<string, BoneMorphOffset> offsets)
    {
        int count = info.Bones.Count;
        var restLocal = new Transform3D[count];
        var morphLocal = new Transform3D[count];
        var boneScale = new Vector3[count];
        var applied = new BoneMorphOffset?[count];
        bool any = false;

        for (int i = 0; i < count; i++)
        {
            float[] transform = info.BoneTransforms[i];
            if (transform.Length < MinimumTransformLength)
            {
                return null;
            }

            restLocal[i] = BoneMatrix(transform);

            offsets.TryGetValue(info.Bones[i].Name, out BoneMorphOffset? offset);
            applied[i] = offset;
            boneScale[i] = offset is null
                ? Vector3.One
                : new Vector3(offset.Scale[0], offset.Scale[1], offset.Scale[2]);

            if (offset is not null)
            {
                any = true;
            }
        }

        if (!any)
        {
            return null;
        }

        for (int i = 0; i < count; i++)
        {
            float[] transform = info.BoneTransforms[i];
            int parent = info.Bones[i].ParentId;

            bool parentScaled = parent >= 0 && parent < count && applied[parent] is not null;
            BoneMorphOffset? own = applied[i];

            if (own is null && !parentScaled)
            {
                morphLocal[i] = restLocal[i];
                continue;
            }

            var morphed = new float[transform.Length];
            System.Array.Copy(transform, morphed, transform.Length);

            for (int axis = 0; axis < 3; axis++)
            {
                float parentScale = parentScaled ? boneScale[parent][axis] : 1.0f;
                float position = own is null ? 0.0f : own.Position[axis];
                morphed[TranslationOffset + axis] =
                    (transform[TranslationOffset + axis] * parentScale) + position;
            }

            morphLocal[i] = BoneMatrix(morphed);
        }

        Transform3D[] restWorld = WorldMatrices(info, restLocal);
        Transform3D[] morphWorld = WorldMatrices(info, morphLocal);

        Transform3D toYUp = ZUpToYUp;
        Transform3D toZUp = toYUp.AffineInverse();

        var deltas = new Transform3D[count];
        for (int i = 0; i < count; i++)
        {
            var localScale = new Transform3D(Basis.FromScale(boneScale[i]), Vector3.Zero);
            Transform3D zUp = morphWorld[i] * localScale * restWorld[i].AffineInverse();
            deltas[i] = toYUp * zUp * toZUp;
        }

        return deltas;
    }

    public static Basis[] NormalDeltas(Transform3D[] deltas)
    {
        var normals = new Basis[deltas.Length];
        for (int i = 0; i < deltas.Length; i++)
        {
            normals[i] = deltas[i].Basis.Inverse().Transposed();
        }

        return normals;
    }
}
