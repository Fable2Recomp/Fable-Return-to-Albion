using System;
using System.Collections.Generic;
using Godot;

namespace Refii.Models;

/// <summary>
/// One mesh assembled from SEVERAL models.
///
/// Fable builds two kinds of thing this way. An entity can name a list of models in the GDB —
/// which is what the editor's <see cref="Refii.Entities.Engine_EntityInstance"/> draws — and a
/// COMPOSITE CHARACTER is layered at runtime out of a head, a torso, legs, eyes and
/// accessories, which is what <c>CECGraphicAppearanceMorph</c> holds. Both are the same
/// operation: resolve each part, then concatenate the surfaces.
///
/// It lives here, and takes its resolver as an argument, for two reasons. There are currently
/// two ways a `.mdl` becomes an ArrayMesh — the `game://` resource loader and
/// <c>Engine_PropStreamer</c>'s own cache — and this has no business picking between them or
/// dragging a dependency on either into the model layer. And the alternative was a second copy
/// of the surface-concatenation loop, which is exactly the thing the engine conventions forbid.
/// </summary>
public static class Engine_MdlComposite
{
    /// <summary>
    /// Concatenates the surfaces of every part that resolves, or null when none of them do.
    /// </summary>
    /// <param name="parts">Virtual model paths, in the order they should be assembled.</param>
    /// <param name="resolve">
    /// Turns one virtual path into a mesh, or null. Failures are counted rather than fatal —
    /// a character missing an accessory should still get a body.
    /// </param>
    /// <param name="resolved">How many parts produced geometry.</param>
    /// <param name="missing">How many did not.</param>
    public static ArrayMesh? Build(
        IReadOnlyList<string> parts,
        Func<string, Mesh?> resolve,
        out int resolved,
        out int missing)
    {
        resolved = 0;
        missing = 0;

        var mesh = new ArrayMesh();

        // THE UNIFIED BONE TABLE, BUILT BY NAME.
        //
        // Every part of one character is rigged to the same body skeleton, but each `.mdl`
        // ships its OWN table and numbers it independently — so a head's bone 5 and a torso's
        // bone 5 are different bones. Concatenating the surfaces without touching BLENDINDICES
        // binds half the character to the wrong skeleton, which on screen is the same fan of
        // spikes that index-space mismatch always produces, and reads as broken weights rather
        // than as a numbering problem.
        //
        // The name is the only key the parts share, exactly as it is the only key the model and
        // the guest's rig share. So: union the tables by name, and remap each part's indices
        // into the union as its surfaces are copied.
        var boneIndex = new Dictionary<string, int>();
        var names = new List<string>();
        var parents = new List<int>();
        var rest = new List<float>();

        var pending =
            new List<(Godot.Collections.Array Arrays, Mesh.PrimitiveType Primitive,
                string Name, Material Material, string? Bare)>();

        foreach (string path in parts)
        {
            if (path.Length == 0 || resolve(path) is not ArrayMesh part ||
                part.GetSurfaceCount() == 0)
            {
                ++missing;
                continue;
            }

            ++resolved;

            Engine_MdlRig? rig = Engine_MdlRig.TryFrom(part);
            int[]? remap = rig is { } table ? Absorb(table, boneIndex, names, parents, rest) : null;

            for (int surface = 0; surface < part.GetSurfaceCount(); surface++)
            {
                Godot.Collections.Array arrays = part.SurfaceGetArrays(surface);

                bool skinned = arrays.Count > (int)Mesh.ArrayType.Bones &&
                    arrays[(int)Mesh.ArrayType.Bones].VariantType != Variant.Type.Nil;

                if (remap is not null && skinned)
                {
                    int[] bones = arrays[(int)Mesh.ArrayType.Bones].AsInt32Array();
                    for (int i = 0; i < bones.Length; i++)
                    {
                        int local = bones[i];
                        bones[i] = local >= 0 && local < remap.Length ? remap[local] : 0;
                    }

                    arrays[(int)Mesh.ArrayType.Bones] = bones;
                }
                else if (skinned)
                {
                    // SKINNED, BUT WITH NO TABLE TO REMAP THROUGH.
                    //
                    // `Engine_MdlRig.TryFrom` wants names, parents AND rest, and returns null if
                    // any is missing — which happens when the `.mdl`'s bone-transform count
                    // disagrees with its bone count, so `Engine_MdlMesh` never wrote the rest
                    // metadata. The old code silently fell through here and added the surface
                    // with its indices UNTOUCHED, so a garment carrying its own numbering was
                    // read against the unioned table. Every vertex then attached to whatever
                    // bone happens to sit at that index in a table built from a different part,
                    // which is precisely the flat sheet of triangles hanging off a character.
                    //
                    // Counted and collapsed to bone 0 rather than left to alias. Bone 0 is the
                    // model root, so the part rides the character rigidly instead of being
                    // scattered across its skeleton — wrong, but wrong in one obvious piece
                    // instead of shredded, and the count says how much geometry is affected.
                    int[] bones = arrays[(int)Mesh.ArrayType.Bones].AsInt32Array();
                    UnremappableSurfaces++;
                    UnremappableVertices += bones.Length / 4;
                    for (int i = 0; i < bones.Length; i++)
                    {
                        bones[i] = 0;
                    }

                    arrays[(int)Mesh.ArrayType.Bones] = bones;
                }

                pending.Add((
                    arrays,
                    part.SurfaceGetPrimitiveType(surface),
                    part.SurfaceGetName(surface),
                    part.SurfaceGetMaterial(surface),
                    skinned ? null : path));
            }
        }

        foreach ((Godot.Collections.Array arrays, Mesh.PrimitiveType primitive, string name,
                  Material material, string? bare) in pending)
        {
            if (bare is not null)
            {
                Anchor(arrays, boneIndex, parents, rest, bare);
            }

            mesh.AddSurfaceFromArrays(primitive, arrays);

            int added = mesh.GetSurfaceCount() - 1;
            mesh.SurfaceSetName(added, name);
            mesh.SurfaceSetMaterial(added, material);
        }

        if (mesh.GetSurfaceCount() == 0)
        {
            return null;
        }

        // The rig rides as metadata, which is where Engine_MdlRig looks for it. Without this a
        // composite draws perfectly and can never be posed — it has no bone table to build a
        // Skeleton3D from, so every one of them stands in bind while the guest animates it.
        if (names.Count != 0)
        {
            mesh.SetMeta(Engine_MdlMesh.BoneNamesMeta, names.ToArray());
            mesh.SetMeta(Engine_MdlMesh.BoneParentsMeta, parents.ToArray());
            mesh.SetMeta(Engine_MdlMesh.BoneRestMeta, rest.ToArray());
        }

        return mesh;
    }

