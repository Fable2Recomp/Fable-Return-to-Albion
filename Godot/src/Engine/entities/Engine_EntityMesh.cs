using System;
using System.Collections.Generic;
using Godot;
using Refii.Models;

namespace Refii.Entities;

public enum EyeSide
{
    None,
    Left,
    Right,
}

public static class Engine_EntityMesh
{
    private static readonly string[] LeftBoneCandidates =
    {
        "L_Eye", "Left_Eye", "LeftEye", "Character.Focal.Eye.Left",
        "Focal_Eye_DummyObject1",
    };

    private static readonly string[] RightBoneCandidates =
    {
        "R_Eye", "Right_Eye", "RightEye", "Character.Focal.Eye.Right",
        "Focal_Eye_DummyObject",
    };

    public static EyeSide EyeSideOf(string modelPath)
    {
        string leaf = modelPath.Replace('\\', '/').ToLowerInvariant();

        int slash = leaf.LastIndexOf('/');
        if (slash >= 0)
        {
            leaf = leaf[(slash + 1)..];
        }

        int dot = leaf.LastIndexOf('.');
        if (dot >= 0)
        {
            leaf = leaf[..dot];
        }

        if (!leaf.Contains("eye", StringComparison.Ordinal))
        {
            return EyeSide.None;
        }

        if (leaf.Contains("left", StringComparison.Ordinal) ||
            leaf.EndsWith("_l", StringComparison.Ordinal))
        {
            return EyeSide.Left;
        }

        if (leaf.Contains("right", StringComparison.Ordinal) ||
            leaf.EndsWith("_r", StringComparison.Ordinal))
        {
            return EyeSide.Right;
        }

        return EyeSide.None;
    }

    public static bool TryEyeOffset(
        IReadOnlyList<string> modelPaths, EyeSide side, out Vector3 offset)
    {
        offset = Vector3.Zero;
        if (side == EyeSide.None)
        {
            return false;
        }

        List<MdlInfo> rigs = LoadRigs(modelPaths);
        return TryEyeOffset(rigs, side, out offset, out _);
    }

    public static ArrayMesh? ResolveModel(string modelPath)
    {
        byte[]? bytes = Engine_BnkMeshLoader.ReadBytes(modelPath);
        return bytes is null
            ? null
            : Engine_MdlMesh.TryCreateMesh(bytes, modelPath, out _, out _);
    }

    public static void Append(
        ArrayMesh target,
        IReadOnlyList<string> modelPaths,
        Func<string, ArrayMesh?> resolve,
        bool includeEyes = true)
    {
        List<MdlInfo>? rigs = null;
        var offsets = new Dictionary<EyeSide, Vector3>();
        var eyeBones = new Dictionary<EyeSide, int>();

        foreach (string path in modelPaths)
        {
            EyeSide side = EyeSideOf(path);
            if (side != EyeSide.None && !includeEyes)
            {
                continue;
            }

            ArrayMesh? part = resolve(path);
            if (part is null)
            {
                continue;
            }

            Vector3 offset = Vector3.Zero;
            int anchor = 0;

            if (side != EyeSide.None)
            {
                rigs ??= LoadRigs(modelPaths);

                if (!offsets.TryGetValue(side, out offset))
                {
                    if (!TryEyeOffset(rigs, side, out offset, out int bone) && rigs.Count != 0)
                    {
                        continue;
                    }

                    offsets[side] = offset;
                    eyeBones[side] = bone < 0 ? 0 : bone;
                }

                anchor = eyeBones.TryGetValue(side, out int found) ? found : 0;
            }

            AppendSurfaces(target, part, offset, anchor);
        }
    }

    private static void AppendSurfaces(
        ArrayMesh target, ArrayMesh part, Vector3 offset, int anchorBone)
    {
        for (int surface = 0; surface < part.GetSurfaceCount(); surface++)
        {
            Godot.Collections.Array arrays = part.SurfaceGetArrays(surface);

            int vertexCount = 0;
            if (arrays[(int)Mesh.ArrayType.Vertex].VariantType ==
                Variant.Type.PackedVector3Array)
            {
                Vector3[] vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                vertexCount = vertices.Length;

                if (offset != Vector3.Zero)
                {
                    for (int v = 0; v < vertices.Length; v++)
                    {
                        vertices[v] += offset;
                    }

                    arrays[(int)Mesh.ArrayType.Vertex] = vertices;
                }
            }

            AnchorUnskinned(arrays, vertexCount, anchorBone);

            target.AddSurfaceFromArrays(part.SurfaceGetPrimitiveType(surface), arrays, null,
                Refii.Models.Engine_MdlMesh.SurfaceLods(part, surface));

            int added = target.GetSurfaceCount() - 1;
            target.SurfaceSetName(added, part.SurfaceGetName(surface));
            target.SurfaceSetMaterial(added, part.SurfaceGetMaterial(surface));
        }
    }

