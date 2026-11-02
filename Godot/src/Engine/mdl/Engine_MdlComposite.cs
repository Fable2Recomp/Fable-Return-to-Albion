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
    public const string PartPathsMeta = "mdl_part_paths";

    public const string PartSpansMeta = "mdl_part_spans";

    /// <summary>
    /// The model paths behind a drawn mesh — every part of a composite, or the one model a
    /// bank-loaded mesh came from. What the cloth, the attachments and the hides key on.
    /// </summary>
    public static string[] ModelPathsOf(Mesh mesh)
    {
        if (mesh.HasMeta(PartPathsMeta))
        {
            return mesh.GetMeta(PartPathsMeta).AsStringArray();
        }

        string path = mesh.ResourcePath;
        return path.StartsWith(Engine_BnkMeshLoader.PathPrefix, System.StringComparison.OrdinalIgnoreCase)
            ? new[] { path[Engine_BnkMeshLoader.PathPrefix.Length..] }
            : System.Array.Empty<string>();
    }

    /// <summary>The index into the part list each surface came from, in surface order.</summary>
    public const string SurfacePartsMeta = "mdl_surface_parts";

    public static readonly StringName SurfacePartsMetaName = SurfacePartsMeta;
    public static readonly StringName PartHashesMetaName = "mdl_part_hashes";

    /// <summary>The part hashes the caller assembled from, in part order; set by the caller.</summary>
    public const string PartHashesMeta = "mdl_part_hashes";

    /// <summary>One surface's coarser levels, to pass through when the surface is re-added.</summary>
    public static Godot.Collections.Dictionary? SurfaceLodsOf(Mesh mesh, int surface) =>
        Engine_MdlMesh.SurfaceLods(mesh, surface);

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
                string Name, Material Material, string? Bare, bool? Left, string Region, int Part,
                Godot.Collections.Dictionary? Lods, int[]? PrtDescriptor, float[]? PrtTable)>();
        var surfaceRegions = new List<string>();
        var surfaceParts = new List<int>();
        var surfaceLods = new Godot.Collections.Array();
        bool anyLods = false;
        var prtDescriptors = new Godot.Collections.Array();
        var prtTables = new Godot.Collections.Array();
        bool anyPrt = false;

        var partPaths = new List<string>();
        var partSpans = new List<int>();
        int partVertexBase = 0;

        // EYES ARE POSITIONAL, NOT NAMED. The guest hangs the model at comp7+0x18 on
        // `Shadow_R_Eye` and the one at +0x28 on `Shadow_L_Eye`; with `BodgeEyes` the left
        // slot HOLDS THE RIGHT MODEL, so both eyeballs are the same `.mdl` and its `_r` suffix
        // would put both on the right bone and leave the left socket empty. The bridge appends
        // them right then left; when two eye parts share a path the side comes from that order.
        var eyeSides = new Dictionary<int, bool>();  // part index -> left
        {
            var eyeParts = new List<int>();
            for (int i = 0; i < parts.Count; i++)
            {
                if (IsEyePart(parts[i]))
                {
                    eyeParts.Add(i);
                }
            }

            if (eyeParts.Count == 2 && string.Equals(
                    parts[eyeParts[0]], parts[eyeParts[1]], StringComparison.OrdinalIgnoreCase))
            {
                eyeSides[eyeParts[0]] = false;
                eyeSides[eyeParts[1]] = true;
            }
        }

        int partIndex = -1;
        foreach (string path in parts)
        {
            ++partIndex;
            if (path.Length == 0 || resolve(path) is not ArrayMesh part ||
                part.GetSurfaceCount() == 0)
            {
                ++missing;
                continue;
            }

            ++resolved;

            int partVertices = 0;
            for (int surface = 0; surface < part.GetSurfaceCount(); surface++)
            {
                partVertices += Engine_MdlSurfaceArrays.VertexCount(part, surface);
            }

            partPaths.Add(path);
            partSpans.Add(partVertexBase);
            partSpans.Add(partVertices);
            partVertexBase += partVertices;

            Engine_MdlRig? rig = Engine_MdlRig.TryFrom(part);
            int[]? remap = rig is { } table ? Absorb(table, boneIndex, names, parents, rest) : null;

            string[] partRegions = Engine_MdlMesh.SurfaceRegions(part);
            for (int surface = 0; surface < part.GetSurfaceCount(); surface++)
            {
                Godot.Collections.Array arrays = Engine_MdlSurfaceArrays.Of(part, surface);

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
                    skinned ? null : path,
                    eyeSides.TryGetValue(partIndex, out bool left) ? left : null,
                    surface < partRegions.Length ? partRegions[surface] : string.Empty,
                    partIndex,
                    Engine_MdlMesh.SurfaceLods(part, surface),
                    part.HasMeta(Engine_MdlTransfer.DescriptorMeta) ? part.GetMeta(Engine_MdlTransfer.DescriptorMeta).AsInt32Array() : null,
                    part.HasMeta(Engine_MdlTransfer.TableMeta) ? part.GetMeta(Engine_MdlTransfer.TableMeta).AsFloat32Array() : null));
            }
        }

        // THE GUEST'S EYE BONES, WHERE THE PARTS DO NOT CARRY THEM. `sub_82209480` hangs each eye
        // on the title's eye bones (Fable 2's `Shadow_L_Eye` / `Shadow_R_Eye`, RigRole.LeftEye /
        // RightEye) of the runtime rig and draws it with that bone's pose
        // (rotation and translation); those bones come from the animation skeleton, not from any
        // part — a hero's `.mdl.gmd` has 28 nodes and only the `Focal_Eye_DummyObject` pair.
        // Hung on the dummy instead, the eye never took the guest's eye pose at all. Each bone is
        // added at its dummy's rest under the same parent; the rig maps it to the guest's bone by
        // name and drives it with the guest's pose (see Anchor for the eye's own turn).
        bool anyEye = false;
        foreach (var entry in pending)
        {
            anyEye |= entry.Bare is { } eyePath && IsEyePart(eyePath);
        }

        if (anyEye)
        {
            if (EyeBone(left: true) is { } leftEye)
            {
                AddEyeBone(leftEye, "focal_eye_dummyobject1", boneIndex, names, parents, rest);
            }

            if (EyeBone(left: false) is { } rightEye)
            {
                AddEyeBone(rightEye, "focal_eye_dummyobject", boneIndex, names, parents, rest);
            }
        }

        foreach ((Godot.Collections.Array arrays, Mesh.PrimitiveType primitive, string name,
                  Material material, string? bare, bool? forcedLeft, string region, int fromPart,
                  Godot.Collections.Dictionary? lods, int[]? prtDescriptor, float[]? prtTable) in pending)
        {
            if (bare is not null && !Anchor(arrays, boneIndex, parents, rest, bare, forcedLeft))
            {
                // AN EYE WITH NO EYE BONE IS NOT DRAWN. The guest creates no eye instance when
                // `Shadow_L_Eye`/`Shadow_R_Eye` is missing from the rig (`sub_82209480` bails
                // at 0x8220A0BC/0x8220A0C8); anchored to bone 0 it would sit at the feet.
                continue;
            }

            int added = Engine_MdlSurfaceArrays.Add(mesh, primitive, arrays, lods);
            mesh.SurfaceSetName(added, name);
            mesh.SurfaceSetMaterial(added, material);
            surfaceRegions.Add(region);
            surfaceParts.Add(fromPart);
            // Each part's own transfer header and cluster table: the title uploads a part's
            // g_PRTConstants from its own model (`sub_8225DC80`), so they ride per surface.
            prtDescriptors.Add(prtDescriptor ?? System.Array.Empty<int>());
            prtTables.Add(prtTable ?? System.Array.Empty<float>());
            anyPrt |= prtDescriptor is { Length: > 0 } && prtTable is { Length: > 0 };
            surfaceLods.Add(lods ?? new Godot.Collections.Dictionary());
            anyLods |= lods is not null;
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

        if (partPaths.Count != 0)
        {
            mesh.SetMeta(PartPathsMeta, partPaths.ToArray());
            mesh.SetMeta(PartSpansMeta, partSpans.ToArray());
        }

        mesh.SetMeta(Engine_MdlMesh.SurfaceRegionsMeta, surfaceRegions.ToArray());
        mesh.SetMeta(SurfacePartsMeta, surfaceParts.ToArray());
        if (anyPrt)
        {
            mesh.SetMeta(Engine_MdlTransfer.SurfaceDescriptorsMeta, prtDescriptors);
            mesh.SetMeta(Engine_MdlTransfer.SurfaceTablesMeta, prtTables);
        }
        if (anyLods)
        {
            mesh.SetMeta(Engine_MdlMesh.SurfaceLodsMeta, surfaceLods);
        }

        return mesh;
    }

    /// <summary>
    /// The bones an eye mesh can hang from after the title's own eye bone (<see cref="EyeBone"/>),
    /// best first.
    ///
    /// THE TITLE'S EYE BONES ARE THE NAMES THE GUEST USES. Fable 2 resolves `Shadow_L_Eye` /
    /// `Shadow_R_Eye` by name on component 6 (`sub_82209480` at 0x8220A078/0x8220A094, the globals
    /// dword_8349C248 and dword_8349C24C), and creates no eye instance at all when either is
    /// missing. Without them an eye part matched no bone and was anchored to bone 0, which puts
    /// the eyeballs at the character's origin.
    /// </summary>
    private static readonly string[] LeftEyeBones =
        { "l_eye", "left_eye", "lefteye", "character_focal_eye_left",
          "focal_eye_dummyobject1" };

    private static readonly string[] RightEyeBones =
        { "r_eye", "right_eye", "righteye", "character_focal_eye_right",
          "focal_eye_dummyobject" };

    /// <summary>The Godot bone name of the title's left or right eye bone, or null when its rig names none.</summary>
    private static string? EyeBone(bool left) =>
        Refii.Core.Engine_TitleGame.Active.Rig.BoneName(
            left ? Refii.Animations.Engine_TitleRig.RigRole.LeftEye : Refii.Animations.Engine_TitleRig.RigRole.RightEye);

    /// <summary>
    /// Binds an unskinned part rigidly to one bone. False means the part must not be drawn:
    /// it is an eye and the rig has no eye bone to hang it on.
    /// </summary>
    private static bool Anchor(
        Godot.Collections.Array arrays,
        Dictionary<string, int> boneIndex,
        IReadOnlyList<int> parents,
        IReadOnlyList<float> rest,
        string path,
        bool? forcedLeft = null)
    {
        if (arrays.Count <= (int)Mesh.ArrayType.Vertex ||
            arrays[(int)Mesh.ArrayType.Vertex].VariantType != Variant.Type.PackedVector3Array)
        {
            return true;
        }

        Vector3[] vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        if (vertices.Length == 0 || boneIndex.Count == 0)
        {
            return true;
        }

        int anchor = 0;
        if (IsEyePart(path) && !TryEyeBone(boneIndex, path, out _, forcedLeft))
        {
            return false;
        }

        if (TryEyeBone(boneIndex, path, out int eye, forcedLeft))
        {
            anchor = eye;

            // THE GUEST'S EYE TRANSFORM: the eye bone's model-space pose times a FIXED quarter turn.
            // `sub_82209480` (CECGraphicAppearanceEyes, secondary vtable 0x82002D54 slot +4)
            // rebuilds each eye's matrix from the bone's as [row1; -row0; row2] (0x82209d18..
            // 0x82209d98) — Rz(+90) about the raw up axis on the mesh side — and sends that with
            // the bone's position; no rest pose or inverse bind enters it. The live hero's poses
            // (L -98, R -81 degrees) then cancel the models' own ~10 degree outward splay and both
            // irises land within two degrees of the head's forward. Placed here at the bone's
            // rest x the turn, skinning `pose x rest^-1 x (rest x turn x v)` is that formula.
            Transform3D toBone = ToMeshSpace(GlobalRest(eye, parents, rest)) * new Transform3D(EyeTurn, Vector3.Zero);

            // NaN COMPARES UNEQUAL TO EVERYTHING, so a non-finite transform would move EVERY
            // vertex of the part to NaN, and a NaN vertex hangs Godot's render-list sort. This
            // path bypasses the guard in Engine_MdlMesh: those vertices were accepted there and
            // are being MOVED afterwards. The rest it reads is raw f32 straight off disk.
            if (!toBone.Origin.IsFinite() || !toBone.Basis.X.IsFinite() ||
                !toBone.Basis.Y.IsFinite() || !toBone.Basis.Z.IsFinite())
            {
                toBone = new Transform3D(EyeTurn, Vector3.Zero);
            }

            for (int v = 0; v < vertices.Length; v++)
            {
                vertices[v] = toBone * vertices[v];
            }

            arrays[(int)Mesh.ArrayType.Vertex] = vertices;

            Basis turn = toBone.Basis.Orthonormalized();
            if (arrays.Count > (int)Mesh.ArrayType.Normal &&
                arrays[(int)Mesh.ArrayType.Normal].VariantType == Variant.Type.PackedVector3Array)
            {
                Vector3[] normals = arrays[(int)Mesh.ArrayType.Normal].AsVector3Array();
                for (int v = 0; v < normals.Length; v++)
                {
                    normals[v] = turn * normals[v];
                }

                arrays[(int)Mesh.ArrayType.Normal] = normals;
            }

            if (arrays.Count > (int)Mesh.ArrayType.Tangent &&
                arrays[(int)Mesh.ArrayType.Tangent].VariantType == Variant.Type.PackedFloat32Array)
            {
                float[] tangents = arrays[(int)Mesh.ArrayType.Tangent].AsFloat32Array();
                for (int t = 0; t + 3 < tangents.Length; t += 4)
                {
                    Vector3 turned = turn * new Vector3(tangents[t], tangents[t + 1], tangents[t + 2]);
                    tangents[t] = turned.X;
                    tangents[t + 1] = turned.Y;
                    tangents[t + 2] = turned.Z;
                }

                arrays[(int)Mesh.ArrayType.Tangent] = tangents;
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
        return true;
    }

    /// <summary>An eye model by its file name: the leaf names a side, and `eye` without `eyecase`.</summary>
    private static bool IsEyePart(string path)
    {
        string leaf = path.Replace('\\', '/').ToLowerInvariant();
        int slash = leaf.LastIndexOf('/');
        if (slash >= 0)
        {
            leaf = leaf[(slash + 1)..];
        }

        return leaf.Contains("eye", StringComparison.Ordinal) &&
            !leaf.Contains("eyecase", StringComparison.Ordinal) &&
            !leaf.Contains("eyelash", StringComparison.Ordinal);
    }

    private static Vector3 ToMeshSpace(Vector3 origin) => new(origin.X, origin.Z, -origin.Y);

    /// <summary>The eye's fixed quarter turn: raw Rz(+90), which in the mesh's axes is +90 degrees about +Y.</summary>
    private static readonly Basis EyeTurn = new(Vector3.Up, Mathf.Pi / 2f);

    /// <summary>The guest's (x, y, z) as the mesh's (x, z, -y): a proper rotation, so a transform conjugates.</summary>
    private static readonly Basis GuestToMesh = new(new Vector3(1f, 0f, 0f), new Vector3(0f, 0f, -1f), new Vector3(0f, 1f, 0f));

    private static Transform3D ToMeshSpace(Transform3D guest) =>
        new(GuestToMesh * guest.Basis * GuestToMesh.Transposed(), ToMeshSpace(guest.Origin));

    /// <summary>
    /// The bone's whole rest transform in model space, guest axes: each link scale, then rotate,
    /// then translate, composed up the parent chain.
    /// </summary>
    private static Transform3D GlobalRest(
        int bone, IReadOnlyList<int> parents, IReadOnlyList<float> rest)
    {
        var global = Transform3D.Identity;
        var seen = new HashSet<int>();
        for (int current = bone;
             current >= 0 && current < parents.Count && seen.Add(current);
             current = parents[current])
        {
            int at = current * Engine_MdlMesh.BoneRestStride;
            if (at + Engine_MdlMesh.BoneRestStride > rest.Count)
            {
                continue;
            }

            var rotation = new Quaternion(rest[at + 0], rest[at + 1], rest[at + 2], rest[at + 3]);
            Basis basis = rotation.LengthSquared() > 1e-16f ? new Basis(rotation.Normalized()) : Basis.Identity;
            basis *= Basis.FromScale(new Vector3(rest[at + 7], rest[at + 8], rest[at + 9]));
            global = new Transform3D(basis, new Vector3(rest[at + 4], rest[at + 5], rest[at + 6])) * global;
        }

        return global;
    }

    private static bool TryEyeBone(
        Dictionary<string, int> boneIndex, string path, out int bone, bool? forcedLeft = null)
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

        bool left = forcedLeft ?? (leaf.Contains("left", StringComparison.Ordinal) ||
            leaf.EndsWith("_l", StringComparison.Ordinal) ||
            leaf.Contains("_l.", StringComparison.Ordinal));

        bool right = forcedLeft.HasValue ? !forcedLeft.Value :
            (leaf.Contains("right", StringComparison.Ordinal) ||
            leaf.EndsWith("_r", StringComparison.Ordinal) ||
            leaf.Contains("_r.", StringComparison.Ordinal));

        if (!left && !right)
        {
            return false;
        }

        if (EyeBone(left) is { } titleEye && TryFlatBone(boneIndex, Flatten(titleEye), out bone))
        {
            return true;
        }

        foreach (string candidate in left ? LeftEyeBones : RightEyeBones)
        {
            if (TryFlatBone(boneIndex, candidate, out bone))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The bone whose flattened name is <paramref name="flat"/>.</summary>
    private static bool TryFlatBone(Dictionary<string, int> boneIndex, string flat, out int bone)
    {
        foreach (KeyValuePair<string, int> entry in boneIndex)
        {
            if (Flatten(entry.Key) == flat)
            {
                bone = entry.Value;
                return true;
            }
        }

        bone = 0;
        return false;
    }

    /// <summary>
    /// Adds <paramref name="name"/> at <paramref name="dummy"/>'s rest and parent, unless the table
    /// already holds it or has no such dummy to stand it on.
    /// </summary>
    private static void AddEyeBone(
        string name, string dummy, Dictionary<string, int> boneIndex,
        List<string> names, List<int> parents, List<float> rest)
    {
        string flat = Flatten(name);
        int anchor = -1;
        foreach (KeyValuePair<string, int> entry in boneIndex)
        {
            string key = Flatten(entry.Key);
            if (key == flat)
            {
                return;
            }

            if (key == dummy)
            {
                anchor = entry.Value;
            }
        }

        int stride = Engine_MdlMesh.BoneRestStride;
        if (anchor < 0 || anchor >= parents.Count || (anchor + 1) * stride > rest.Count)
        {
            return;
        }

        int added = names.Count;
        names.Add(name);
        parents.Add(parents[anchor]);
        for (int lane = 0; lane < stride; lane++)
        {
            rest.Add(rest[(anchor * stride) + lane]);
        }

        // AT REST THE TURN IS UNDONE: the bone rests at the dummy's rotation times the inverse of
        // the eye's quarter turn (raw Rz(-90)), so where no guest poses the rig — the editor, the
        // viewers — `rest x turn` is the identity and the eye shows its authored forward. A live
        // pose replaces the rest, and the eye takes the guest's `pose x turn`.
        var dummyRotation = new Quaternion(
            rest[(anchor * stride) + 0], rest[(anchor * stride) + 1],
            rest[(anchor * stride) + 2], rest[(anchor * stride) + 3]);
        if (dummyRotation.LengthSquared() < 1e-16f)
        {
            dummyRotation = Quaternion.Identity;
        }

        Quaternion eyeRest = dummyRotation.Normalized() * new Quaternion(Vector3.Back, -Mathf.Pi / 2f);
        int at = added * stride;
        rest[at + 0] = eyeRest.X;
        rest[at + 1] = eyeRest.Y;
        rest[at + 2] = eyeRest.Z;
        rest[at + 3] = eyeRest.W;

        boneIndex[name] = added;
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