    private static readonly string[] LeftEyeBones =
        { "l_eye", "left_eye", "lefteye", "character_focal_eye_left", "focal_eye_dummyobject1" };

    private static readonly string[] RightEyeBones =
        { "r_eye", "right_eye", "righteye", "character_focal_eye_right", "focal_eye_dummyobject" };

    private static void Anchor(
        Godot.Collections.Array arrays,
        Dictionary<string, int> boneIndex,
        IReadOnlyList<int> parents,
        IReadOnlyList<float> rest,
        string path)
    {
        if (arrays.Count <= (int)Mesh.ArrayType.Vertex ||
            arrays[(int)Mesh.ArrayType.Vertex].VariantType != Variant.Type.PackedVector3Array)
        {
            return;
        }

        Vector3[] vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        if (vertices.Length == 0 || boneIndex.Count == 0)
        {
            return;
        }

        int anchor = 0;
        if (TryEyeBone(boneIndex, path, out int eye))
        {
            anchor = eye;

            Vector3 offset = ToMeshSpace(GlobalOrigin(eye, parents, rest));

            // NaN COMPARES UNEQUAL TO EVERYTHING, so a non-finite offset passes the test below
            // and is then added to EVERY vertex of the part — one bad bone rest translation
            // becomes a whole surface of NaN, and a NaN vertex hangs Godot's render-list sort.
            // This path bypasses the guard in Engine_MdlMesh: those vertices were accepted
            // there and are being MOVED afterwards. GlobalOrigin only checks quaternion length;
            // the translation it reads is a raw f32 straight off disk.
            if (!offset.IsFinite())
            {
                offset = Vector3.Zero;
            }

            if (offset != Vector3.Zero)
            {
                for (int v = 0; v < vertices.Length; v++)
                {
                    vertices[v] += offset;
                }

                arrays[(int)Mesh.ArrayType.Vertex] = vertices;
            }
        }

        var bones = new int[vertices.Length * 4];
        var weights = new float[vertices.Length * 4];

        for (int v = 0; v < vertices.Length; v++)
        {
            bones[v * 4] = anchor;
            weights[v * 4] = 1.0f;
        }

        arrays[(int)Mesh.ArrayType.Bones] = bones;
        arrays[(int)Mesh.ArrayType.Weights] = weights;
    }

