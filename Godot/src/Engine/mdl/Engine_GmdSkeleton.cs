using System;
using System.Collections.Generic;
using System.Text;
using Godot;

namespace Refii.Models;

/// <summary>One entry in a <c>.mdl.gmd</c>'s attachment table.</summary>
/// <param name="Name">
/// The attachment's name AS THE FILE SPELLS IT — the dotted form, e.g.
/// <c>Prop.FX.Particle.Effect_1.FXENV_Lamp_Flame_01.par</c>. This is the key the guest matches
/// on: `sub_822650A0` CRC-32s a stated DummyObject value UNMODIFIED (poly 0xEDB88320, init 0,
/// no final XOR — `sub_823B0A38`) and binary-searches with it. There is no path parsing and no
/// name rewriting anywhere in the guest.
/// </param>
/// <param name="Local">The attachment's own transform, relative to <paramref name="Node"/>.</param>
/// <param name="Node">The node it hangs from, or -1 when it hangs from the model root.</param>
public readonly record struct GmdAttachment(string Name, Transform3D Local, int Node);

/// <summary>
/// A model's <c>.mdl.gmd</c> — the <c>GameMesh</c> skeleton sidecar, node table AND attachment
/// table.
///
/// THE ATTACHMENT TABLE IS WHY THIS EXISTS. A model's dummies are not all bones: ESA_HangingLantern
/// carries `FX_Particle_DummyObject` and `Layout_Light_DummyObject` in its node table and
/// `Prop.FX.Particle.Effect_1.FXENV_Lamp_Flame_01.par` — the name its GDB data actually states —
/// in the attachment table after it. A lookup that only searches the model's BONES therefore
/// misses the stated name, fails the derived `FX_Particle_DummyObject1` too because no such node
/// exists, and drops the effect onto the lantern's origin. Nine of BWSSlums' eighteen misplaced
/// effects were this one model.
///
/// The layout, verified against `ParseGmdFxAttachments` in the asset browser's
/// <c>Sockets.inl</c> and against a dump of ESA_HangingLantern.mdl.gmd. Everything is
/// big-endian:
///
///     0x00  char[8]  "GameMesh"
///     0x2C  u32      nodeCount
///     0x30           nodeCount x { strz name; s32 parent }   parent &lt; 0 = root
///           u32      transformCount, equal to nodeCount
///                    nodeCount x 44 bytes: 11 floats — quat xyzw, translation, scale, one pad
///           u32      attachmentCount
///                    attachmentCount x { strz name; float quat[4]; float pos[3]; s32 node }
///
/// The transforms are LOCAL, child-of-parent, in the model's own space — the same raw space the
/// vertices and the .mdl bone rest pose are read in, so a resolved attachment composes straight
/// onto a placement without an axis conversion.
/// </summary>
public sealed class Engine_GmdSkeleton
{
    /// <summary>The record the file stores per node: 11 floats, 44 bytes.</summary>
    public const int NodeTransformStride = 44;

    /// <summary>
    /// Refusal thresholds, not truncation. A file whose counts run past these is being misread —
    /// the header offset is wrong, or it is not a GameMesh at all — and walking on would parse
    /// name strings out of float data. The asset browser uses the same two numbers.
    /// </summary>
    public const int MaxNodes = 512;

    public const int MaxAttachments = 64;

    private Engine_GmdSkeleton(
        string[] names, int[] parents, Transform3D[] locals, GmdAttachment[] attachments)
    {
        Names = names;
        Parents = parents;
        Locals = locals;
        Attachments = attachments;
    }

    public string[] Names { get; }

    public int[] Parents { get; }

    /// <summary>Each node's transform relative to its parent.</summary>
    public Transform3D[] Locals { get; }

    public GmdAttachment[] Attachments { get; }

