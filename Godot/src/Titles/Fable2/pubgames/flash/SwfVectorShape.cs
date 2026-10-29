using System;
using System.Collections.Generic;
using Godot;

namespace Refii.Titles.Fable2.PubGames;

public sealed class SwfVectorPart
{
    public required ArrayMesh Mesh { get; init; }

    public Texture2D? Texture { get; init; }

    public bool Repeat { get; init; }

    public int FirstFill { get; init; }

    public required Vector2[] Vertices { get; init; }

    public Color[]? Colours { get; init; }

    public Vector2[]? Uvs { get; init; }
}

public static class SwfVectorShape
{
    private const int GradientResolution = 256;

    private const float GradientHalfSpan = 16384f;

    public static List<SwfVectorPart> Build(
        SwfShape shape,
        SwfGeometry geometry,
        Func<int, Texture2D?>? hostBitmap,
        Func<int, Texture2D?>? embeddedBitmap)
    {
        var parts = new List<SwfVectorPart>();
        List<SwfFillMesh> meshes = SwfShapeTriangulator.Build(shape, geometry);
        if (meshes.Count == 0)
        {
            return parts;
        }

        var solidPoints = new List<Vector2>();
        var solidColours = new List<Color>();
        var runStart = 0;

        foreach (SwfFillMesh mesh in meshes)
        {
            SwfFillStyle style = shape.FillStyles[mesh.FillStyle - 1];

            if (style.Kind == SwfFillKind.Solid)
            {
                if (solidPoints.Count == 0)
                {
                    runStart = mesh.FillStyle;
                }

                Color colour = ToColor(style.Color);
                foreach (Vector2 point in mesh.Vertices)
                {
                    solidPoints.Add(point);
                    solidColours.Add(colour);
                }

                continue;
            }

            Flush(parts, solidPoints, solidColours, runStart);

            SwfVectorPart? textured = Textured(mesh, style, hostBitmap, embeddedBitmap);
            if (textured is not null)
            {
                parts.Add(textured);
            }
        }

        Flush(parts, solidPoints, solidColours, runStart);
        return parts;
    }

    public static List<int> PartFills(SwfShape shape, SwfGeometry geometry)
    {
        var withContours = new SortedSet<int>();
        foreach (SwfContour contour in geometry.Contours)
        {
            if (contour.FillStyle > 0 && contour.FillStyle <= shape.FillStyles.Count &&
                contour.Points.Count >= 3)
            {
                withContours.Add(contour.FillStyle);
            }
        }

        var fills = new List<int>();
        var inSolidRun = false;
        foreach (int fill in withContours)
        {
            SwfFillStyle style = shape.FillStyles[fill - 1];
            if (style.Kind == SwfFillKind.Solid)
            {
                if (!inSolidRun)
                {
                    fills.Add(fill);
                    inSolidRun = true;
                }

                continue;
            }

            inSolidRun = false;
            if (style.BitmapId != SwfRasterizer.HostSuppliedBitmapId)
            {
                fills.Add(fill);
            }
        }

        return fills;
    }

    private static void Flush(
        List<SwfVectorPart> parts, List<Vector2> points, List<Color> colours, int firstFill)
    {
        if (points.Count >= 3)
        {
            Vector2[] vertices = points.ToArray();
            Color[] tints = colours.ToArray();
            parts.Add(new SwfVectorPart
            {
                Mesh = MeshOf(vertices, tints, uvs: null),
                FirstFill = firstFill,
                Vertices = vertices,
                Colours = tints,
            });
        }

        points.Clear();
        colours.Clear();
    }

    public static ArrayMesh MeshFrom(Vector2[] vertices, Color[]? colours, Vector2[]? uvs) =>
        MeshOf(vertices, colours, uvs);