    private static Vector3 GlobalOrigin(
        int bone, IReadOnlyList<int> parents, IReadOnlyList<float> rest)
    {
        var chain = new List<int>();
        var seen = new HashSet<int>();

        for (int current = bone;
             current >= 0 && current < parents.Count && seen.Add(current);
             current = parents[current])
        {
            chain.Add(current);
        }

        var origin = Vector3.Zero;

        foreach (int current in chain)
        {
            int at = current * Engine_MdlMesh.BoneRestStride;
            if (at + Engine_MdlMesh.BoneRestStride > rest.Count)
            {
                continue;
            }

            origin = new Vector3(
                origin.X * rest[at + 7], origin.Y * rest[at + 8], origin.Z * rest[at + 9]);

            var rotation = new Quaternion(
                rest[at + 0], rest[at + 1], rest[at + 2], rest[at + 3]);

            if (rotation.LengthSquared() > 1e-16f)
            {
                origin = rotation.Normalized() * origin;
            }

            origin += new Vector3(rest[at + 4], rest[at + 5], rest[at + 6]);
        }

        return origin;
    }

    private static Vector3 ToMeshSpace(Vector3 origin) => new(origin.X, origin.Z, -origin.Y);

    private static bool TryEyeBone(
        Dictionary<string, int> boneIndex, string path, out int bone)
    {
        bone = 0;

        string leaf = path.Replace('\\', '/').ToLowerInvariant();
        int slash = leaf.LastIndexOf('/');
        if (slash >= 0)
        {
            leaf = leaf[(slash + 1)..];
        }

        if (!leaf.Contains("eye", StringComparison.Ordinal))
        {
            return false;
        }

        bool left = leaf.Contains("left", StringComparison.Ordinal) ||
            leaf.EndsWith("_l", StringComparison.Ordinal) ||
            leaf.Contains("_l.", StringComparison.Ordinal);

        bool right = leaf.Contains("right", StringComparison.Ordinal) ||
            leaf.EndsWith("_r", StringComparison.Ordinal) ||
            leaf.Contains("_r.", StringComparison.Ordinal);

        if (!left && !right)
        {
            return false;
        }

        foreach (string candidate in left ? LeftEyeBones : RightEyeBones)
        {
            foreach (KeyValuePair<string, int> entry in boneIndex)
            {
                if (Flatten(entry.Key) == candidate)
                {
                    bone = entry.Value;
                    return true;
                }
            }
        }

        return false;
    }

    private static string Flatten(string name)
    {
        var text = new System.Text.StringBuilder(name.Length);
        foreach (char c in name)
        {
            text.Append(c is ' ' or '-' or '.' ? '_' : char.ToLowerInvariant(c));
        }

        return text.ToString().Trim('_');
    }

