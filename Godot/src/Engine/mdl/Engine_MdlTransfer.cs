using System;
using System.Collections.Generic;

namespace Refii.Models;

/// <summary>
/// A model's precomputed radiance transfer, expanded per vertex.
/// </summary>
/// <remarks>
/// The guest lights a dynamic object's ambient by D3DX compressed PRT, not by an SH lookup
/// along the normal. The header (<see cref="MdlInfo.ShDescriptor"/>) is [order N, channels C,
/// clusters K, PCA vectors M, FLOAT4 vectors per vertex]; <see cref="MdlInfo.ShProbeData"/>
/// holds, per cluster, a MEAN then M BASIS vectors, each C channels of N*N coefficients
/// (`sub_8225DC80`). A vertex carries its cluster in POSITION.w — the offset of the cluster's
/// constant block in vec4s, (M*C+4)/4 apart — and its M PCA weights T in the FLOAT4 stream that
/// follows the primitive's indices (`sub_82B26460`). The vertex shader computes
/// <c>colour_c = mean_c.L_c + sum_j T_j * (basis_j,c.L_c)</c> against the entity's probe L.
///
/// That is linear in L, so the compression is undone here once:
/// <c>t_c = mean_c + sum_j T_j * basis_j,c</c>, four coefficients per channel per vertex, and
/// the shader is left with <c>dot(t_c, L_c)</c>. Only order 2 fits a vec4 and is accepted.
/// </remarks>
public static class Engine_MdlTransfer
{
    /// <summary>Coefficients per channel per vertex in the expanded form.</summary>
    public const int Coefficients = 4;

    /// <summary>
    /// The surface format a set of arrays needs for its transfer channels, or 0 when it has
    /// none. EVERY `AddSurfaceFromArrays` over arrays that may have come from a model passes
    /// this: without it Godot reads CUSTOM0..2 as 8-bit and refuses the float data, and a
    /// composed character is rebuilt from `SurfaceGetArrays` more than once.
    /// </summary>
    public static Godot.Mesh.ArrayFormat FormatOf(Godot.Collections.Array arrays)
    {
        // Every custom channel that carries floats is four floats a vertex: the expanded
        // transfer in CUSTOM0..2, or the raw weights in CUSTOM0 and the cluster in CUSTOM3.
        long rgba = (long)Godot.Mesh.ArrayCustomFormat.RgbaFloat;
        int[] shifts =
        {
            (int)Godot.Mesh.ArrayFormat.FormatCustom0Shift, (int)Godot.Mesh.ArrayFormat.FormatCustom1Shift,
            (int)Godot.Mesh.ArrayFormat.FormatCustom2Shift, (int)Godot.Mesh.ArrayFormat.FormatCustom3Shift,
        };
        long format = 0;
        for (int i = 0; i < 4; i++)
        {
            int slot = (int)Godot.Mesh.ArrayType.Custom0 + i;
            if (arrays.Count > slot && arrays[slot].VariantType == Godot.Variant.Type.PackedFloat32Array)
            {
                format |= rgba << shifts[i];
            }
        }
        return (Godot.Mesh.ArrayFormat)format;
    }

    /// <summary>Mesh metadata carrying the transfer header and table for the classic shaders.</summary>
    public static readonly Godot.StringName DescriptorMeta = "classic_prt_descriptor";
    public static readonly Godot.StringName TableMeta = "classic_prt_table";
    /// <summary>A composite's per-surface transfer headers and tables (one entry per surface, an
    /// empty array where the part ships none): each part keeps its own model's.</summary>
    public static readonly Godot.StringName SurfaceDescriptorsMeta = "classic_prt_surface_descriptors";
    public static readonly Godot.StringName SurfaceTablesMeta = "classic_prt_surface_tables";

