using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using Godot;

namespace Refii.Level;

public sealed class KynapseGraphData
{
    public uint Version { get; init; }
    public uint Flags { get; init; }
    public Vector3[] Vertices { get; init; } = Array.Empty<Vector3>();
    public uint[] VertexTags { get; init; } = Array.Empty<uint>();
    public (uint StartIndex, uint EndIndex)[] Edges { get; init; } = Array.Empty<(uint, uint)>();
}

/// <summary>
/// Parses Kynogon <c>.pdl</c> AI spatial MoveGraph files (<c>worlds/albion/&lt;region&gt;/&lt;scenario&gt;/pathdata/&lt;scenario&gt;.pdl</c>)
/// verified against <c>sub_82E90E98</c> (<c>GraphCell::ReadFromStream</c>) in <c>default.xex</c>.
/// </summary>
public static class Engine_KynapseGraph
{
    public const string NodeName = "KynapseNavGraph";
    public const string MagicHeader = "Kynogon Spatial graph";

    public static bool TryParse(byte[]? pdlBytes, out KynapseGraphData graph, out string error)
    {
        graph = new KynapseGraphData();
        error = string.Empty;

        if (pdlBytes is null || pdlBytes.Length < 48)
        {
            error = "PDL buffer is null or too short.";
            return false;
        }

        ReadOnlySpan<byte> span = pdlBytes;
        string magic = Encoding.ASCII.GetString(span.Slice(0, 21));
        if (!string.Equals(magic, MagicHeader, StringComparison.Ordinal))
        {
            error = $"Invalid PDL magic header: '{magic}'.";
            return false;
        }

        int pos = 21;
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(pos, 4));
        pos += 4;

        if (version == 3)
        {
            // sub_82E90E98 skips 11 bytes of 0xAA padding when version == 3
            pos += 11;
        }
        else if (version != 2)
        {
            error = $"Unsupported PDL version {version}.";
            return false;
        }