    /// <summary>
    /// Folds one part's bone table into the shared one and returns its index remap.
    /// </summary>
    /// <remarks>
    /// Walked in the part's own order, which is parent-before-child — measured, not assumed:
    /// all 919 rigged models in globals order their tables that way and none has a duplicate
    /// bone name, so a bone's parent is already in the union when the bone is added and the
    /// by-name key is unambiguous.
    ///
    /// A name already present keeps the FIRST part's rest transform, which is only lossless if
    /// the parts agree. THEY DO NOT ALWAYS. Measured across the shipped models, the same bone
    /// name carries materially different rests in different files — `Shadow_L_Shin` is 0.26
    /// apart between `ch_heromale_middleclass_coat_f` and the male body, and `Shadow_Hip`
    /// varies by more than half a unit. Much of that spread is child-versus-adult rigs that are
    /// never layered together, which is why this is COUNTED here rather than guessed at
    /// offline: only the guest knows which parts actually make up one character, and a coat
    /// bound to the body's shin rest instead of its own is displaced by exactly that distance.
    /// </remarks>
    private static int[] Absorb(
        Engine_MdlRig rig,
        Dictionary<string, int> boneIndex,
        List<string> names,
        List<int> parents,
        List<float> rest)
    {
        var remap = new int[rig.BoneCount];

        for (int b = 0; b < rig.BoneCount; b++)
        {
            string name = rig.Names[b];
            if (boneIndex.TryGetValue(name, out int existing))
            {
                remap[b] = existing;

                // The disagreement, measured on the translation because that is what displaces
                // geometry. Reported by the caller rather than printed per bone: a character is
                // ~4 parts and a region is ~29 characters, so a per-bone line would be noise.
                int here = b * Engine_MdlMesh.BoneRestStride;
                int there = existing * Engine_MdlMesh.BoneRestStride;
                float apart = 0f;
                for (int lane = 4; lane < 7; lane++)
                {
                    apart = Mathf.Max(apart, Mathf.Abs(rig.Rest[here + lane] - rest[there + lane]));
                }

                if (apart > RestDisagreementEpsilon)
                {
                    ++SharedBonesDisagreeing;
                    if (apart > WorstRestDisagreement)
                    {
                        WorstRestDisagreement = apart;
                        WorstRestBone = name;
                    }
                }
                else
                {
                    ++SharedBonesAgreeing;
                }

                continue;
            }

            int parent = rig.Parents[b];
            int mapped = parent >= 0 && parent < b ? remap[parent] : -1;

            remap[b] = names.Count;
            boneIndex[name] = names.Count;
            names.Add(name);
            parents.Add(mapped);

            int at = b * Engine_MdlMesh.BoneRestStride;
            for (int lane = 0; lane < Engine_MdlMesh.BoneRestStride; lane++)
            {
                rest.Add(rig.Rest[at + lane]);
            }
        }

        return remap;
    }

    /// <summary>
    /// A millimetre. Below this two parts are quoting the same authored rest through float
    /// round-trips; above it they are describing different skeletons and the loser's geometry
    /// is displaced by the difference.
    /// </summary>
    private const float RestDisagreementEpsilon = 0.001f;

    // THE FOUR COUNTERS BELOW ARE RUN TOTALS, DELIBERATELY, AND HAVE NO RESET.
    //
    // Assembly happens once per distinct body and the result is cached, so a per-frame reset
    // would zero them on every frame after the first and report a healthy zero for ever. They
    // count everything the cache has ever assembled, which is the question worth asking, and
    // the report says so rather than implying a frame count.

    /// <summary>Shared bones where the parts quote the same rest. The healthy case.</summary>
    public static int SharedBonesAgreeing;

    /// <summary>
    /// Shared bones where a later part wanted a DIFFERENT rest and was overruled.
    ///
    /// Non-zero means composite characters are being assembled from parts rigged to different
    /// skeletons, and every vertex of the losing part that is weighted to such a bone is off by
    /// <see cref="WorstRestDisagreement"/> at most. That is the difference between a coat
    /// hanging on a body and a coat hanging beside it.
    /// </summary>
    public static int SharedBonesDisagreeing;

    /// <summary>How far apart the worst disagreement is, in metres.</summary>
    public static float WorstRestDisagreement;

    /// <summary>Which bone, because "0.26 apart" is not actionable and "Shadow_L_Shin" is.</summary>
    public static string WorstRestBone = string.Empty;

    /// <summary>
    /// Skinned surfaces whose part shipped no usable bone table, so their indices could not be
    /// remapped into the union. Non-zero means geometry was binding through the WRONG table.
    /// </summary>
    public static int UnremappableSurfaces;

    /// <summary>How many vertices those surfaces account for.</summary>
    public static int UnremappableVertices;

}
