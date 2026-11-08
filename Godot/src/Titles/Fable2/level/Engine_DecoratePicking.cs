using System;
using System.Collections.Generic;
using Godot;

namespace Refii.Titles.Fable2;

using Refii.Level;

public sealed class Engine_DecoratePicking : IDisposable
{
    public const float Range = 40;
    private readonly Dictionary<Mesh, (Aabb Bounds, TriangleMesh? Triangles)> _meshes = new();

    public static bool Segment(Aabb bounds, Transform3D transform, Vector3 origin, Vector3 direction,
        float distance, out Vector3 from, out Vector3 to)
    {
        from = to = default;
        if (!origin.IsFinite() || !direction.IsFinite() || !transform.IsFinite() ||
            !float.IsFinite(distance) || distance <= 0 || direction.LengthSquared() < 0.000001f ||
            Math.Abs(transform.Basis.Determinant()) < 0.000001f) return false;
        Transform3D inverse = transform.AffineInverse();
        from = inverse * origin;
        to = inverse * (origin + direction.Normalized() * distance);
        return bounds.IntersectsSegment(from, to);
    }

    public bool Hit(Mesh mesh, Transform3D transform, Vector3 origin, Vector3 direction, float limit, out float distance)
    {
        distance = float.PositiveInfinity;
        if (!_meshes.TryGetValue(mesh, out var cached))
        {
            cached = (mesh.GetAabb(), null);
            _meshes.Add(mesh, cached);
        }
        if (!Segment(cached.Bounds, transform, origin, direction, limit, out Vector3 from, out Vector3 to)) return false;
        if (cached.Triangles is null)
        {
            cached.Triangles = mesh.GenerateTriangleMesh();
            _meshes[mesh] = cached;
        }
        if (cached.Triangles is null) return false;
        using var hit = cached.Triangles.IntersectSegment(from, to);
        if (!hit.TryGetValue("position", out Variant position)) return false;
        distance = origin.DistanceTo(transform * position.AsVector3());
        return float.IsFinite(distance) && distance <= limit;
    }

    public void Dispose()
    {
        foreach (var mesh in _meshes.Values) mesh.Triangles?.Dispose();
        _meshes.Clear();
    }
}
