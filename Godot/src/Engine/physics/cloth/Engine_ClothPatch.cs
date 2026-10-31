using System;
using System.Collections.Generic;
using Godot;

namespace Refii.Physics;

/// <summary>
/// The cloth's own drawable, one surface per piece, built once from the cloth block's own
/// vertex set, triangle list and UVs — what the guest's cloth draw binds — with the material
/// of the record surface it stands in for. After that only the vertex buffer moves: positions,
/// normals and tangents are packed off the main thread into Godot's own surface layout and
/// written in place, never rebuilt.
/// </summary>
public sealed class ClothPatch
{
    private sealed class Surface
    {
        public int VertexCount { get; init; }

        /// <summary>float3 per vertex, the first block of the vertex buffer.</summary>
        public byte[] PositionBytes { get; init; } = Array.Empty<byte>();

        /// <summary>Octahedral normal and tangent, two u16 each per vertex, the second block.</summary>
        public byte[] NormalTangentBytes { get; init; } = Array.Empty<byte>();
    }

    private readonly List<Surface> _surfaces;

    private readonly ArrayMesh _mesh;

    public ArrayMesh Built => _mesh;

    /// <summary>The source surfaces every piece stands in for, by index bit.</summary>
    public UInt128 DrivenMask { get; }

    public int SurfaceCount => _surfaces.Count;

    private ClothPatch(ArrayMesh mesh, List<Surface> surfaces, UInt128 mask)
    {
        _mesh = mesh;
        _surfaces = surfaces;
        DrivenMask = mask;
    }

    public static ClothPatch? Capture(IReadOnlyList<ClothPiece> pieces)
    {
        var surfaces = new List<Surface>();
        var mesh = new ArrayMesh();
        UInt128 mask = UInt128.Zero;

        foreach (ClothPiece piece in pieces)
        {
            ClothAsset asset = piece.Asset;
            int count = asset.VertexCount;
            if (count == 0 || asset.Indices.Length < 3)
            {
                continue;
            }

            var positions = new Vector3[count];
            var normals = new Vector3[count];
            var tangents = new float[count * 4];
            for (int v = 0; v < count; v++)
            {
                PhysVec4 p = asset.BindPos[v];
                positions[v] = new Vector3(p.X, p.Y, p.Z);
                normals[v] = Vector3.Up;
                tangents[v * 4] = 1.0f;
                tangents[(v * 4) + 3] = 1.0f;
            }

            var uvs = new Vector2[count];
            for (int v = 0; v < count && (v * 2) + 1 < asset.Uvs.Length; v++)
            {
                uvs[v] = new Vector2(asset.Uvs[v * 2], asset.Uvs[(v * 2) + 1]);
            }

            int triangles = asset.Indices.Length - (asset.Indices.Length % 3);
            var indices = new int[triangles];
            bool valid = true;
            for (int i = 0; i < triangles; i++)
            {
                int index = asset.Indices[i];
                if (index >= count)
                {
                    valid = false;
                    break;
                }

                indices[i] = index;
            }

            if (!valid)
            {
                continue;
            }

            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = positions;
            arrays[(int)Mesh.ArrayType.Normal] = normals;
            arrays[(int)Mesh.ArrayType.Tangent] = tangents;
            arrays[(int)Mesh.ArrayType.TexUV] = uvs;
            arrays[(int)Mesh.ArrayType.Index] = indices;

            // No compression flag: positions stay float3 and the normal/tangent block is two
            // u16 pairs per vertex, which is the layout Pack writes.
            Refii.Core.Engine_ArrayMeshes.AddSurface(mesh, Mesh.PrimitiveType.Triangles, arrays);
            int added = mesh.GetSurfaceCount() - 1;
            if (piece.Material is not null)
            {
                mesh.SurfaceSetMaterial(added, piece.Material);
            }

            surfaces.Add(new Surface
            {
                VertexCount = count,
                PositionBytes = new byte[count * 12],
                NormalTangentBytes = new byte[count * 8],
            });
            mask |= piece.HiddenSurfaces;
        }

        return surfaces.Count == 0 ? null : new ClothPatch(mesh, surfaces, mask);
    }

