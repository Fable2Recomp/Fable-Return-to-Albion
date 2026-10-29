using System;
using System.Collections.Generic;
using Godot;

namespace Refii.Level;

public sealed class Engine_DecorateInterior
{
    private readonly Dictionary<Vector2I, List<Triangle>> _columns = new();
    private readonly List<float> _heights = new();
    private Aabb _bounds;
    private readonly record struct Triangle(Vector3 A, Vector3 B, Vector3 C, float Determinant);
    public bool Ready => _columns.Count != 0;

    public void Clear() => _columns.Clear();

    public void Build(ReadOnlySpan<Vector3> faces, Transform3D transform)
    {
        Clear();
        if (faces.Length < 3 || !transform.IsFinite()) return;
        _bounds = new Aabb(transform * faces[0], Vector3.Zero);
        for (int i = 0; i + 2 < faces.Length; i += 3)
        {
            Vector3 a = transform * faces[i], b = transform * faces[i + 1], c = transform * faces[i + 2];
            if (!a.IsFinite() || !b.IsFinite() || !c.IsFinite()) { Clear(); return; }
            _bounds = _bounds.Expand(a).Expand(b).Expand(c);
            Vector3 ab = b - a, ac = c - a;
            float determinant = ab.X * ac.Z - ab.Z * ac.X;
            if (Math.Abs(determinant) < 0.000001f) continue;
            Vector3 min = a.Min(b).Min(c), max = a.Max(b).Max(c);
            int x0 = Mathf.FloorToInt(min.X), x1 = Mathf.FloorToInt(max.X);
            int z0 = Mathf.FloorToInt(min.Z), z1 = Mathf.FloorToInt(max.Z);
            if ((long)(x1 - x0 + 1) * (z1 - z0 + 1) > 65536) { Clear(); return; }
            var triangle = new Triangle(a, ab, ac, determinant);
            for (int x = x0; x <= x1; x++)
                for (int z = z0; z <= z1; z++)
                {
                    var key = new Vector2I(x, z);
                    if (!_columns.TryGetValue(key, out var column)) _columns[key] = column = new();
                    column.Add(triangle);
                }
        }
    }

    public bool Contains(Vector3 point)
    {
        if (!point.IsFinite() || !Ready || !_bounds.Grow(0.06f).HasPoint(point) ||
            !_columns.TryGetValue(new Vector2I(Mathf.FloorToInt(point.X), Mathf.FloorToInt(point.Z)), out var column)) return false;
        _heights.Clear();
        foreach (var t in column)
        {
            Vector3 offset = point - t.A;
            float u = (offset.X * t.C.Z - offset.Z * t.C.X) / t.Determinant;
            float v = (t.B.X * offset.Z - t.B.Z * offset.X) / t.Determinant;
            if (u < -0.00001f || v < -0.00001f || u + v > 1.00001f) continue;
            _heights.Add(t.A.Y + u * t.B.Y + v * t.C.Y);
        }
        if (_heights.Count < 2) return false;
        _heights.Sort();
        float bottom = _heights[0];
        for (int i = 1; i < _heights.Count; i++)
        {
            float top = _heights[i];
            if (top - bottom >= 0.4f && point.Y >= bottom - 0.06f && point.Y <= top + 0.06f) return true;
            bottom = top;
        }
        return false;
    }

    public bool Contains(Aabb bounds, Transform3D transform)
    {
        if (!Ready || !transform.IsFinite() || !bounds.Position.IsFinite() || !bounds.Size.IsFinite()) return false;
        Vector3 size = transform.Basis.Scale.Abs() * bounds.Size;
        if (size.X > 100 || size.Y > 100 || size.Z > 100) return false;
        int nx = Math.Clamp((int)Math.Ceiling(size.X / 0.25f), 1, 400);
        int nz = Math.Clamp((int)Math.Ceiling(size.Z / 0.25f), 1, 400);
        for (int x = 0; x <= nx; x++)
            for (int z = 0; z <= nz; z++)
                for (int y = 0; y <= 2; y++)
                {
                    Vector3 at = bounds.Position + bounds.Size * new Vector3((float)x / nx, y * 0.5f, (float)z / nz);
                    if (!Contains(transform * at)) return false;
                }
        return true;
    }
}
