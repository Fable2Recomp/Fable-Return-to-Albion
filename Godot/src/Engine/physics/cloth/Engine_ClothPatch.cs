using System;
using System.Collections.Generic;
using Godot;

namespace Refii.Physics;

public sealed class ClothPatch
{
    private sealed class Surface
    {
        public Godot.Collections.Array Arrays { get; init; } = new();

        public Godot.Collections.Dictionary? Lods { get; init; }

        public Material? Material { get; init; }

        public string Name { get; init; } = string.Empty;

        public int VertexBase { get; init; }

        public int VertexCount { get; init; }

        public Vector3[] RestPositions { get; init; } = Array.Empty<Vector3>();

        public Vector3[] Positions { get; set; } = Array.Empty<Vector3>();

        public Vector3[] Normals { get; set; } = Array.Empty<Vector3>();

        public int[] Indices { get; init; } = Array.Empty<int>();

        public bool Driven { get; set; }
    }

    private readonly List<Surface> _surfaces = new();

    private readonly ArrayMesh _mesh;

    public ArrayMesh Built => _mesh;

    public int VertexCount { get; }

    private ClothPatch(ArrayMesh mesh, List<Surface> surfaces, int vertexCount)
    {
        _mesh = mesh;
        _surfaces = surfaces;
        VertexCount = vertexCount;
    }

    public static ClothPatch? Capture(ArrayMesh source)
    {
        var surfaces = new List<Surface>();
        int total = 0;

        for (int s = 0; s < source.GetSurfaceCount(); s++)
        {
            Godot.Collections.Array arrays = Refii.Models.Engine_MdlSurfaceArrays.Of(source, s);
            Vector3[] positions = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            Vector3[] normals = arrays[(int)Mesh.ArrayType.Normal].AsVector3Array();
            int[] indices = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();

            if (positions.Length == 0)
            {
                return null;
            }

            surfaces.Add(new Surface
            {
                Arrays = arrays,
                Lods = Refii.Models.Engine_MdlMesh.SurfaceLods(source, s),
                Material = source.SurfaceGetMaterial(s),
                Name = source.SurfaceGetName(s),
                VertexBase = total,
                VertexCount = positions.Length,
                RestPositions = (Vector3[])positions.Clone(),
                Positions = (Vector3[])positions.Clone(),
                Normals = normals.Length == positions.Length
                    ? (Vector3[])normals.Clone()
                    : new Vector3[positions.Length],
                Indices = indices,
            });

            total += positions.Length;
        }

        return surfaces.Count == 0 ? null : new ClothPatch(new ArrayMesh(), surfaces, total);
    }

    public void MarkDriven(int[] clothVertexOf)
    {
        foreach (Surface surface in _surfaces)
        {
            for (int v = 0; v < surface.VertexCount; v++)
            {
                int flat = surface.VertexBase + v;
                if (flat < clothVertexOf.Length && clothVertexOf[flat] >= 0)
                {
                    surface.Driven = true;
                    break;
                }
            }
        }
    }

    public void ResetToRest()
    {
        foreach (Surface surface in _surfaces)
        {
            Array.Copy(surface.RestPositions, surface.Positions, surface.VertexCount);
        }
    }

    public void Apply(ClothPiece piece, PhysVec4[] simulated)
    {
        int[] clothVertexOf = piece.ClothVertexOf;

        foreach (Surface surface in _surfaces)
        {
            if (!surface.Driven)
            {
                continue;
            }

            for (int v = 0; v < surface.VertexCount; v++)
            {
                int flat = surface.VertexBase + v;
                if (flat >= clothVertexOf.Length)
                {
                    break;
                }

                int cloth = clothVertexOf[flat];
                if (cloth < 0 || cloth >= simulated.Length)
                {
                    continue;
                }

                PhysVec4 p = simulated[cloth];
                surface.Positions[v] = new Vector3(p.X, p.Y, p.Z);
            }
        }
    }

    public void RecomputeNormals()
    {
        foreach (Surface surface in _surfaces)
        {
            if (!surface.Driven || surface.Indices.Length < 3)
            {
                continue;
            }

            Vector3[] normals = surface.Normals;
            Array.Clear(normals, 0, normals.Length);

            int triangles = surface.Indices.Length - (surface.Indices.Length % 3);
            for (int i = 0; i < triangles; i += 3)
            {
                int a = surface.Indices[i];
                int b = surface.Indices[i + 1];
                int c = surface.Indices[i + 2];

                if (a < 0 || b < 0 || c < 0 ||
                    a >= surface.VertexCount || b >= surface.VertexCount ||
                    c >= surface.VertexCount)
                {
                    continue;
                }

                Vector3 face = (surface.Positions[b] - surface.Positions[a])
                    .Cross(surface.Positions[c] - surface.Positions[a]);

                normals[a] += face;
                normals[b] += face;
                normals[c] += face;
            }

            for (int v = 0; v < normals.Length; v++)
            {
                normals[v] = normals[v].LengthSquared() > 1e-12f
                    ? normals[v].Normalized()
                    : Vector3.Up;
            }
        }
    }

    public void Rebuild()
    {
        _mesh.ClearSurfaces();

        for (int s = 0; s < _surfaces.Count; s++)
        {
            Surface surface = _surfaces[s];
            Godot.Collections.Array arrays = surface.Arrays;

            if (surface.Driven)
            {
                arrays[(int)Mesh.ArrayType.Vertex] = surface.Positions;
                arrays[(int)Mesh.ArrayType.Normal] = surface.Normals;
            }

            _mesh.AddSurfaceFromArrays(
                Mesh.PrimitiveType.Triangles, arrays, null, surface.Lods,
                Refii.Models.Engine_MdlTransfer.FormatOf(arrays));

            if (surface.Name.Length != 0)
            {
                _mesh.SurfaceSetName(s, surface.Name);
            }

            if (surface.Material is not null)
            {
                _mesh.SurfaceSetMaterial(s, surface.Material);
            }
        }
    }

    public int DrivenSurfaceCount()
    {
        int count = 0;
        foreach (Surface surface in _surfaces)
        {
            if (surface.Driven)
            {
                count++;
            }
        }

        return count;
    }

    public Vector3 PositionAt(int flatVertex)
    {
        foreach (Surface surface in _surfaces)
        {
            if (flatVertex >= surface.VertexBase &&
                flatVertex < surface.VertexBase + surface.VertexCount)
            {
                return surface.Positions[flatVertex - surface.VertexBase];
            }
        }

        return Vector3.Zero;
    }
}