    /// <summary>
    /// Packs one piece's drawn pose into the surface's vertex-buffer bytes. Thread-safe: it
    /// touches only this patch's own byte arrays. The layout is the renderer's for an
    /// uncompressed surface: all positions as float3, then per vertex the octahedral normal and
    /// tangent as u16 pairs (<c>RenderingServer::_surface_set_data</c>).
    /// </summary>
    public void Pack(int piece, PhysVec4[] positions, PhysVec4[] normals, PhysVec4[] tangents)
    {
        if (piece < 0 || piece >= _surfaces.Count)
        {
            return;
        }

        Surface surface = _surfaces[piece];
        int count = Math.Min(surface.VertexCount, positions.Length);
        Span<byte> pos = surface.PositionBytes;
        Span<byte> nt = surface.NormalTangentBytes;

        for (int v = 0; v < count; v++)
        {
            PhysVec4 p = positions[v];
            BitConverter.TryWriteBytes(pos.Slice(v * 12, 4), p.X);
            BitConverter.TryWriteBytes(pos.Slice((v * 12) + 4, 4), p.Y);
            BitConverter.TryWriteBytes(pos.Slice((v * 12) + 8, 4), p.Z);

            PhysVec4 n = v < normals.Length ? normals[v] : PhysVec4.Zero;
            if (n.X == 0.0f && n.Y == 0.0f && n.Z == 0.0f)
            {
                n = new PhysVec4(0.0f, 1.0f, 0.0f, 0.0f);
            }

            OctahedronEncode(n, out float ox, out float oy);
            BitConverter.TryWriteBytes(nt.Slice(v * 8, 2), ToU16(ox));
            BitConverter.TryWriteBytes(nt.Slice((v * 8) + 2, 2), ToU16(oy));

            PhysVec4 t = v < tangents.Length ? tangents[v] : new PhysVec4(1.0f, 0.0f, 0.0f, 1.0f);
            OctahedronTangentEncode(t, t.W, out float tx, out float ty);
            ushort ux = ToU16(tx);
            ushort uy = ToU16(ty);
            if (ux == 0 && uy == 65535)
            {
                ux = 65535;
            }

            BitConverter.TryWriteBytes(nt.Slice((v * 8) + 4, 2), ux);
            BitConverter.TryWriteBytes(nt.Slice((v * 8) + 6, 2), uy);
        }
    }

    /// <summary>Writes the packed bytes into the surface. Main thread only.</summary>
    public void Upload()
    {
        for (int s = 0; s < _surfaces.Count; s++)
        {
            Surface surface = _surfaces[s];
            _mesh.SurfaceUpdateVertexRegion(s, 0, surface.PositionBytes);
            _mesh.SurfaceUpdateVertexRegion(s, surface.PositionBytes.Length, surface.NormalTangentBytes);
        }
    }

    private static ushort ToU16(float unit) =>
        (ushort)Math.Clamp(unit * 65535.0f, 0.0f, 65535.0f);

    /// <summary><c>Vector3::octahedron_encode</c>.</summary>
    private static void OctahedronEncode(in PhysVec4 v, out float x, out float y)
    {
        float sum = MathF.Abs(v.X) + MathF.Abs(v.Y) + MathF.Abs(v.Z);
        float nx = v.X / sum;
        float ny = v.Y / sum;
        float nz = v.Z / sum;
        if (nz >= 0.0f)
        {
            x = nx;
            y = ny;
        }
        else
        {
            x = (1.0f - MathF.Abs(ny)) * (nx >= 0.0f ? 1.0f : -1.0f);
            y = (1.0f - MathF.Abs(nx)) * (ny >= 0.0f ? 1.0f : -1.0f);
        }

        x = (x * 0.5f) + 0.5f;
        y = (y * 0.5f) + 0.5f;
    }

    /// <summary><c>Vector3::octahedron_tangent_encode</c>: the handedness rides in y.</summary>
    private static void OctahedronTangentEncode(in PhysVec4 t, float sign, out float x, out float y)
    {
        const float bias = 1.0f / 32767.0f;
        OctahedronEncode(t, out x, out y);
        y = MathF.Max(y, bias);
        y = (y * 0.5f) + 0.5f;
        y = sign >= 0.0f ? y : 1.0f - y;
    }
}
