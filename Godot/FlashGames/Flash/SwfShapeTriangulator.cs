using System;
using System.Collections.Generic;
using Godot;

namespace Refii.FlashGames;

/// <summary>
/// One fill of one shape, as triangles Godot can draw.
/// </summary>
/// <remarks>
/// Positions are in PIXELS, shape local — whatever space <see cref="SwfContour.Points"/> is in,
/// unchanged, because a triangulation that also converted units would be two things to get wrong
/// instead of one.
///
/// PIXELS RATHER THAN TWIPS IS EASY TO GET BACKWARDS, and it is not visible from the contour type:
/// <c>SwfShapeGeometry.ToPixels</c> divides by <see cref="SwfShapeGeometry.TwipsPerPixel"/> as it
/// traces, which is why <see cref="SwfRasterizer.DrawInto"/> multiplies every contour point by 20
/// again before applying a fill matrix. FILL MATRICES ARE DEFINED IN TWIPS, so anything mapping a
/// vertex through one has to convert first.
/// </remarks>
public sealed class SwfFillMesh
{
    /// <summary>Index into <see cref="SwfShape.FillStyles"/>, one based, as the contours carry it.</summary>
    public required int FillStyle { get; init; }

    /// <summary>Triangle soup: three vertices per triangle, no index buffer.</summary>
    public required Vector2[] Vertices { get; init; }

    public int TriangleCount => Vertices.Length / 3;
}

/// <summary>
/// Turns flattened SWF contours into triangles, so Godot draws the pub games' artwork instead of
/// a software rasterizer.
/// </summary>
/// <remarks>
/// WHY THIS EXISTS. The boards were drawn by <see cref="SwfRasterizer"/> into bitmaps at the size
/// the artwork was authored at, and the stage is then magnified to fill the window — about 2.3x at
/// 1080p from a 640x462 stage. So the artwork was soft, and the only lever was to rasterize
/// larger, which is quadratic: keystone measured 109 MB and 4.6 s of CPU at authored size, and
/// 348 MB and 11.9 s at 2.5x. Triangles have no resolution. They are built once, cost the same
/// whatever the window is, and the GPU draws them — which is the whole reason the pub games were
/// rebuilt in C# rather than interpreted.
///
/// EVEN-ODD, BECAUSE THAT IS WHAT THE RASTERIZER DOES. Its scanline filler sorts the crossings on
/// each row and fills between alternate pairs, so a ring inside another ring is a hole and two
/// disjoint rings are two islands, with no winding rule and no per-ring flag to consult. Any
/// replacement has to agree with that or shapes come out with their holes filled in.
///
/// TWO PATHS, AND THE COMMON ONE IS THE SIMPLE ONE. A census of all three shipped games found
/// 27,774 fill groups, 69.0% of them a single ring — those need no even-odd resolution at all and
/// go through ear clipping, which produces the minimum n-2 triangles. Everything else goes through
/// a trapezoidal decomposition of the same even-odd rule the rasterizer applies, which is exact for
/// straight edges and handles holes and islands without a boolean library or a containment test.
///
/// STRAIGHT EDGES ARE WHAT IT GETS. <see cref="SwfShapeGeometry"/> has already flattened every
/// quadratic to a 0.25 px tolerance, so the decomposition is exact rather than approximate, and
/// the flattening error is the only error — 0.6 px at the 2.3x the stage is magnified by.
/// </remarks>
public static class SwfShapeTriangulator
{
    /// <summary>
    /// Every fill of a shape, triangulated.
    /// </summary>
    /// <remarks>
    /// Grouped by fill style exactly as <see cref="SwfRasterizer.DrawInto"/> groups them, and
    /// walked in the same sorted order, because the fills are drawn over one another and the order
    /// is what decides which is on top. A dictionary's enumeration order is not that order.
    /// </remarks>
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