    private static ArrayMesh MeshOf(Vector2[] points, Color[]? colours, Vector2[]? uvs)
    {
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = points;

        if (colours is not null)
        {
            arrays[(int)Mesh.ArrayType.Color] = colours;
        }

        if (uvs is not null)
        {
            arrays[(int)Mesh.ArrayType.TexUV] = uvs;
        }

        var mesh = new ArrayMesh();
        Refii.Core.Engine_ArrayMeshes.AddSurface(mesh, Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    private static SwfVectorPart? Textured(
        SwfFillMesh mesh,
        SwfFillStyle style,
        Func<int, Texture2D?>? hostBitmap,
        Func<int, Texture2D?>? embeddedBitmap)
    {
        SwfMatrix matrix = style.Matrix;
        Texture2D? texture = null;
        var repeat = false;

        Vector2 space = Vector2.One;

        if (style.BitmapId >= 0)
        {
            if (style.BitmapId == SwfRasterizer.HostSuppliedBitmapId)
            {
                return null;
            }

            Texture2D? embedded = embeddedBitmap?.Invoke(style.BitmapId);
            texture = hostBitmap?.Invoke(style.BitmapId) ?? embedded;
            if (texture is null)
            {
                return null;
            }

            Texture2D reference = embedded ?? texture;
            space = new Vector2(
                Mathf.Max(reference.GetWidth(), 1), Mathf.Max(reference.GetHeight(), 1));

            repeat = style.Kind is SwfFillKind.RepeatingBitmap
                or SwfFillKind.NonSmoothedRepeatingBitmap;
        }
        else
        {
            texture = Gradient(style);
            if (texture is null)
            {
                return null;
            }
        }

        Transform2D fill = ToTransform(matrix);
        float determinant = (fill.X.X * fill.Y.Y) - (fill.X.Y * fill.Y.X);
        Transform2D inverse = Mathf.IsZeroApprox(determinant)
            ? Transform2D.Identity
            : fill.AffineInverse();

        var points = new Vector2[mesh.Vertices.Length];
        var uvs = new Vector2[mesh.Vertices.Length];
        bool isGradient = style.BitmapId < 0;

        var texel = new Vector2(1f / space.X, 1f / space.Y);

        for (var i = 0; i < mesh.Vertices.Length; i++)
        {
            points[i] = mesh.Vertices[i];

            Vector2 local = inverse * (mesh.Vertices[i] * SwfShapeGeometry.TwipsPerPixel);
            uvs[i] = isGradient ? GradientUv(style, local) : local * texel;
        }

        return new SwfVectorPart
        {
            Mesh = MeshOf(points, colours: null, uvs),
            Texture = texture,

            Repeat = repeat,
            FirstFill = mesh.FillStyle,
            Vertices = points,
            Uvs = uvs,
        };
    }

    private static Vector2 GradientUv(SwfFillStyle style, Vector2 local)
    {
        if (style.Kind == SwfFillKind.LinearGradient)
        {
            float t = (local.X + GradientHalfSpan) / (GradientHalfSpan * 2f);
            return new Vector2(Mathf.Clamp(t, 0f, 1f), 0.5f);
        }

        return (local / (GradientHalfSpan * 2f)) + new Vector2(0.5f, 0.5f);
    }

    public static Texture2D? GradientTexture(SwfFillStyle style) => Gradient(style);

    private static Texture2D? Gradient(SwfFillStyle style)
    {
        if (style.Stops.Count == 0)
        {
            return null;
        }

        var gradient = new Gradient();

        var offsets = new float[style.Stops.Count];
        var colours = new Color[style.Stops.Count];
        for (var i = 0; i < style.Stops.Count; i++)
        {
            offsets[i] = style.Stops[i].Ratio / 255f;
            colours[i] = ToColor(style.Stops[i].Color);
        }

        gradient.Offsets = offsets;
        gradient.Colors = colours;

        if (style.Kind == SwfFillKind.LinearGradient)
        {
            return new GradientTexture1D { Gradient = gradient, Width = GradientResolution };
        }

        return new GradientTexture2D
        {
            Gradient = gradient,
            Width = GradientResolution,
            Height = GradientResolution,
            Fill = GradientTexture2D.FillEnum.Radial,
            FillFrom = new Vector2(0.5f, 0.5f),
            FillTo = new Vector2(1.0f, 0.5f),
        };
    }

    private static Color ToColor(SwfColor colour) =>
        new(colour.R / 255f, colour.G / 255f, colour.B / 255f, colour.A / 255f);

    private static Transform2D ToTransform(SwfMatrix matrix) =>
        new(
            new Vector2(matrix.ScaleX, matrix.RotateSkew0),
            new Vector2(matrix.RotateSkew1, matrix.ScaleY),
            new Vector2(matrix.TranslateX, matrix.TranslateY));
}