        if (pos + 20 > span.Length)
        {
            error = "Truncated PDL header.";
            return false;
        }

        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(pos, 4));
        uint maxVertices = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(pos + 12, 4));
        uint numVertices = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(pos + 16, 4));
        pos += 20;

        if (numVertices > 500_000 || maxVertices > 500_000)
        {
            error = $"Unreasonable PDL vertex count ({numVertices}/{maxVertices}).";
            return false;
        }

        long vertexBytes = (long)numVertices * 20L;
        if (pos + vertexBytes > span.Length)
        {
            error = "Truncated PDL vertex array.";
            return false;
        }

        var vertices = new Vector3[numVertices];
        var tags = new uint[numVertices];
        for (int i = 0; i < numVertices; i++)
        {
            // Each vertex is 20 bytes: u32 vid, f32 x, f32 y, f32 z, u32 tag
            float gx = BinaryPrimitives.ReadSingleLittleEndian(span.Slice(pos + 4, 4));
            float gy = BinaryPrimitives.ReadSingleLittleEndian(span.Slice(pos + 8, 4));
            float gz = BinaryPrimitives.ReadSingleLittleEndian(span.Slice(pos + 12, 4));
            uint tag = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(pos + 16, 4));

            // Convert game Z-up (X, Y, Z) to region-local Godot (X, Z, Y)
            vertices[i] = new Vector3(gx, gz, gy);
            tags[i] = tag;
            pos += 20;
        }

        // Optional per-vertex arrays gated by flags (sub_82E90E98)
        if ((flags & 0x01u) != 0)
        {
            pos += (int)(4 * maxVertices);
        }

        if ((flags & 0x10u) != 0)
        {
            pos += (int)(4 * maxVertices);
        }

        if ((flags & 0x08u) != 0)
        {
            pos += (int)(4 * maxVertices);
        }

        var edges = Array.Empty<(uint, uint)>();
        if (pos + 12 <= span.Length)
        {
            uint maxEdges = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(pos + 4, 4));
            uint numEdges = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(pos + 8, 4));
            pos += 12;

            long edgeHeaderBytes = (long)maxEdges * 8L;
            long edgeEndpointsBytes = (long)numEdges * 8L;
            if (numEdges <= 1_000_000 && maxEdges <= 1_000_000 &&
                pos + edgeHeaderBytes + edgeEndpointsBytes <= span.Length)
            {
                pos += (int)edgeHeaderBytes;
                ReadOnlySpan<byte> vStarts = span.Slice(pos, (int)(numEdges * 4));
                ReadOnlySpan<byte> vEnds = span.Slice(pos + (int)(numEdges * 4), (int)(numEdges * 4));

                var validEdges = new List<(uint, uint)>((int)numEdges);
                for (int i = 0; i < numEdges; i++)
                {
                    uint u = BinaryPrimitives.ReadUInt32LittleEndian(vStarts.Slice(i * 4, 4));
                    uint v = BinaryPrimitives.ReadUInt32LittleEndian(vEnds.Slice(i * 4, 4));
                    if (u < numVertices && v < numVertices && u != v)
                    {
                        validEdges.Add((u, v));
                    }
                }

                edges = validEdges.ToArray();
            }
        }

        graph = new KynapseGraphData
        {
            Version = version,
            Flags = flags,
            Vertices = vertices,
            VertexTags = tags,
            Edges = edges,
        };
        return true;
    }

    public static MeshInstance3D? Build(LevelEntry level, Func<string, byte[]?> read, string gdbPath)
    {
        byte[]? pdlBytes = TryReadPdl(level, read, gdbPath, out string pdlPath);
        if (pdlBytes is null || !TryParse(pdlBytes, out KynapseGraphData graph, out _))
        {
            return null;
        }

        if (graph.Vertices.Length == 0 || graph.Edges.Length == 0)
        {
            return null;
        }

        var lineVerts = new Vector3[graph.Edges.Length * 2];
        var lift = new Vector3(0f, 0.12f, 0f);
        for (int i = 0; i < graph.Edges.Length; i++)
        {
            (uint u, uint v) = graph.Edges[i];
            lineVerts[i * 2] = graph.Vertices[u] + lift;
            lineVerts[(i * 2) + 1] = graph.Vertices[v] + lift;
        }

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = lineVerts;

        var mesh = new ArrayMesh();
        Refii.Core.Engine_ArrayMeshes.AddSurface(mesh, Mesh.PrimitiveType.Lines, arrays);

        var mat = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.18f, 0.92f, 0.78f, 0.80f),
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        };

        var node = new MeshInstance3D
        {
            Name = NodeName,
            Mesh = mesh,
            MaterialOverride = mat,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            GIMode = GeometryInstance3D.GIModeEnum.Disabled,
            Visible = false, // Hidden by default so the 3D view isn't covered in nav lines until toggled on in the toolbar
        };
        node.SetMeta("refii_pdl_path", pdlPath);
        node.SetMeta("refii_pdl_vertices", graph.Vertices.Length);
        node.SetMeta("refii_pdl_edges", graph.Edges.Length);
        return node;
    }

    private static byte[]? TryReadPdl(
        LevelEntry level,
        Func<string, byte[]?> read,
        string gdbPath,
        out string pdlPath)
    {
        string normalized = gdbPath.Replace('\\', '/');
        int slash = normalized.LastIndexOf('/');
        string dir = slash >= 0 ? normalized[..slash] : string.Empty;
        string stem = slash >= 0 ? normalized[(slash + 1)..] : normalized;
        if (stem.EndsWith(".gdb", StringComparison.OrdinalIgnoreCase))
        {
            stem = stem[..^4];
        }

        string[] candidates =
        {
            $"{dir}/pathdata/{stem}.pdl",
            $"data/{dir}/pathdata/{stem}.pdl",
            $"worlds/albion/{level.LevelPath}/{stem}/pathdata/{stem}.pdl",
            $"data/worlds/albion/{level.LevelPath}/{stem}/pathdata/{stem}.pdl",
        };

        foreach (string candidate in candidates)
        {
            byte[]? bytes = read(candidate);
            if (bytes is not null && bytes.Length > 0)
            {
                pdlPath = candidate;
                return bytes;
            }
        }

        pdlPath = string.Empty;
        return null;
    }
}
