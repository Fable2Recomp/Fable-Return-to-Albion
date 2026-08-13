using System;
using System.Collections.Generic;
using Godot;

namespace Refii.FlashGames;

public sealed class SwfRasterizeOptions
{
    public int Width { get; init; } = 512;

    public int Height { get; init; } = 512;

    public Color Background { get; init; } = new(0f, 0f, 0f, 0f);

    public Func<int, Image?>? BitmapResolver { get; init; }

    public int Supersample { get; init; } = 2;
}

public static class SwfRasterizer
{
    public const int HostSuppliedBitmapId = 65535;

    public static Image Render(SwfShape shape, SwfGeometry geometry, SwfRasterizeOptions options)
    {
        int scaleFactor = Mathf.Clamp(options.Supersample, 1, 4);
        int width = options.Width * scaleFactor;
        int height = options.Height * scaleFactor;

        Image image = Image.CreateEmpty(width, height, false, Image.Format.Rgba8);
        image.Fill(options.Background);

        float boundsWidth = Mathf.Max(shape.Bounds.Width / SwfShapeGeometry.TwipsPerPixel, 1f);
        float boundsHeight = Mathf.Max(shape.Bounds.Height / SwfShapeGeometry.TwipsPerPixel, 1f);
        float scale = Mathf.Min(width / boundsWidth, height / boundsHeight);
        var origin = new Vector2(
            shape.Bounds.XMin / SwfShapeGeometry.TwipsPerPixel,
            shape.Bounds.YMin / SwfShapeGeometry.TwipsPerPixel);

        var byFill = new SortedDictionary<int, List<List<Vector2>>>();
        foreach (SwfContour contour in geometry.Contours)
        {
            if (contour.FillStyle <= 0 || contour.FillStyle > shape.FillStyles.Count)
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

        foreach ((int fillIndex, List<List<Vector2>> rings) in byFill)
        {
            SwfFillStyle style = shape.FillStyles[fillIndex - 1];
            FillRings(image, rings, style, options, origin, scale, width, height);
        }

        if (scaleFactor > 1)
        {
            image.Resize(options.Width, options.Height, Image.Interpolation.Lanczos);
        }

        return image;
    }


    public static void DrawInto(
        SwfCanvas canvas, SwfShape shape, SwfGeometry geometry, Transform2D shapeToCanvas,
        SwfColorTransform tint, Func<int, SwfBitmapSampler?>? resolver)
    {
        var byFill = new SortedDictionary<int, List<List<Vector2>>>();
        foreach (SwfContour contour in geometry.Contours)
        {
            if (contour.FillStyle <= 0 || contour.FillStyle > shape.FillStyles.Count)
            {
                continue;
            }

            var transformed = new List<Vector2>(contour.Points.Count);
            foreach (Vector2 point in contour.Points)
            {
                transformed.Add(shapeToCanvas * (point * SwfShapeGeometry.TwipsPerPixel));
            }

            if (!byFill.TryGetValue(contour.FillStyle, out List<List<Vector2>>? rings))
            {
                rings = new List<List<Vector2>>();
                byFill[contour.FillStyle] = rings;
            }

            rings.Add(transformed);
        }

        Transform2D canvasToShape = shapeToCanvas.AffineInverse();
        foreach ((int fillIndex, List<List<Vector2>> rings) in byFill)
        {
            SwfFillStyle style = shape.FillStyles[fillIndex - 1];
            SwfBitmapSampler? bitmap = style.BitmapId >= 0 ? resolver?.Invoke(style.BitmapId) : null;
            if (style.BitmapId >= 0 && bitmap is null)
            {
                continue;
            }

            bool isGradient = style.Kind is SwfFillKind.LinearGradient
                or SwfFillKind.RadialGradient or SwfFillKind.FocalGradient;
            Transform2D fillInverse = Transform2D.Identity;
            if (bitmap is not null || isGradient)
            {
                Transform2D fill = ToTransform(style.Matrix);
                float determinant = (fill.X.X * fill.Y.Y) - (fill.X.Y * fill.Y.X);
                fillInverse = Mathf.IsZeroApprox(determinant)
                    ? Transform2D.Identity
                    : fill.AffineInverse();
            }

            ScanFill(canvas, rings, style, bitmap, fillInverse, isGradient, canvasToShape, tint);
        }
    }

    private struct ScanEdge
    {
        public float X;
        public float Step;
        public int LastScanline;
    }

    private static void ScanFill(
        SwfCanvas canvas, List<List<Vector2>> rings, SwfFillStyle style, SwfBitmapSampler? bitmap,
        Transform2D fillInverse, bool isGradient, Transform2D canvasToShape, SwfColorTransform tint)
    {
        float minY = float.MaxValue;
        float maxY = float.MinValue;
        foreach (List<Vector2> ring in rings)
        {
            foreach (Vector2 point in ring)
            {
                minY = Mathf.Min(minY, point.Y);
                maxY = Mathf.Max(maxY, point.Y);
            }
        }

        if (minY > maxY)
        {
            return;
        }

        int y0 = Mathf.Max(Mathf.CeilToInt(minY - 0.5f), 0);
        int y1 = Mathf.Min(Mathf.CeilToInt(maxY - 0.5f) - 1, canvas.Height - 1);
        if (y1 < y0)
        {
            return;
        }

        int lines = y1 - y0 + 1;
        var buckets = new List<ScanEdge>?[lines];

        foreach (List<Vector2> ring in rings)
        {
            int count = ring.Count;
            for (int i = 0; i < count; i++)
            {
                Vector2 a = ring[i];
                Vector2 b = ring[(i + 1) % count];
                if (a.Y == b.Y)
                {
                    continue;
                }

                Vector2 top = a.Y < b.Y ? a : b;
                Vector2 bottom = a.Y < b.Y ? b : a;
                int first = Mathf.CeilToInt(top.Y - 0.5f);
                int last = Mathf.CeilToInt(bottom.Y - 0.5f) - 1;
                if (last < y0 || first > y1)
                {
                    continue;
                }

                float step = (bottom.X - top.X) / (bottom.Y - top.Y);
                int startLine = Mathf.Max(first, y0);
                float x = top.X + (((startLine + 0.5f) - top.Y) * step);
                int endLine = Mathf.Min(last, y1);

                int slot = startLine - y0;
                buckets[slot] ??= new List<ScanEdge>();
                buckets[slot]!.Add(new ScanEdge { X = x, Step = step, LastScanline = endLine });
            }
        }

        var active = new List<ScanEdge>();
        var crossings = new List<float>();

        for (int y = y0; y <= y1; y++)
        {
            List<ScanEdge>? entering = buckets[y - y0];
            if (entering is not null)
            {
                active.AddRange(entering);
            }

            if (active.Count == 0)
            {
                continue;
            }

            crossings.Clear();
            foreach (ScanEdge edge in active)
            {
                crossings.Add(edge.X);
            }

            crossings.Sort();
            for (int i = 0; i + 1 < crossings.Count; i += 2)
            {
                int x0 = Mathf.Max(Mathf.CeilToInt(crossings[i] - 0.5f), 0);
                int x1 = Mathf.Min(Mathf.FloorToInt(crossings[i + 1] - 0.5f), canvas.Width - 1);
                for (int x = x0; x <= x1; x++)
                {
                    Vector2 shapePoint = canvasToShape * new Vector2(x + 0.5f, y + 0.5f);
                    Color colour = SampleTwips(style, bitmap, fillInverse, isGradient, shapePoint);
                    colour = Tint(colour, tint);
                    canvas.Blend(x, y, colour.R, colour.G, colour.B, colour.A);
                }
            }

            for (int i = active.Count - 1; i >= 0; i--)
            {
                if (active[i].LastScanline <= y)
                {
                    active.RemoveAt(i);
                    continue;
                }

                ScanEdge edge = active[i];
                edge.X += edge.Step;
                active[i] = edge;
            }
        }
    }

    private static Color Tint(Color colour, SwfColorTransform tint)
    {
        return new Color(
            Mathf.Clamp((colour.R * tint.RedMultiply) + (tint.RedAdd / 255f), 0f, 1f),
            Mathf.Clamp((colour.G * tint.GreenMultiply) + (tint.GreenAdd / 255f), 0f, 1f),
            Mathf.Clamp((colour.B * tint.BlueMultiply) + (tint.BlueAdd / 255f), 0f, 1f),
            Mathf.Clamp((colour.A * tint.AlphaMultiply) + (tint.AlphaAdd / 255f), 0f, 1f));
    }

    private static Color SampleTwips(
        SwfFillStyle style, SwfBitmapSampler? bitmap, Transform2D fillInverse, bool isGradient, Vector2 twips)
    {
        if (bitmap is not null)
        {
            Vector2 local = fillInverse * twips;
            int px = Mathf.FloorToInt(local.X);
            int py = Mathf.FloorToInt(local.Y);
            int w = bitmap.Width;
            int h = bitmap.Height;
            bool clamp = style.Kind is SwfFillKind.ClippedBitmap
                or SwfFillKind.NonSmoothedClippedBitmap;
            if (!clamp && w > 0 && h > 0)
            {
                px = ((px % w) + w) % w;
                py = ((py % h) + h) % h;
            }

            return bitmap.TrySample(px, py, out float br, out float bg, out float bb, out float ba)
                ? new Color(br, bg, bb, ba)
                : new Color(0f, 0f, 0f, 0f);
        }

        if (isGradient)
        {
            Vector2 local = fillInverse * twips;
            float t = style.Kind == SwfFillKind.LinearGradient
                ? (local.X + 16384f) / 32768f
                : local.Length() / 16384f;
            return SampleStops(style.Stops, Mathf.Clamp(t, 0f, 1f));
        }

        return new Color(
            style.Color.R / 255f, style.Color.G / 255f, style.Color.B / 255f, style.Color.A / 255f);
    }

    private static void FillRings(
        Image image, List<List<Vector2>> rings, SwfFillStyle style, SwfRasterizeOptions options,
        Vector2 origin, float scale, int width, int height)
    {
        Image? bitmap = null;
        Transform2D bitmapInverse = Transform2D.Identity;
        if (style.BitmapId >= 0)
        {
            bitmap = options.BitmapResolver?.Invoke(style.BitmapId);
            if (bitmap is null)
            {
                return;
            }

            if (bitmap.GetFormat() != Image.Format.Rgba8)
            {
                bitmap.Convert(Image.Format.Rgba8);
            }

            bitmapInverse = ToTransform(style.Matrix).AffineInverse();
        }

        Transform2D gradientInverse = Transform2D.Identity;
        bool isGradient = style.Kind is SwfFillKind.LinearGradient
            or SwfFillKind.RadialGradient or SwfFillKind.FocalGradient;
        if (isGradient)
        {
            gradientInverse = ToTransform(style.Matrix).AffineInverse();
        }

        var crossings = new List<float>();
        for (int y = 0; y < height; y++)
        {
            float sampleY = ((y + 0.5f) / scale) + origin.Y;
            crossings.Clear();
            foreach (List<Vector2> ring in rings)
            {
                int count = ring.Count;
                for (int i = 0; i < count; i++)
                {
                    Vector2 a = ring[i];
                    Vector2 b = ring[(i + 1) % count];
                    if (a.Y == b.Y)
                    {
                        continue;
                    }

                    if ((sampleY >= a.Y && sampleY < b.Y) || (sampleY >= b.Y && sampleY < a.Y))
                    {
                        float t = (sampleY - a.Y) / (b.Y - a.Y);
                        crossings.Add(a.X + (t * (b.X - a.X)));
                    }
                }
            }

            if (crossings.Count < 2)
            {
                continue;
            }

            crossings.Sort();
            for (int i = 0; i + 1 < crossings.Count; i += 2)
            {
                int x0 = Mathf.CeilToInt(((crossings[i] - origin.X) * scale) - 0.5f);
                int x1 = Mathf.FloorToInt(((crossings[i + 1] - origin.X) * scale) - 0.5f);
                x0 = Mathf.Max(x0, 0);
                x1 = Mathf.Min(x1, width - 1);
                for (int x = x0; x <= x1; x++)
                {
                    Color colour = Sample(
                        style, bitmap, bitmapInverse, isGradient, gradientInverse,
                        ((x + 0.5f) / scale) + origin.X, sampleY);
                    if (colour.A <= 0f)
                    {
                        continue;
                    }

                    Blend(image, x, y, colour);
                }
            }
        }
    }

    private static Color Sample(
        SwfFillStyle style, Image? bitmap, Transform2D bitmapInverse,
        bool isGradient, Transform2D gradientInverse, float worldX, float worldY)
    {
        if (bitmap is not null)
        {
            Vector2 local = bitmapInverse * new Vector2(
                worldX * SwfShapeGeometry.TwipsPerPixel,
                worldY * SwfShapeGeometry.TwipsPerPixel);
            int px = Mathf.FloorToInt(local.X);
            int py = Mathf.FloorToInt(local.Y);
            bool clamp = style.Kind is SwfFillKind.ClippedBitmap
                or SwfFillKind.NonSmoothedClippedBitmap;
            int w = bitmap.GetWidth();
            int h = bitmap.GetHeight();
            if (clamp)
            {
                if (px < 0 || py < 0 || px >= w || py >= h)
                {
                    return new Color(0f, 0f, 0f, 0f);
                }
            }
            else
            {
                px = ((px % w) + w) % w;
                py = ((py % h) + h) % h;
            }

            return bitmap.GetPixel(px, py);
        }

        if (isGradient)
        {
            Vector2 local = gradientInverse * new Vector2(
                worldX * SwfShapeGeometry.TwipsPerPixel,
                worldY * SwfShapeGeometry.TwipsPerPixel);
            float t = style.Kind == SwfFillKind.LinearGradient
                ? (local.X + 16384f) / 32768f
                : local.Length() / 16384f;
            return SampleStops(style.Stops, Mathf.Clamp(t, 0f, 1f));
        }

        return new Color(
            style.Color.R / 255f, style.Color.G / 255f, style.Color.B / 255f, style.Color.A / 255f);
    }

    private static Color SampleStops(List<SwfGradientStop> stops, float t)
    {
        if (stops.Count == 0)
        {
            return new Color(0f, 0f, 0f, 0f);
        }

        float target = t * 255f;
        SwfGradientStop first = stops[0];
        if (target <= first.Ratio)
        {
            return ToColor(first.Color);
        }

        for (int i = 1; i < stops.Count; i++)
        {
            SwfGradientStop previous = stops[i - 1];
            SwfGradientStop current = stops[i];
            if (target <= current.Ratio)
            {
                float span = current.Ratio - previous.Ratio;
                float local = span <= 0f ? 0f : (target - previous.Ratio) / span;
                return ToColor(previous.Color).Lerp(ToColor(current.Color), local);
            }
        }

        return ToColor(stops[^1].Color);
    }

    private static Color ToColor(SwfColor colour) =>
        new(colour.R / 255f, colour.G / 255f, colour.B / 255f, colour.A / 255f);

    private static Transform2D ToTransform(SwfMatrix matrix) =>
        new(
            new Vector2(matrix.ScaleX, matrix.RotateSkew0),
            new Vector2(matrix.RotateSkew1, matrix.ScaleY),
            new Vector2(matrix.TranslateX, matrix.TranslateY));

    private static void Blend(Image image, int x, int y, Color source)
    {
        Color destination = image.GetPixel(x, y);
        float alpha = source.A;
        float outAlpha = alpha + (destination.A * (1f - alpha));
        if (outAlpha <= 0f)
        {
            image.SetPixel(x, y, new Color(0f, 0f, 0f, 0f));
            return;
        }

        image.SetPixel(x, y, new Color(
            ((source.R * alpha) + (destination.R * destination.A * (1f - alpha))) / outAlpha,
            ((source.G * alpha) + (destination.G * destination.A * (1f - alpha))) / outAlpha,
            ((source.B * alpha) + (destination.B * destination.A * (1f - alpha))) / outAlpha,
            outAlpha));
    }
}
