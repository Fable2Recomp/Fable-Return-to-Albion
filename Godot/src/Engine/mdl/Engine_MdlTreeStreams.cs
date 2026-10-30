using System;
using System.Collections.Generic;

namespace Refii.Models;

/// <summary>
/// A tree part's wind lanes, per emitted vertex, for the title's tree vertex shaders.
/// </summary>
/// <remarks>
/// The tree declaration (`sub_82A70510`, types from the table at 0x83316CC0) is the rigid layout
/// with three elements appended to the 36-byte stream: POSITION1 FLOAT16_4 (0x001A2360) at +20,
/// COLOR1 and COLOR2 D3DCOLOR (0x00182886) at +28 and +32. The shaders read POSITION1's xyz (the
/// point the vertex sways about, in the file's axes) and both colours whole; a D3DCOLOR is the
/// big-endian ARGB word, fetched as (R, G, B, A) / 255. The mesh builder carries POSITION1 in
/// CUSTOM3.yzw — CUSTOM3.x is an AMB2 vertex's cluster offset — and the colours in CUSTOM1 and
/// CUSTOM2, where the translator maps them for the tree family.
/// </remarks>
public static class Engine_MdlTreeStreams
{
    private const int Stride = 36;

    /// <summary>
    /// The three lanes for the vertices named by <paramref name="sourceVertices"/>, four floats a
    /// vertex each (<paramref name="pivots"/> with x left for the cluster offset). False when any
    /// of them is not in a tree part.
    /// </summary>
    public static bool TryRead(
        byte[] data, MdlInfo info, int[] sourceVertices,
        out float[] pivots, out float[] colour1, out float[] colour2)
    {
        pivots = colour1 = colour2 = Array.Empty<float>();
        data = info.Body ?? data;
        IReadOnlyList<MdlMeshBuffer> buffers = info.MeshBuffers;
        if (sourceVertices.Length == 0 || buffers.Count == 0)
        {
            return false;
        }
        var starts = new int[buffers.Count];
        int running = 0;
        bool anyTree = false;
        for (int b = 0; b < buffers.Count; b++)
        {
            starts[b] = running;
            running += (int)buffers[b].VertexCount;
            anyTree |= IsTreeLayout(buffers[b]);
        }
        if (!anyTree)
        {
            return false;
        }

        int count = sourceVertices.Length;
        var p = new float[count * 4];
        var c1 = new float[count * 4];
        var c2 = new float[count * 4];
        int at = 0;
        for (int v = 0; v < count; v++)
        {
            int source = sourceVertices[v];
            if (source < 0 || source >= running)
            {
                return false;
            }
            while (at + 1 < buffers.Count && source >= starts[at + 1])
            {
                at++;
            }
            while (at > 0 && source < starts[at])
            {
                at--;
            }
            MdlMeshBuffer mb = buffers[at];
            if (!IsTreeLayout(mb))
            {
                return false;
            }
            int vertex = mb.VertexOffset + (source - starts[at]) * Stride;
            if (vertex + Stride > data.Length)
            {
                return false;
            }
            p[v * 4 + 1] = Half(data, vertex + 20);
            p[v * 4 + 2] = Half(data, vertex + 22);
            p[v * 4 + 3] = Half(data, vertex + 24);
            Colour(data, vertex + 28, c1, v * 4);
            Colour(data, vertex + 32, c2, v * 4);
        }
        pivots = p;
        colour1 = c1;
        colour2 = c2;
        return true;
    }

    /// <summary>
    /// The 36-byte tree stream: an engine tree part, or a foliage-path tree part, which ships the
    /// same seven elements at the same offsets (Engine_MdlFoliage reads its position, normal and
    /// UV from them).
    /// </summary>
    public static bool IsTreeLayout(MdlMeshBuffer mb) =>
        mb.IsEngineTree || (mb.IsFoliagePath && mb.FoliageVertexStride == Stride);

    private static float Half(byte[] data, int at) =>
        MdlReader.HalfToFloat((ushort)((data[at] << 8) | data[at + 1]));

    private static void Colour(byte[] data, int at, float[] into, int to)
    {
        into[to] = data[at + 1] / 255f;
        into[to + 1] = data[at + 2] / 255f;
        into[to + 2] = data[at + 3] / 255f;
        into[to + 3] = data[at] / 255f;
    }
}
