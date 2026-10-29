using System;
using System.Collections.Generic;
using Godot;

namespace Refii.Titles.Fable2.PubGames;

public sealed class SwfFillMesh
{
    public required int FillStyle { get; init; }

    public required Vector2[] Vertices { get; init; }

    public int TriangleCount => Vertices.Length / 3;
}

public static class SwfShapeTriangulator
{
    public static List<SwfFillMesh> Build(SwfShape shape, SwfGeometry geometry)
    {
        var meshes = new List<SwfFillMesh>();
        var byFill = new SortedDictionary<int, List<List<Vector2>>>();

        foreach (SwfContour contour in geometry.Contours)
        {
            if (contour.FillStyle <= 0 || contour.FillStyle > shape.FillStyles.Count ||
                contour.Points.Count < 3)
            {
                continue;
            }

            if (!byFill.TryGetValue(contour.FillStyle, out List<List<Vector2>>? rings))
            {
                rings = new List<List<Vector2>>();
                byFill[contour.FillStyle] = rings;
            }

            rings.Add(contour.Points);
        }

        foreach ((int fillStyle, List<List<Vector2>> rings) in byFill)
        {
            Vector2[] vertices = Triangulate(rings);
            if (vertices.Length >= 3)
            {
                meshes.Add(new SwfFillMesh { FillStyle = fillStyle, Vertices = vertices });
            }
        }

        return meshes;
    }

    private static Vector2[] Triangulate(List<List<Vector2>> rings)
    {
        if (rings.Count == 1)
        {
            return EarClip(rings[0]);
        }

        var boxes = new Rect2[rings.Count];
        for (var i = 0; i < rings.Count; i++)
        {
            boxes[i] = BoxOf(rings[i]);
        }

        var parent = new int[rings.Count];
        for (var i = 0; i < rings.Count; i++)
        {
            parent[i] = i;
        }

        for (var i = 0; i < rings.Count; i++)
        {
            for (int j = i + 1; j < rings.Count; j++)
            {
                if (boxes[i].Intersects(boxes[j]))
                {
                    Union(parent, i, j);
                }
            }
        }

        var clusters = new Dictionary<int, List<List<Vector2>>>();
        for (var i = 0; i < rings.Count; i++)
        {
            int root = Find(parent, i);
            if (!clusters.TryGetValue(root, out List<List<Vector2>>? cluster))
            {
                cluster = new List<List<Vector2>>();
                clusters[root] = cluster;
            }

            cluster.Add(rings[i]);
        }

        var vertices = new List<Vector2>();
        foreach (List<List<Vector2>> cluster in clusters.Values)
        {
            vertices.AddRange(cluster.Count == 1 ? EarClip(cluster[0]) : Trapezoids(cluster));
        }

        return vertices.ToArray();
    }

    private static int Find(int[] parent, int index)
    {
        while (parent[index] != index)
        {
            parent[index] = parent[parent[index]];
            index = parent[index];
        }

        return index;
    }

    private static void Union(int[] parent, int a, int b)
    {
        int rootA = Find(parent, a);
        int rootB = Find(parent, b);
        if (rootA != rootB)
        {
            parent[rootB] = rootA;
        }
    }

    private static Rect2 BoxOf(List<Vector2> ring)
    {
        var min = new Vector2(float.MaxValue, float.MaxValue);
        var max = new Vector2(float.MinValue, float.MinValue);
        foreach (Vector2 point in ring)
        {
            min = new Vector2(Math.Min(min.X, point.X), Math.Min(min.Y, point.Y));
            max = new Vector2(Math.Max(max.X, point.X), Math.Max(max.Y, point.Y));
        }

        return new Rect2(min, max - min);
    }

    private static Vector2[] EarClip(List<Vector2> ring)
    {
        Vector2[] points = ring.ToArray();
        int[] indices = Geometry2D.TriangulatePolygon(points);
        if (indices.Length < 3)
        {
            return Trapezoids(new List<List<Vector2>> { ring });
        }

        var vertices = new Vector2[indices.Length];
        for (var i = 0; i < indices.Length; i++)
        {
            vertices[i] = points[indices[i]];
        }

        return vertices;
    }

    private readonly struct Edge
    {
        public Edge(Vector2 a, Vector2 b)
        {
            TopY = Math.Min(a.Y, b.Y);
            BottomY = Math.Max(a.Y, b.Y);
            Vector2 top = a.Y < b.Y ? a : b;
            TopX = top.X;
            Slope = (b.X - a.X) / (b.Y - a.Y);
        }

        public float TopY { get; }

        public float BottomY { get; }

        private float TopX { get; }

        private float Slope { get; }

        public float XAt(float y) => TopX + ((y - TopY) * Slope);
    }

    private static Vector2[] Trapezoids(List<List<Vector2>> rings)
    {
        var edges = new List<Edge>();
        var boundaries = new List<float>();

        foreach (List<Vector2> ring in rings)
        {
            int count = ring.Count;
            for (var i = 0; i < count; i++)
            {
                Vector2 a = ring[i];
                Vector2 b = ring[(i + 1) % count];
                boundaries.Add(a.Y);

                if (a.Y == b.Y)
                {
                    continue;
                }

                edges.Add(new Edge(a, b));
            }
        }

        if (edges.Count == 0)
        {
            return Array.Empty<Vector2>();
        }

        boundaries.Sort();

        var vertices = new List<Vector2>();
        var top = new List<float>();
        var bottom = new List<float>();

        for (var i = 0; i + 1 < boundaries.Count; i++)
        {
            float y0 = boundaries[i];
            float y1 = boundaries[i + 1];
            if (y1 - y0 <= 0f)
            {
                continue;
            }

            float middle = (y0 + y1) * 0.5f;

            top.Clear();
            bottom.Clear();
            foreach (Edge edge in edges)
            {
                if (edge.TopY > middle || edge.BottomY < middle)
                {
                    continue;
                }

                top.Add(edge.XAt(y0));
                bottom.Add(edge.XAt(y1));
            }

            if (top.Count < 2)
            {
                continue;
            }

            top.Sort();
            bottom.Sort();

            for (var pair = 0; pair + 1 < top.Count; pair += 2)
            {
                float topLeft = top[pair];
                float topRight = top[pair + 1];
                float bottomLeft = bottom[pair];
                float bottomRight = bottom[pair + 1];

                var a = new Vector2(topLeft, y0);
                var b = new Vector2(topRight, y0);
                var c = new Vector2(bottomRight, y1);
                var d = new Vector2(bottomLeft, y1);

                vertices.Add(a);
                vertices.Add(b);
                vertices.Add(c);

                vertices.Add(a);
                vertices.Add(c);
                vertices.Add(d);
            }
        }

        return vertices.ToArray();
    }
}