    private static void AnchorUnskinned(
        Godot.Collections.Array arrays, int vertexCount, int anchorBone)
    {
        if (vertexCount == 0)
        {
            return;
        }

        bool hasBones = arrays[(int)Mesh.ArrayType.Bones].VariantType != Variant.Type.Nil;
        bool hasWeights = arrays[(int)Mesh.ArrayType.Weights].VariantType != Variant.Type.Nil;

        if (hasBones && hasWeights)
        {
            return;
        }

        var bones = new int[vertexCount * 4];
        var weights = new float[vertexCount * 4];

        for (int v = 0; v < vertexCount; v++)
        {
            bones[v * 4] = anchorBone;
            weights[v * 4] = 1.0f;
        }

        arrays[(int)Mesh.ArrayType.Bones] = bones;
        arrays[(int)Mesh.ArrayType.Weights] = weights;
    }

    private static List<MdlInfo> LoadRigs(IReadOnlyList<string> modelPaths)
    {
        var rigs = new List<MdlInfo>();

        foreach (string path in modelPaths)
        {
            if (EyeSideOf(path) != EyeSide.None)
            {
                continue;
            }

            byte[]? bytes = Engine_BnkMeshLoader.ReadBytes(path);
            if (bytes is null ||
                !Engine_MdlData.TryParseInfo(bytes, path, out MdlInfo info, out _) ||
                !info.HasBoneTransforms || info.Bones.Count == 0)
            {
                continue;
            }

            rigs.Add(info);
        }

        rigs.Sort((a, b) => b.Bones.Count.CompareTo(a.Bones.Count));
        return rigs;
    }

    private static bool TryEyeOffset(
        List<MdlInfo> rigs, EyeSide side, out Vector3 offset, out int boneIndex)
    {
        offset = Vector3.Zero;
        boneIndex = -1;
        string[] candidates = side == EyeSide.Left ? LeftBoneCandidates : RightBoneCandidates;

        foreach (MdlInfo rig in rigs)
        {
            int bone = FindBone(rig, candidates);
            if (bone < 0)
            {
                continue;
            }

            offset = ToMeshSpace(BindOrigin(rig, bone));
            boneIndex = bone;
            return true;
        }

        return false;
    }

    private static Vector3 ToMeshSpace(Vector3 origin) =>
        new(origin.X, origin.Z, -origin.Y);

    private static Vector3 BindOrigin(MdlInfo rig, int bone)
    {
        var chain = new List<int>();
        var visited = new HashSet<int>();

        for (int current = bone;
             current >= 0 && current < rig.Bones.Count && visited.Add(current);
             current = rig.Bones[current].ParentId)
        {
            chain.Add(current);
        }

        var origin = Vector3.Zero;

        foreach (int current in chain)
        {
            if (current >= rig.BoneTransforms.Count)
            {
                continue;
            }

            float[] transform = rig.BoneTransforms[current];
            if (transform.Length < 10)
            {
                continue;
            }

            origin = new Vector3(
                origin.X * transform[7], origin.Y * transform[8], origin.Z * transform[9]);

            var rotation = new Quaternion(
                transform[0], transform[1], transform[2], transform[3]);

            if (rotation.LengthSquared() > 1e-16f)
            {
                origin = rotation.Normalized() * origin;
            }

            origin += new Vector3(transform[4], transform[5], transform[6]);
        }

        return origin;
    }

    private static int FindBone(MdlInfo rig, IReadOnlyList<string> candidates)
    {
        var lookup = new Dictionary<string, int>(rig.Bones.Count * 3);

        for (int i = 0; i < rig.Bones.Count; i++)
        {
            string normalised = Normalise(rig.Bones[i].Name);
            string bare = WithoutShadowPrefix(normalised);

            Alias(lookup, normalised, i);
            Alias(lookup, bare, i);
            Alias(lookup, "shadow_" + bare, i);
        }

        foreach (string candidate in candidates)
        {
            string normalised = Normalise(candidate);
            if (lookup.TryGetValue(normalised, out int bone))
            {
                return bone;
            }

            string bare = WithoutShadowPrefix(normalised);
            if (lookup.TryGetValue(bare, out bone) ||
                lookup.TryGetValue("shadow_" + bare, out bone))
            {
                return bone;
            }
        }

        return -1;
    }

    private static void Alias(Dictionary<string, int> lookup, string alias, int bone)
    {
        if (alias.Length != 0)
        {
            lookup.TryAdd(alias, bone);
        }
    }

    private static string Normalise(string name)
    {
        var text = new System.Text.StringBuilder(name.Length);

        foreach (char c in name)
        {
            text.Append(c is ' ' or '-' or '.' ? '_' : char.ToLowerInvariant(c));
        }

        return text.ToString().Trim('_');
    }

    private static string WithoutShadowPrefix(string name) =>
        name.Length > 7 && name.StartsWith("shadow_", StringComparison.Ordinal)
            ? name[7..]
            : name;
}
