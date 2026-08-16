using System;
using System.Collections.Generic;
using Godot;

namespace Refii.FlashGames.Runtime;

/// <summary>
/// One SWF shape as Godot geometry, drawn by the GPU at whatever size the window is.
/// </summary>
/// <remarks>
/// THE REPLACEMENT FOR RASTERIZING THE ARTWORK. <see cref="SwfRasterizer"/> fills the shape into a
/// bitmap at the size it was authored at, and the stage is then magnified to fill the window —
/// about 2.3x at 1080p from a 640x462 stage — so the board was always a stretched authored-size
/// image. The only lever was to rasterize larger, and that is quadratic: keystone's 234 placed
/// shapes cost 109 MB and 4.6 s at authored size, and 348 MB and 11.9 s at 2.5x. The same 234
/// shapes are 28.8 MB of vertices and 1.65 s as triangles, and triangles have no resolution —
/// resizing the window costs nothing at all, because there is nothing to rebuild.
///
/// THE RASTERIZER IS STILL THE SPECIFICATION. Every rule here is taken from
/// <see cref="SwfRasterizer.DrawInto"/> rather than from the SWF specification, because that is
/// what the shipped artwork was verified against: even-odd fills, the host bitmap substitution,
/// the gradient parameterisation over 16384 twips, and treating a focal gradient as radial. Where
/// a difference would be invisible it is still avoided, because "close enough" across two
/// implementations is how the same asset ends up looking like two different assets.
///
/// BATCHED IN PAINT ORDER, which is the constraint that decides the node layout. Fills within a
/// shape overlap and the later one wins, so they cannot be reordered to group them by texture. A
/// run of consecutive solid fills becomes ONE <see cref="Polygon2D"/> carrying per-vertex colours
/// — 83.2% of all fills in the shipped games are solid, so the runs are long — and a gradient or
/// bitmap fill ends the run and takes a node of its own. Ordering is then just child order.
/// </remarks>
/// <summary>
/// One drawable piece of a shape: a mesh, and the texture it is drawn with.
/// </summary>
/// <remarks>
/// A MESH RATHER THAN A NODE, because a shape is placed many times — keystone's board is 1,775
/// placements of 234 distinct shapes — and a node can only have one parent. Building nodes to
/// cache and duplicating them per placement worked and leaked: the cached originals are never in
/// the tree, so nothing frees them, and a run exited with 479 orphaned meshes and 667 orphaned
/// canvas items. An <see cref="ArrayMesh"/> is a resource, so every placement can reference the
/// same one, and it goes away with the last reference.
/// </remarks>
public sealed class SwfVectorPart
{
    public required ArrayMesh Mesh { get; init; }

    /// <summary>The gradient or bitmap this is drawn with, or null for per-vertex colour.</summary>
    public Texture2D? Texture { get; init; }

    /// <summary>Whether the bitmap tiles rather than being clipped to one copy.</summary>
    public bool Repeat { get; init; }

    public int FirstFill { get; init; }

    public required Vector2[] Vertices { get; init; }

    public Color[]? Colours { get; init; }

    public Vector2[]? Uvs { get; init; }
}

public static class SwfVectorShape
{
    /// <summary>Samples across a gradient texture. The rasterizer quantises stops to 1/255.</summary>
    private const int GradientResolution = 256;

    /// <summary>
    /// Half the span the SWF gradient square covers, in twips.
    /// </summary>
    /// <remarks>
    /// The gradient's own space is a 32768-twip square centred on the origin, which the fill
    /// matrix maps onto the shape. Taken from the rasterizer's <c>(local.X + 16384) / 32768</c>
    /// rather than from a specification, so the two cannot drift.
    /// </remarks>
    private const float GradientHalfSpan = 16384f;

