using System;
using System.Collections.Generic;
using Godot;

namespace Refii.Level;

public sealed class Engine_DecorateCollision
{
    private readonly Dictionary<Mesh, (Vector3[] Faces, Aabb[] Chunks)> _meshes = new();
    private readonly HashSet<int> _crossings = new();
    private const int ChunkSize = 96;

    public void Clear() => _meshes.Clear();

    public bool Blocked(MeshInstance3D preview, Transform3D source, bool excludeSource)
    {
        if (preview.Mesh is not { } mesh) return true;
        Transform3D pose = preview.GlobalTransform;
        return Blocked(preview, source, excludeSource,
            RenderingServer.InstancesCullAabb(pose * Inset(mesh.GetAabb(), pose), preview.GetWorld3D().Scenario));
    }

    public bool Blocked(MeshInstance3D preview, Transform3D source, bool excludeSource, ReadOnlySpan<long> candidates)
    {
        if (preview.Mesh is not { } mesh) return true;
        Transform3D pose = preview.GlobalTransform;
        Aabb bounds = Inset(mesh.GetAabb(), pose);
        Aabb worldBounds = pose * bounds;
        foreach (ulong id in candidates)
        {
            if (GodotObject.InstanceFromId(id) is not GeometryInstance3D node || node == preview ||
                !node.IsVisibleInTree() || node.CastShadow == GeometryInstance3D.ShadowCastingSetting.ShadowsOnly) continue;
            Transform3D transform = node.GlobalTransform;
            if (node is MeshInstance3D { Mesh: { } other })
            {
                if (excludeSource && other == mesh && transform.IsEqualApprox(source)) continue;
                if (Overlaps(bounds, pose, other, transform)) return true;
            }
            else if (node is MultiMeshInstance3D { Multimesh: { Mesh: { } batched } instances })
            {
                int count = instances.VisibleInstanceCount < 0 ? instances.InstanceCount : instances.VisibleInstanceCount;
                Aabb local = batched.GetAabb();
                for (int i = 0; i < count; i++)
                {
                    Transform3D at = transform * instances.GetInstanceTransform(i);
                    if (Math.Abs(at.Basis.Determinant()) < 0.000001f || !worldBounds.Intersects(at * local) ||
                        excludeSource && batched == mesh && at.IsEqualApprox(source)) continue;
                    if (Overlaps(bounds, pose, batched, at)) return true;
                }
            }
        }
        return false;
    }

    public static Aabb Inset(Aabb bounds, Transform3D pose)
    {
        Vector3 size = pose.Basis.Scale.Abs();
        Vector3 margin = new(Math.Min(bounds.Size.X * 0.1f, 0.015f / Math.Max(size.X, 0.001f)),
            Math.Min(bounds.Size.Y * 0.1f, 0.015f / Math.Max(size.Y, 0.001f)),
            Math.Min(bounds.Size.Z * 0.1f, 0.015f / Math.Max(size.Z, 0.001f)));
        return new Aabb(bounds.Position + margin, bounds.Size - margin * 2);
    }

    public bool Overlaps(Aabb bounds, Transform3D pose, Mesh mesh, Transform3D other)
    {
        if (!pose.IsFinite() || !other.IsFinite() || Math.Abs(pose.Basis.Determinant()) < 0.000001f ||
            Math.Abs(other.Basis.Determinant()) < 0.000001f) return false;
        Transform3D toBox = pose.AffineInverse() * other;
        if (!bounds.Intersects(toBox * mesh.GetAabb())) return false;
        if (!_meshes.TryGetValue(mesh, out var cached))
        {
            Vector3[] faces = mesh.GetFaces();
            var chunks = new Aabb[(faces.Length + ChunkSize - 1) / ChunkSize];
            for (int i = 0; i < chunks.Length; i++)
            {
                var chunk = new Aabb(faces[i * ChunkSize], Vector3.Zero);
                for (int j = i * ChunkSize + 1; j < Math.Min(faces.Length, (i + 1) * ChunkSize); j++) chunk = chunk.Expand(faces[j]);
                chunks[i] = chunk;
            }
            cached = (faces, chunks);
            _meshes.Add(mesh, cached);
        }
        for (int i = 0; i < cached.Chunks.Length; i++)
        {
            if (!bounds.Intersects(toBox * cached.Chunks[i])) continue;
            for (int j = i * ChunkSize; j + 2 < Math.Min(cached.Faces.Length, (i + 1) * ChunkSize); j += 3)
                if (Triangle(bounds, toBox * cached.Faces[j], toBox * cached.Faces[j + 1], toBox * cached.Faces[j + 2])) return true;
        }
        Vector3 point = toBox.AffineInverse() * bounds.GetCenter();
        if (!mesh.GetAabb().HasPoint(point)) return false;
        Vector3 ray = new Vector3(1, 0.371f, 0.193f).Normalized();
        _crossings.Clear();
        for (int i = 0; i < cached.Faces.Length; i += 3)
        {
            Vector3 a = cached.Faces[i], ab = cached.Faces[i + 1] - a, ac = cached.Faces[i + 2] - a;
            Vector3 h = ray.Cross(ac);
            float det = ab.Dot(h);
            if (Math.Abs(det) < 0.0000001f) continue;
            Vector3 offset = point - a;
            float u = offset.Dot(h) / det;
            if (u < 0 || u > 1) continue;
            Vector3 q = offset.Cross(ab);
            float v = ray.Dot(q) / det;
            if (v < 0 || u + v > 1) continue;
            float distance = ac.Dot(q) / det;
            if (distance > 0) _crossings.Add((int)Math.Round(distance * 10000));
        }
        return (_crossings.Count & 1) != 0;
    }

    public static bool Triangle(Aabb bounds, Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 center = bounds.GetCenter(), half = bounds.Size * 0.5f;
        a -= center; b -= center; c -= center;
        Vector3 ab = b - a, bc = c - b, ca = a - c;
        if (Separated(Vector3.Right, half, a, b, c) || Separated(Vector3.Up, half, a, b, c) ||
            Separated(Vector3.Back, half, a, b, c) || Separated(ab.Cross(bc), half, a, b, c)) return false;
        for (int i = 0; i < 3; i++)
        {
            Vector3 edge = i == 0 ? ab : i == 1 ? bc : ca;
            if (Separated(edge.Cross(Vector3.Right), half, a, b, c) ||
                Separated(edge.Cross(Vector3.Up), half, a, b, c) ||
                Separated(edge.Cross(Vector3.Back), half, a, b, c)) return false;
        }
        return true;
    }

    private static bool Separated(Vector3 axis, Vector3 half, Vector3 a, Vector3 b, Vector3 c)
    {
        float radius = axis.Abs().Dot(half), x = axis.Dot(a), y = axis.Dot(b), z = axis.Dot(c);
        return Math.Min(x, Math.Min(y, z)) > radius || Math.Max(x, Math.Max(y, z)) < -radius;
    }
}