    /// <summary>
    /// The skeleton a <c>.mdl.gmd</c> holds, or null when the bytes are not one or do not
    /// describe themselves consistently.
    ///
    /// Null rather than a partial result on every failure. A half-read node table produces names
    /// and parents that look ordinary and place attachments metres away from where they belong,
    /// which is far worse than reporting that the sidecar could not be read.
    /// </summary>
    public static Engine_GmdSkeleton? TryParse(byte[]? bytes)
    {
        if (bytes is null || bytes.Length < 0x3C ||
            Encoding.ASCII.GetString(bytes, 0, 8) != "GameMesh")
        {
            return null;
        }

        uint nodeCount = ReadU32(bytes, 0x2C);
        if (nodeCount > MaxNodes)
        {
            return null;
        }

        var names = new string[nodeCount];
        var parents = new int[nodeCount];
        int at = 0x30;

        for (uint i = 0; i < nodeCount; i++)
        {
            if (!TryReadStringZ(bytes, ref at, out names[i]) || at + 4 > bytes.Length)
            {
                return null;
            }

            parents[i] = unchecked((int)ReadU32(bytes, at));
            at += 4;
        }

        var locals = new Transform3D[nodeCount];
        if (nodeCount != 0)
        {
            if (at + 4 > bytes.Length)
            {
                return null;
            }

            uint transformCount = ReadU32(bytes, at);
            at += 4;

            // The count is stated twice and both statements have to agree. Where they do not,
            // the walk is off the rails and the stride would carry the error into every node.
            if (transformCount != nodeCount ||
                at + ((long)transformCount * NodeTransformStride) > bytes.Length)
            {
                return null;
            }

            for (uint i = 0; i < nodeCount; i++)
            {
                locals[i] = new Transform3D(
                    new Basis(ReadQuaternion(bytes, at)),
                    new Vector3(
                        ReadF32(bytes, at + 16), ReadF32(bytes, at + 20),
                        ReadF32(bytes, at + 24)));
                at += NodeTransformStride;
            }
        }

        if (at + 4 > bytes.Length)
        {
            return null;
        }

        uint attachmentCount = ReadU32(bytes, at);
        at += 4;
        if (attachmentCount > MaxAttachments)
        {
            return null;
        }

        var attachments = new List<GmdAttachment>((int)attachmentCount);
        for (uint i = 0; i < attachmentCount; i++)
        {
            if (!TryReadStringZ(bytes, ref at, out string name) || at + 32 > bytes.Length)
            {
                return null;
            }

            attachments.Add(new GmdAttachment(
                name,
                new Transform3D(
                    new Basis(ReadQuaternion(bytes, at)),
                    new Vector3(
                        ReadF32(bytes, at + 16), ReadF32(bytes, at + 20),
                        ReadF32(bytes, at + 24))),
                unchecked((int)ReadU32(bytes, at + 28))));
            at += 32;
        }

        return new Engine_GmdSkeleton(names, parents, locals, attachments.ToArray());
    }

    /// <summary>
    /// Where a named dummy sits in the model's own space, attachments first and nodes second.
    ///
    /// Attachments first because that is where the stated names live. Both tables are searched
    /// because both spellings are real: BS_Lights_Town_Slum's node table holds `Prop.Layout.Light`
    /// beside `Layout_Light_DummyObject`, and a model can name the thing either way.
    /// </summary>
    public bool TryResolve(string dummy, out Transform3D local)
    {
        local = Transform3D.Identity;
        if (dummy.Length == 0)
        {
            return false;
        }

        foreach (GmdAttachment attachment in Attachments)
        {
            if (!attachment.Name.Equals(dummy, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            local = Compose(attachment.Node) * attachment.Local;
            return true;
        }

        int index = Array.FindIndex(
            Names, n => n.Equals(dummy, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return false;
        }

        local = Compose(index);
        return true;
    }

    /// <summary>
    /// A node's transform in model space — its own local with every ancestor's applied outside
    /// it, which is what makes a dummy hanging off bone 4 land at the lamp's head rather than at
    /// its foot.
    /// </summary>
    private Transform3D Compose(int node)
    {
        var world = Transform3D.Identity;

        // Bounded, because a corrupt or cyclic parent link would otherwise spin here. 64 is the
        // same guard the asset browser walks with, and no shipped rig is close to that deep.
        for (int at = node, guard = 0; at >= 0 && at < Locals.Length && guard < 64; guard++)
        {
            world = Locals[at] * world;
            at = Parents[at];
        }

        return world;
    }

    private static bool TryReadStringZ(byte[] bytes, ref int at, out string text)
    {
        text = string.Empty;
        int start = at;
        while (at < bytes.Length && bytes[at] != 0)
        {
            at++;
        }

        if (at >= bytes.Length)
        {
            return false;
        }

        text = Encoding.ASCII.GetString(bytes, start, at - start);
        at++;
        return true;
    }

    private static Quaternion ReadQuaternion(byte[] bytes, int at)
    {
        var q = new Quaternion(
            ReadF32(bytes, at), ReadF32(bytes, at + 4),
            ReadF32(bytes, at + 8), ReadF32(bytes, at + 12));

        // An unnormalised or non-finite quaternion is a read that went wrong, and Godot's Basis
        // constructor throws on one rather than shrugging. Identity keeps the rest of the table
        // readable so the caller sees a placement it can reject, not an exception.
        float length = q.LengthSquared();
        return float.IsFinite(length) && length > 0.0001f
            ? q.Normalized()
            : Quaternion.Identity;
    }

    private static float ReadF32(byte[] bytes, int at) =>
        BitConverter.Int32BitsToSingle(unchecked((int)ReadU32(bytes, at)));

    private static uint ReadU32(byte[] bytes, int at) =>
        ((uint)bytes[at] << 24) | ((uint)bytes[at + 1] << 16) |
        ((uint)bytes[at + 2] << 8) | bytes[at + 3];
}