    /// <summary>
    /// Builds the shape's geometry under a fresh <see cref="Node2D"/>, or null if it draws nothing.
    /// </summary>
    /// <param name="shape">The shape, for its fill styles.</param>
    /// <param name="geometry">Its flattened contours.</param>
    /// <param name="hostBitmap">
    /// The replacement artwork, by character id — the pub games' loose <c>.dds</c>, which is where
    /// the texture pipeline's upscaling lands. Consulted first, exactly as the rasterizer does.
    /// </param>
    /// <param name="embeddedBitmap">The SWF's own bitmaps, by character id.</param>
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
                    // ALREADY IN PIXELS. The contours are traced through
                    // SwfShapeGeometry.ToPixels, so the triangles are in the same space the scene
                    // places clips in and no conversion belongs here.
                    solidPoints.Add(point);
                    solidColours.Add(colour);
                }

                continue;
            }

            // A TEXTURED FILL ENDS THE RUN. Flushing here rather than at the end is what keeps the
            // paint order: everything solid so far is underneath this fill, and anything solid
            // after it goes on top in a new batch.
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

    /// <summary>Emits the accumulated solid run as one mesh and starts a new run.</summary>
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

    /// <summary>
    /// A triangle soup as a canvas mesh.
    /// </summary>
    /// <remarks>
    /// PrimitiveType.Triangles with NO index array, because the triangulator already emits three
    /// vertices per triangle — an index buffer would be 0,1,2,3,4,... and buy nothing.
    ///
    /// The vertex array is Vector2 rather than Vector3. Godot accepts a 2D vertex array for a mesh
    /// that is drawn by a canvas item, and giving it 3D vertices would silently work while
    /// doubling the geometry.
    /// </remarks>
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
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    /// <summary>One gradient or bitmap fill, as a textured part.</summary>
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
                // The rasterizer skips the fill entirely rather than drawing it flat, and a missing
                // bitmap drawn as a solid rectangle is far more visible than a missing one.
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

            // BACK TO TWIPS FOR THE MATRIX, because that is the space fill matrices are defined
            // in and the triangles are in pixels. This is the same multiply the rasterizer does
            // before it transforms a contour point.
            //
            // THE INVERSE FILL MATRIX MAPS SHAPE TWIPS TO THE FILL'S OWN SPACE, and for a bitmap
            // that space IS texture pixels — the rasterizer floors the result and indexes the
            // bitmap with it directly, which is only correct if the units are pixels.
            Vector2 local = inverse * (mesh.Vertices[i] * SwfShapeGeometry.TwipsPerPixel);
            uvs[i] = isGradient ? GradientUv(style, local) : local * texel;
        }

        // NO VERTEX COLOURS on a textured part. Godot multiplies the two, and an unset colour array
        // is white — but leaving it out says that plainly rather than relying on the default.
        return new SwfVectorPart
        {
            Mesh = MeshOf(points, colours: null, uvs),
            Texture = texture,

            // REPEAT IS A CANVAS ITEM PROPERTY, not a texture one, so it travels with the part and
            // is set on the node that draws — two fills of the same bitmap can disagree about it.
            Repeat = repeat,
            FirstFill = mesh.FillStyle,
            Vertices = points,
            Uvs = uvs,
        };
    }

    /// <summary>
    /// Where a point lands in the gradient's texture, in texture pixels.
    /// </summary>
    /// <remarks>
    /// The parameterisation is the rasterizer's: a linear gradient runs along the fill space's X
    /// over 32768 twips centred on the origin, and a radial one runs from the origin out to 16384.
    /// A FOCAL gradient is treated as radial, which is also what the rasterizer does — its focal
    /// point is parsed and then never consulted. 57 of 27,774 fills in the shipped games are
    /// focal, so matching the existing behaviour is worth more than being more correct than it.
    /// </remarks>
    private static Vector2 GradientUv(SwfFillStyle style, Vector2 local)
    {
        if (style.Kind == SwfFillKind.LinearGradient)
        {
            float t = (local.X + GradientHalfSpan) / (GradientHalfSpan * 2f);
            return new Vector2(Mathf.Clamp(t, 0f, 1f), 0.5f);
        }

        // The radial texture is a square whose centre is the gradient's origin and whose half
        // width is the gradient's radius, so the mapping is a scale and a shift into it.
        return (local / (GradientHalfSpan * 2f)) + new Vector2(0.5f, 0.5f);
    }

    /// <summary>The stops as a texture: 1D along X for linear, radial in a square otherwise.</summary>
    public static Texture2D? GradientTexture(SwfFillStyle style) => Gradient(style);

    private static Texture2D? Gradient(SwfFillStyle style)
    {
        if (style.Stops.Count == 0)
        {
            return null;
        }

        var gradient = new Gradient();

        // BUILT BY REPLACING THE DEFAULTS, not by adding to them. A fresh Gradient already has two
        // points, and adding stops to it leaves black and white at 0 and 1 fighting the real ones.
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
