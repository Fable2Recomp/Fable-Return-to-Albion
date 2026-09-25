using System.Collections.Generic;
using Godot;
using Refii.Models;

namespace Refii.Animations;

public static class Engine_AnimSkeleton
{
    public const string SkeletonName = "Skeleton3D";

    public static readonly Basis ZUpToYUp = new(
        new Vector3(1.0f, 0.0f, 0.0f),
        new Vector3(0.0f, 0.0f, -1.0f),
        new Vector3(0.0f, 1.0f, 0.0f));

    public static readonly Quaternion ZUpToYUpRotation = ZUpToYUp.GetRotationQuaternion();

    public static Quaternion ToYUp(Quaternion rotation)
    {
        return ZUpToYUpRotation * rotation * ZUpToYUpRotation.Inverse();
    }

    public static Vector3 ToYUp(Vector3 position)
    {
        return ZUpToYUp * position;
    }

    public static Skeleton3D? Build(MdlInfo info)
    {
        if (info is null || info.Bones.Count == 0)
        {
            return null;
        }

        var names = new string[info.Bones.Count];
        var parents = new int[info.Bones.Count];

        for (int i = 0; i < info.Bones.Count; i++)
        {
            names[i] = info.Bones[i].Name;
            parents[i] = info.Bones[i].ParentId;
        }

        Skeleton3D skeleton = Build(names, parents);

        if (!info.HasBoneTransforms)
        {
            return skeleton;
        }

        for (int i = 0; i < info.Bones.Count && i < info.BoneTransforms.Count; i++)
        {
            ApplyRest(skeleton, i, info.BoneTransforms[i]);
        }

        return skeleton;
    }

    public static Skeleton3D? Build(Engine_MdlRig rig)
    {
        if (rig.BoneCount == 0)
        {
            return null;
        }

        Skeleton3D skeleton = Build(rig.Names, rig.Parents);
        ApplyRests(skeleton, rig);
        return skeleton;
    }

    public static void ApplyRests(Skeleton3D skeleton, Engine_MdlRig rig)
    {
        var transform = new float[Engine_MdlMesh.BoneRestStride];
        int count = System.Math.Min(rig.BoneCount, skeleton.GetBoneCount());

        for (int i = 0; i < count; i++)
        {
            System.Array.Copy(
                rig.Rest, i * Engine_MdlMesh.BoneRestStride, transform, 0, transform.Length);

            ApplyRest(skeleton, i, transform);
        }
    }

    private static Skeleton3D Build(IReadOnlyList<string> names, IReadOnlyList<int> parents)
    {
        // Manual modifier processing, for the reason Engine_EntitySkeleton.Build gives.
        var skeleton = new Skeleton3D
        {
            Name = SkeletonName,
            ModifierCallbackModeProcess = Skeleton3D.ModifierCallbackModeProcessEnum.Manual,
        };

        for (int i = 0; i < names.Count; i++)
        {
            skeleton.AddBone(names[i].Length != 0 ? names[i] : $"bone_{i}");
        }

        for (int i = 0; i < names.Count; i++)
        {
            int parent = parents[i];
            if (parent >= 0 && parent < names.Count && parent != i)
            {
                skeleton.SetBoneParent(i, parent);
            }
        }

        return skeleton;
    }

    private static void ApplyRest(Skeleton3D skeleton, int bone, float[] transform)
    {
        if (!TryBuildRest(transform, out Transform3D rest))
        {
            return;
        }

        skeleton.SetBoneRest(bone, rest);
        skeleton.SetBonePosePosition(bone, rest.Origin);
        skeleton.SetBonePoseRotation(bone, rest.Basis.GetRotationQuaternion());
        skeleton.SetBonePoseScale(bone, rest.Basis.Scale);
    }

    private static bool TryBuildRest(float[] transform, out Transform3D rest)
    {
        rest = Transform3D.Identity;

        if (transform is null || transform.Length < 10)
        {
            return false;
        }

        float x = transform[0];
        float y = transform[1];
        float z = transform[2];
        float w = transform[3];

        float length = Mathf.Sqrt((x * x) + (y * y) + (z * z) + (w * w));
        if (length <= 1e-7f)
        {
            x = 0.0f;
            y = 0.0f;
            z = 0.0f;
            w = 1.0f;
        }
        else
        {
            float inverse = 1.0f / length;
            x *= inverse;
            y *= inverse;
            z *= inverse;
            w *= inverse;
        }

        float sx = SafeScale(transform[7]);
        float sy = SafeScale(transform[8]);
        float sz = SafeScale(transform[9]);

        var column0 = new Vector3(
            (1.0f - (2.0f * ((y * y) + (z * z)))) * sx,
            (2.0f * ((x * y) + (z * w))) * sx,
            (2.0f * ((x * z) - (y * w))) * sx);

        var column1 = new Vector3(
            (2.0f * ((x * y) - (z * w))) * sy,
            (1.0f - (2.0f * ((x * x) + (z * z)))) * sy,
            (2.0f * ((y * z) + (x * w))) * sy);

        var column2 = new Vector3(
            (2.0f * ((x * z) + (y * w))) * sz,
            (2.0f * ((y * z) - (x * w))) * sz,
            (1.0f - (2.0f * ((x * x) + (y * y)))) * sz);

        var basis = new Basis(column0, column1, column2);
        var origin = new Vector3(transform[4], transform[5], transform[6]);

        rest = new Transform3D(
            ZUpToYUp * basis * ZUpToYUp.Inverse(),
            ZUpToYUp * origin);

        return true;
    }

    private static float SafeScale(float value)
    {
        return float.IsFinite(value) && Mathf.Abs(value) > 1e-6f ? value : 1.0f;
    }

    public static List<string> BoneNames(Skeleton3D skeleton)
    {
        var names = new List<string>(skeleton.GetBoneCount());
        for (int i = 0; i < skeleton.GetBoneCount(); i++)
        {
            names.Add(skeleton.GetBoneName(i));
        }

        return names;
    }
}
