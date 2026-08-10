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

                if (remap is not null &&
                    arrays.Count > (int)Mesh.ArrayType.Bones &&
                    arrays[(int)Mesh.ArrayType.Bones].VariantType != Variant.Type.Nil)
                {
                    int[] bones = arrays[(int)Mesh.ArrayType.Bones].AsInt32Array();
                    for (int i = 0; i < bones.Length; i++)
                    {
                        int local = bones[i];
                        bones[i] = local >= 0 && local < remap.Length ? remap[local] : 0;
                    }

                    arrays[(int)Mesh.ArrayType.Bones] = bones;
                }

                mesh.AddSurfaceFromArrays(part.SurfaceGetPrimitiveType(surface), arrays);

                int added = mesh.GetSurfaceCount() - 1;
                mesh.SurfaceSetName(added, part.SurfaceGetName(surface));
                mesh.SurfaceSetMaterial(added, part.SurfaceGetMaterial(surface));
            }
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

    /// <summary>
    /// Folds one part's bone table into the shared one and returns its index remap.
    /// </summary>
    /// <remarks>
    /// Walked in the part's own order, which is parent-before-child, so a bone's parent is
    /// already in the union by the time the bone itself is added and can be remapped directly.
    /// A name already present keeps the FIRST part's rest transform: the parts agree on the
    /// body skeleton, and if they ever disagree, one answer chosen consistently beats the last
    /// part silently overwriting everything the earlier ones bound to.
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
}
