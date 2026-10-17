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

    public static readonly Basis ZUpToYUp = new(
        new Vector3(1.0f, 0.0f, 0.0f),
        new Vector3(0.0f, 0.0f, -1.0f),
        new Vector3(0.0f, 1.0f, 0.0f));

    public const int MaxAttachments = 64;

    private static readonly byte[] DefaultHeaderBytes = CreateDefaultHeaderBytes();
    private static readonly byte[] DefaultTrailingBytes = new byte[4];

    private static byte[] CreateDefaultHeaderBytes()
    {
        var header = new byte[0x2C];
        Encoding.ASCII.GetBytes("GameMesh", 0, 8, header, 0);
        // Version = 3 at 0x08 (big-endian)
        header[0x0B] = 3;
        return header;
    }

    private Engine_GmdSkeleton(
        string[] names,
        int[] parents,
        Transform3D[] locals,
        GmdAttachment[] attachments,
        byte[]? headerBytes = null,
        Vector3[]? nodeScales = null,
        float[]? nodePads = null,
        byte[]? trailingBytes = null)
    {
        Names = names;
        Parents = parents;
        Locals = locals;
        Attachments = attachments;
        HeaderBytes = headerBytes is { Length: 0x2C } ? headerBytes : (byte[])DefaultHeaderBytes.Clone();
        if (nodeScales is not null && nodeScales.Length == names.Length)
        {
            NodeScales = nodeScales;
        }
        else
        {
            NodeScales = new Vector3[names.Length];
            for (int i = 0; i < NodeScales.Length; i++)
            {
                NodeScales[i] = Vector3.One;
            }
        }
        NodePads = nodePads is not null && nodePads.Length == names.Length ? nodePads : new float[names.Length];
        TrailingBytes = trailingBytes ?? (byte[])DefaultTrailingBytes.Clone();
    }

    public string[] Names { get; }

    public int[] Parents { get; }

    /// <summary>Each node's transform relative to its parent (in Godot Y-up space).</summary>
    public Transform3D[] Locals { get; }

    public GmdAttachment[] Attachments { get; }

    /// <summary>The 44-byte <c>GameMesh</c> header (<c>0x00..0x2B</c>).</summary>
    public byte[] HeaderBytes { get; }

    /// <summary>Per-node raw scale vectors from the 44-byte node transform records.</summary>
    public Vector3[] NodeScales { get; }

    /// <summary>Per-node raw trailing float from the 44-byte node transform records.</summary>
    public float[] NodePads { get; }

    /// <summary>Trailing Section 3 bytes following the attachment table.</summary>
    public byte[] TrailingBytes { get; }

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
        if (bytes is null || bytes.Length < 0x34 ||
            Encoding.ASCII.GetString(bytes, 0, 8) != "GameMesh")
        {
            return null;
        }

        uint nodeCount = ReadU32(bytes, 0x2C);
        if (nodeCount > MaxNodes)
        {
            return null;
        }

        var headerBytes = new byte[0x2C];
        Array.Copy(bytes, 0, headerBytes, 0, 0x2C);

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
        var nodeScales = new Vector3[nodeCount];
        var nodePads = new float[nodeCount];
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
                locals[i] = ToGodot(new Transform3D(
                    new Basis(ReadQuaternion(bytes, at)),
                    new Vector3(
                        ReadF32(bytes, at + 16), ReadF32(bytes, at + 20),
                        ReadF32(bytes, at + 24))));
                nodeScales[i] = new Vector3(
                    ReadF32(bytes, at + 28), ReadF32(bytes, at + 32), ReadF32(bytes, at + 36));
                nodePads[i] = ReadF32(bytes, at + 40);
                at += NodeTransformStride;
            }
        }

        if (!TryFindAttachmentCount(bytes, at, out uint attachmentCount, out int attachStart))
        {
            // When nodeCount == 0, transformCount (0) is at 0x30 and attachmentCount (0) is at 0x34.
            int trailingOffset = at;
            if (nodeCount == 0 && at + 4 <= bytes.Length && ReadU32(bytes, at) == 0)
            {
                trailingOffset += 4;
            }
            if (trailingOffset + 4 <= bytes.Length && ReadU32(bytes, trailingOffset) == 0)
            {
                trailingOffset += 4;
            }
            byte[] emptyTrailing = trailingOffset <= bytes.Length
                ? bytes[trailingOffset..]
                : (byte[])DefaultTrailingBytes.Clone();
            return new Engine_GmdSkeleton(
                names, parents, locals, Array.Empty<GmdAttachment>(),
                headerBytes, nodeScales, nodePads, emptyTrailing);
        }

        at = attachStart;
        var attachments = new List<GmdAttachment>((int)attachmentCount);
        for (uint i = 0; i < attachmentCount; i++)
        {
            if (!TryReadStringZ(bytes, ref at, out string name) || at + 32 > bytes.Length)
            {
                return null;
            }

            attachments.Add(new GmdAttachment(
                name,
                ToGodot(new Transform3D(
                    new Basis(ReadQuaternion(bytes, at)),
                    new Vector3(
                        ReadF32(bytes, at + 16), ReadF32(bytes, at + 20),
                        ReadF32(bytes, at + 24)))),
                unchecked((int)ReadU32(bytes, at + 28))));
            at += 32;
        }

        byte[] trailingBytes = at <= bytes.Length ? bytes[at..] : (byte[])DefaultTrailingBytes.Clone();
        return new Engine_GmdSkeleton(
            names, parents, locals, attachments.ToArray(),
            headerBytes, nodeScales, nodePads, trailingBytes);
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

            local = attachment.Local;
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
    /// The NODE a stated dummy hangs from, without composing anything — the answer for a model
    /// that is being posed, where the node's ancestors are live bones rather than bind-pose
    /// transforms.
    ///
    /// <paramref name="index"/> is the guest's dummy index: `sub_822650A0` lower-bounds the
    /// name's CRC in the sorted table and then steps <c>index</c> records forward, so it selects
    /// WITHIN the run of equally-named dummies rather than among all of them.
    ///
    /// Returns -1 in <paramref name="node"/> for a dummy that hangs from the model root.
    /// </summary>
    public bool TryResolveNode(string dummy, int index, out int node) =>
        TryResolveEntry(dummy, index, out node, out _);

    /// <summary>
    /// <see cref="TryResolveNode"/>, with the dummy's own transform RELATIVE TO THAT NODE — what the
    /// guest's runtime entry holds (`sub_82265100`: quaternion at +0x20, translation at +0x30) and
    /// composes under the node's live pose — or the identity when the dummy is a node itself.
    /// </summary>
    /// <remarks>
    /// THE FILE'S ATTACHMENT TRANSFORM IS IN MODEL SPACE. Composed under the node as if it were
    /// relative, every sheathed weapon landed 1.2-1.7 m from where the guest put it — the height
    /// of the back dummy above the model root, counted twice. Relative = inverse(node's bind in
    /// model space) x model-space local. <see cref="TryResolve"/> keeps the model-space value,
    /// which is what a static prop composes directly onto its placement.
    /// </remarks>
    public bool TryResolveEntry(string dummy, int index, out int node, out Transform3D local)
    {
        node = -1;
        local = Transform3D.Identity;
        if (dummy.Length == 0)
        {
            return false;
        }

        int seen = 0;
        foreach (GmdAttachment attachment in Attachments)
        {
            if (!attachment.Name.Equals(dummy, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (seen++ < index)
            {
                continue;
            }

            node = attachment.Node;
            local = node >= 0 && node < Locals.Length
                ? Compose(node).AffineInverse() * attachment.Local
                : attachment.Local;
            return true;
        }

        // The dummy IS a node. The guest has this case too: with no `.gmd` record it looks the
        // name up in the skeleton's own CRC map, and the dummy is then coincident with its bone.
        int named = Array.FindIndex(
            Names, n => n.Equals(dummy, StringComparison.OrdinalIgnoreCase));
        if (named < 0)
        {
            return false;
        }

        node = named;
        return true;
    }

    /// <summary>
    /// A node's transform in model space (Godot Y-up) — its own local with every ancestor's applied outside
    /// it, which is what makes a dummy hanging off bone 4 land at the lamp's head rather than at
    /// its foot.
    /// </summary>
    public Transform3D ComposeNode(int node) => Compose(node);

    /// <summary>
    /// Computes an attachment's full model-space transform (in Godot Y-up space), composing its parent
    /// node chain when <see cref="GmdAttachment.Node"/> is non-negative.
    /// </summary>
    public Transform3D ComposeAttachment(int attachmentIndex)
    {
        if (attachmentIndex < 0 || attachmentIndex >= Attachments.Length)
        {
            return Transform3D.Identity;
        }

        return ComposeAttachment(Attachments[attachmentIndex]);
    }

    /// <summary>
    /// Computes an attachment's full model-space transform (in Godot Y-up space), composing its parent
    /// node chain when <see cref="GmdAttachment.Node"/> is non-negative.
    /// </summary>
    public Transform3D ComposeAttachment(GmdAttachment att)
    {
        return att.Node >= 0 && att.Node < Locals.Length
            ? Compose(att.Node) * att.Local
            : att.Local;
    }

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

    /// <summary>Converts a transform from raw GameMesh Z-up space into Godot / glTF Y-up space.</summary>
    public static Transform3D ToGodotSpace(Transform3D game) => ToGodot(game);

    /// <summary>Converts a transform from Godot / glTF Y-up space back into raw GameMesh Z-up space.</summary>
    public static Transform3D ToGameSpace(Transform3D godot)
    {
        Basis inv = ZUpToYUp.Inverse();
        return new Transform3D(inv * godot.Basis * ZUpToYUp, inv * godot.Origin);
    }

    private static Transform3D ToGodot(Transform3D game) =>
        new(ZUpToYUp * game.Basis * ZUpToYUp.Inverse(), ZUpToYUp * game.Origin);

    private static bool TryFindAttachmentCount(
        byte[] bytes, int from, out uint count, out int after)
    {
        count = 0;
        after = from;

        for (int probe = 0; probe <= 2; probe++)
        {
            int at = from + (probe * 4);
            if (at + 5 > bytes.Length)
            {
                return false;
            }

            uint value = ReadU32(bytes, at);
            byte next = bytes[at + 4];

            if (value >= 1 && value <= MaxAttachments && next >= 0x20 && next < 0x7F)
            {
                count = value;
                after = at + 4;
                return true;
            }
        }

        return false;
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