    /// <summary>
    /// The transfer as the title's vertex shader takes it: each vertex's PCA weights (four
    /// floats, zero-padded) and its cluster offset in vec4s (what the file carries in the
    /// position's w), for the emitted vertices named by <paramref name="sourceVertices"/>.
    /// The title's AMB2 shader indexes g_PRTConstants by that offset and dots the weights with
    /// the cluster's basis rows. False when the model carries no usable transfer.
    /// </summary>
    public static bool TryRaw(
        byte[] data, MdlInfo info, int[] sourceVertices, out float[] weights, out float[] clusterOffsets)
    {
        weights = clusterOffsets = Array.Empty<float>();
        data = info.Body ?? data;
        uint[] d = info.ShDescriptor;
        if (d.Length < 5 || sourceVertices.Length == 0)
        {
            return false;
        }
        int order = (int)d[0], channels = (int)d[1], clusters = (int)d[2], pca = (int)d[3], vectors = (int)d[4];
        if (order * order != Coefficients || channels != 3 || clusters < 1 || pca < 1 || pca > 4 ||
            vectors < 1 || pca > vectors * 4)
        {
            return false;
        }
        IReadOnlyList<MdlMeshBuffer> buffers = info.MeshBuffers;
        var starts = new int[buffers.Count];
        int running = 0;
        for (int b = 0; b < buffers.Count; b++)
        {
            starts[b] = running;
            running += (int)buffers[b].VertexCount;
        }
        int count = sourceVertices.Length;
        var w4 = new float[count * 4];
        var offsets = new float[count * 4];
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
            int stride = mb.IsEngineSkinned ? 28 : Engine_MdlTreeStreams.IsTreeLayout(mb) ? 36 : mb.IsEngineRigid ? 20 : 0;
            if (stride == 0 || mb.SharesLod0Vertices)
            {
                return false;
            }
            int local = source - starts[at];
            int vertex = mb.VertexOffset + local * stride;
            int weightsAt = mb.FaceOffset + (int)(mb.FaceCount * 2) + local * 16 * vectors;
            if (vertex + 8 > data.Length || weightsAt + pca * 4 > data.Length)
            {
                return false;
            }
            offsets[v * 4] = MdlReader.HalfToFloat((ushort)((data[vertex + 6] << 8) | data[vertex + 7]));
            for (int j = 0; j < pca; j++)
            {
                w4[v * 4 + j] = BitConverter.Int32BitsToSingle((int)MdlReader.ReadU32BE(data, weightsAt + j * 4));
            }
        }
        weights = w4;
        clusterOffsets = offsets;
        return true;
    }

    /// <summary>
    /// g_PRTConstants for one placement: per cluster, (mean . L) per channel, then the four
    /// basis dots per channel — the layout `sub_8225DC80` writes and the AMB2 vertex shader
    /// reads at the vertex's cluster offset. <paramref name="l"/> is the placement's L1
    /// harmonic environment, four coefficients per channel. Twelve registers; clusters past
    /// the twelfth register are left zero, as the shader's own bound leaves them.
    /// </summary>
    public static Godot.Collections.Array<Godot.Vector4> Constants(
        uint[] descriptor, float[] table, Godot.Vector4 lr, Godot.Vector4 lg, Godot.Vector4 lb)
    {
        var result = new Godot.Collections.Array<Godot.Vector4>();
        for (int i = 0; i < 12; i++)
        {
            result.Add(Godot.Vector4.Zero);
        }
        if (descriptor.Length < 5)
        {
            return result;
        }
        int channels = (int)descriptor[1], clusters = (int)descriptor[2], pca = (int)descriptor[3];
        int coefs = Coefficients;
        int clusterFloats = (pca + 1) * channels * coefs;
        int blockVec4 = (pca * channels + 4) / 4;
        Godot.Vector4[] l = { lr, lg, lb };
        for (int k = 0; k < clusters; k++)
        {
            int baseRegister = k * blockVec4;
            if (baseRegister + 3 >= 12 || (k + 1) * clusterFloats > table.Length)
            {
                break;
            }
            int mean = k * clusterFloats;
            var m = new Godot.Vector4(
                Dot(table, mean, lr), Dot(table, mean + coefs, lg), Dot(table, mean + 2 * coefs, lb), 0f);
            result[baseRegister] = m;
            for (int c = 0; c < 3; c++)
            {
                var row = Godot.Vector4.Zero;
                for (int j = 0; j < pca && j < 4; j++)
                {
                    int basis = mean + (j + 1) * channels * coefs + c * coefs;
                    row[j] = Dot(table, basis, l[c]);
                }
                result[baseRegister + 1 + c] = row;
            }
        }
        return result;
    }

    private static float Dot(float[] table, int at, Godot.Vector4 l) =>
        table[at] * l.X + table[at + 1] * l.Y + table[at + 2] * l.Z + table[at + 3] * l.W;

    /// <summary>
    /// The expanded transfer for the emitted vertices named by <paramref name="sourceVertices"/>
    /// (LOD 0's file numbering, -1 for none), four floats per vertex per channel. False when
    /// the model carries no usable transfer or any vertex cannot be resolved.
    /// </summary>
    public static bool TryExpand(
        byte[] data, MdlInfo info, int[] sourceVertices,
        out float[] red, out float[] green, out float[] blue)
    {
        red = green = blue = Array.Empty<float>();
        data = info.Body ?? data;

        uint[] d = info.ShDescriptor;
        if (d.Length < 5 || sourceVertices.Length == 0)
        {
            return false;
        }

        int order = (int)d[0], channels = (int)d[1], clusters = (int)d[2], pca = (int)d[3], vectors = (int)d[4];
        int coefs = order * order;
        if (coefs != Coefficients || channels != 3 || clusters < 1 || pca < 1 ||
            vectors < 1 || pca > vectors * 4)
        {
            return false;
        }

        int clusterFloats = (pca + 1) * channels * coefs;
        if (info.ShProbeData.Length < clusters * clusterFloats)
        {
            return false;
        }

        int blockVec4 = (pca * channels + 4) / 4;
        float[] table = info.ShProbeData;

        // Where each LOD 0 buffer starts in the file's running vertex numbering.
        IReadOnlyList<MdlMeshBuffer> buffers = info.MeshBuffers;
        var starts = new int[buffers.Count];
        int running = 0;
        for (int b = 0; b < buffers.Count; b++)
        {
            starts[b] = running;
            running += (int)buffers[b].VertexCount;
        }

        int count = sourceVertices.Length;
        var r = new float[count * Coefficients];
        var g = new float[count * Coefficients];
        var bl = new float[count * Coefficients];
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
            int stride = mb.IsEngineSkinned ? 28 : Engine_MdlTreeStreams.IsTreeLayout(mb) ? 36 : mb.IsEngineRigid ? 20 : 0;
            if (stride == 0 || mb.SharesLod0Vertices)
            {
                return false;
            }

            int local = source - starts[at];
            int vertex = mb.VertexOffset + local * stride;
            int weights = mb.FaceOffset + (int)(mb.FaceCount * 2) + local * 16 * vectors;
            if (vertex + 8 > data.Length || weights + pca * 4 > data.Length)
            {
                return false;
            }

            float w = MdlReader.HalfToFloat((ushort)((data[vertex + 6] << 8) | data[vertex + 7]));
            int cluster = (int)MathF.Round(w) / blockVec4;
            if (cluster < 0 || cluster >= clusters)
            {
                return false;
            }

            int mean = cluster * clusterFloats;
            for (int k = 0; k < coefs; k++)
            {
                r[v * Coefficients + k] = table[mean + k];
                g[v * Coefficients + k] = table[mean + coefs + k];
                bl[v * Coefficients + k] = table[mean + 2 * coefs + k];
            }

            for (int j = 0; j < pca; j++)
            {
                float t = BitConverter.Int32BitsToSingle((int)MdlReader.ReadU32BE(data, weights + j * 4));
                int basis = mean + (j + 1) * channels * coefs;
                for (int k = 0; k < coefs; k++)
                {
                    r[v * Coefficients + k] += t * table[basis + k];
                    g[v * Coefficients + k] += t * table[basis + coefs + k];
                    bl[v * Coefficients + k] += t * table[basis + 2 * coefs + k];
                }
            }
        }

        red = r;
        green = g;
        blue = bl;
        return true;
    }
}
