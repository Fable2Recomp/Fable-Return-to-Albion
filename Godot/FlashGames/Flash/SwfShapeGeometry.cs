using System;
using System.Collections.Generic;
using Godot;

namespace Refii.FlashGames;

public sealed class SwfContour
{
    public int FillStyle { get; init; }

    public List<Vector2> Points { get; } = new();

    public bool Closed { get; set; }
}

public sealed class SwfStroke
{
    public int LineStyle { get; init; }

    public List<Vector2> Points { get; } = new();
}

public sealed class SwfGeometry
{
    public List<SwfContour> Contours { get; } = new();

    public List<SwfStroke> Strokes { get; } = new();

    public int OpenContours { get; set; }
}

public static class SwfShapeGeometry
{
    public const float TwipsPerPixel = 20f;

    private sealed class Segment
    {
        public required List<Vector2I> Anchors { get; init; }

        public required List<Vector2> Points { get; init; }

        public Vector2I Start => Anchors[0];

        public Vector2I End => Anchors[^1];
    }

    public static SwfGeometry Build(SwfShape shape, float curveTolerancePixels = 0.25f)
    {
        var geometry = new SwfGeometry();
        var byFill = new Dictionary<int, List<Segment>>();

        foreach (SwfPath path in shape.Paths)
        {
            Segment? segment = Trace(path, curveTolerancePixels);
            if (segment is null)
            {
                continue;
            }

            if (path.LineStyle > 0)
            {
                var stroke = new SwfStroke { LineStyle = path.LineStyle };
                stroke.Points.AddRange(segment.Points);
                geometry.Strokes.Add(stroke);
            }

            if (path.FillStyle1 > 0)
            {
                Add(byFill, path.FillStyle1, segment);
            }

            if (path.FillStyle0 > 0)
            {
                Add(byFill, path.FillStyle0, Reverse(segment));
            }
        }

        foreach ((int fill, List<Segment> segments) in byFill)
        {
            Stitch(geometry, fill, segments);
        }

        return geometry;
    }

    private static void Add(Dictionary<int, List<Segment>> map, int fill, Segment segment)
    {
        if (!map.TryGetValue(fill, out List<Segment>? list))
        {
            list = new List<Segment>();
            map[fill] = list;
        }

        list.Add(segment);
    }

    private static Segment Reverse(Segment segment)
    {
        var anchors = new List<Vector2I>(segment.Anchors);
        anchors.Reverse();
        var points = new List<Vector2>(segment.Points);
        points.Reverse();
        return new Segment { Anchors = anchors, Points = points };
    }

    private static Segment? Trace(SwfPath path, float tolerancePixels)
    {
        if (path.Edges.Count == 0)
        {
            return null;
        }

        var anchors = new List<Vector2I> { new(path.StartX, path.StartY) };
        var points = new List<Vector2> { ToPixels(path.StartX, path.StartY) };

        int x = path.StartX;
        int y = path.StartY;
        foreach (SwfEdge edge in path.Edges)
        {
            if (edge.Kind == SwfEdgeKind.Line)
            {
                points.Add(ToPixels(edge.AnchorX, edge.AnchorY));
            }
            else
            {
                Flatten(points, x, y, edge.ControlX, edge.ControlY, edge.AnchorX, edge.AnchorY, tolerancePixels);
            }

            x = edge.AnchorX;
            y = edge.AnchorY;
            anchors.Add(new Vector2I(x, y));
        }

        return new Segment { Anchors = anchors, Points = points };
    }

    private static void Flatten(
        List<Vector2> into, int x0, int y0, int cx, int cy, int x1, int y1, float tolerancePixels)
    {
        float deviation = Mathf.Abs((x0 + x1 - (2 * cx)) / TwipsPerPixel) +
                          Mathf.Abs((y0 + y1 - (2 * cy)) / TwipsPerPixel);
        int steps = 1;
        if (deviation > 0f && tolerancePixels > 0f)
        {
            steps = Mathf.CeilToInt(Mathf.Sqrt(deviation / (4f * tolerancePixels)) * 4f);
        }

        steps = Mathf.Clamp(steps, 1, 64);

        for (int i = 1; i <= steps; i++)
        {
            float t = (float)i / steps;
            float inv = 1f - t;
            float px = (inv * inv * x0) + (2f * inv * t * cx) + (t * t * x1);
            float py = (inv * inv * y0) + (2f * inv * t * cy) + (t * t * y1);
            into.Add(new Vector2(px / TwipsPerPixel, py / TwipsPerPixel));
        }
    }

    private static Vector2 ToPixels(int x, int y) =>
        new(x / TwipsPerPixel, y / TwipsPerPixel);

    private static void Stitch(SwfGeometry geometry, int fill, List<Segment> segments)
    {
        var starts = new Dictionary<Vector2I, List<int>>();
        for (int i = 0; i < segments.Count; i++)
        {
            Vector2I key = segments[i].Start;
            if (!starts.TryGetValue(key, out List<int>? list))
            {
                list = new List<int>();
                starts[key] = list;
            }

            list.Add(i);
        }

        var used = new bool[segments.Count];
        for (int i = 0; i < segments.Count; i++)
        {
            if (used[i])
            {
                continue;
            }

            var contour = new SwfContour { FillStyle = fill };
            used[i] = true;
            contour.Points.AddRange(segments[i].Points);
            Vector2I first = segments[i].Start;
            Vector2I cursor = segments[i].End;

            while (cursor != first)
            {
                int next = -1;
                if (starts.TryGetValue(cursor, out List<int>? candidates))
                {
                    foreach (int candidate in candidates)
                    {
                        if (!used[candidate])
                        {
                            next = candidate;
                            break;
                        }
                    }
                }

                if (next < 0)
                {
                    break;
                }

                used[next] = true;
                List<Vector2> points = segments[next].Points;
                for (int p = 1; p < points.Count; p++)
                {
                    contour.Points.Add(points[p]);
                }

                cursor = segments[next].End;
            }

            contour.Closed = cursor == first;
            if (!contour.Closed)
            {
                geometry.OpenContours++;
            }

            geometry.Contours.Add(contour);
        }
    }
}