    /// <summary>
    /// One fill group's rings, split into clusters that cannot affect one another.
    /// </summary>
    /// <remarks>
    /// THIS IS WHAT MAKES THE DECOMPOSITION AFFORDABLE, and without it the whole approach is worse
    /// than what it replaces. Even-odd is only interesting where rings OVERLAP: two rings that
    /// share no ground are two independent shapes, and their combined fill is the union of their
    /// separate fills. Sending the whole group through the decomposition anyway meant its band
    /// count was the number of distinct vertex rows in the entire group — the largest group in the
    /// shipped games has 814 rings — and the cost is bands times spans. Measured over all three
    /// games that was 18.9 million triangles and 38.8 s, against a rasterizer that takes 4.6 s.
    ///
    /// CLUSTERED BY BOUNDING BOX OVERLAP, which is conservative in the safe direction: two rings
    /// whose boxes overlap might not interact, and they are decomposed together for nothing. Two
    /// rings whose boxes do not overlap cannot interact, and that is the case being exploited.
    ///
    /// CONTAINMENT WOULD BE CHEAPER AND IS WRONG. Clustering only rings that lie inside one another
    /// cut keystone to 3.8 s and 3.2 M triangles, and the probe immediately found wizardstower's
    /// shape 884 down at 95.076% agreement: even-odd also differs from a plain union where two
    /// rings CROSS, and the shipped artwork has those. A hole is the case that is easy to think of,
    /// not the only one.
    ///
    /// A cluster of one goes through ear clipping, which is the minimum n-2 triangles.
    /// </remarks>
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

        // Union-find over "boxes overlap", so a chain of overlaps ends up in one cluster even
        // where the ends of the chain do not touch. Anything less would split a cluster whose
        // rings genuinely interact through a third.
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

    /// <summary>
    /// The single-ring path: Godot's own ear clipper.
    /// </summary>
    /// <remarks>
    /// Used rather than sending everything through the decomposition because it produces n-2
    /// triangles against the decomposition's several per horizontal band, and 69% of all fill
    /// groups take this path. A ring it cannot triangulate — degenerate, or self intersecting —
    /// returns nothing, and the caller falls back rather than drawing a wrong shape.
    /// </remarks>
    private static Vector2[] EarClip(List<Vector2> ring)
    {
        Vector2[] points = ring.ToArray();
        int[] indices = Geometry2D.TriangulatePolygon(points);
        if (indices.Length < 3)
        {
            // Self intersecting rings do occur, and even-odd is defined for them where ear
            // clipping is not. The decomposition handles them, so hand it over rather than lose
            // the fill.
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

        /// <summary>Where the edge crosses a row. Only meaningful between its own two ends.</summary>
        public float XAt(float y) => TopX + ((y - TopY) * Slope);
    }

    /// <summary>
    /// Even-odd fill as trapezoids, exact for straight edges.
    /// </summary>
    /// <remarks>
    /// THE SAME RULE AS THE SCANLINE FILLER, evaluated at the vertices instead of at pixel centres.
    /// Between two consecutive distinct vertex Y values no edge starts, ends or crosses another's
    /// ordering, so the crossings at the top of the band and the crossings at the bottom pair up in
    /// the same order — and each pair of pairs is a trapezoid, which is two triangles. That is what
    /// makes this resolution independent rather than a finer rasterization: the bands are decided
    /// by the geometry, not by a pixel grid.
    ///
    /// SAMPLED AT THE BAND'S MIDDLE to decide which spans are inside. Sampling at a boundary lands
    /// exactly on the vertices that created it, where an edge is simultaneously starting and
    /// ending and the crossing count is unreliable.
    ///
    /// Horizontal edges are dropped, as they are in the scanline filler: they contribute no
    /// crossing and their endpoints are already in the band list.
    /// </remarks>
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
                // SPANNING THE WHOLE BAND, which is the property the band boundaries guarantee: an
                // edge that merely touched it would have put its own end in the boundary list and
                // split the band there.
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
