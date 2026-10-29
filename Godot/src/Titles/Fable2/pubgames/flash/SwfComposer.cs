using System;
using System.Collections.Generic;
using Godot;

namespace Refii.Titles.Fable2.PubGames;

public sealed class SwfComposeOptions
{
    public int Width { get; init; } = 512;

    public int Height { get; init; } = 512;

    public Color Background { get; init; } = new(0f, 0f, 0f, 0f);

    public Func<int, Image?>? HostBitmapResolver { get; init; }

    public int MaxDepth { get; init; } = 12;

    public int MaxDrawCalls { get; init; } = 20000;

    public int Supersample { get; init; } = 2;
}

public static class SwfComposer
{
    public static Rect2 MeasureCharacter(SwfDocument document, int characterId, int depth = 0)
    {
        return Measure(document, characterId, depth, new Dictionary<int, Rect2>(), new HashSet<int>());
    }

    private static Rect2 Measure(
        SwfDocument document, int characterId, int depth,
        Dictionary<int, Rect2> cache, HashSet<int> active)
    {
        if (depth > 8 || !active.Add(characterId))
        {
            return new Rect2();
        }

        if (cache.TryGetValue(characterId, out Rect2 cached))
        {
            active.Remove(characterId);
            return cached;
        }

        if (document.Shapes.TryGetValue(characterId, out SwfShape? shape))
        {
            var box = new Rect2(
                shape.Bounds.XMin, shape.Bounds.YMin,
                Mathf.Max(shape.Bounds.Width, 1), Mathf.Max(shape.Bounds.Height, 1));
            cache[characterId] = box;
            active.Remove(characterId);
            return box;
        }

        if (!document.Sprites.TryGetValue(characterId, out SwfSprite? sprite))
        {
            active.Remove(characterId);
            return new Rect2();
        }

        bool any = false;
        var bounds = new Rect2();
        foreach (SwfPlaceObject place in FlattenFirstFrame(sprite))
        {
            if (place.CharacterId < 0)
            {
                continue;
            }

            Rect2 child = Measure(document, place.CharacterId, depth + 1, cache, active);
            if (child.Size == Vector2.Zero)
            {
                continue;
            }

            Transform2D matrix = place.HasMatrix ? ToTransform(place.Matrix) : Transform2D.Identity;
            Rect2 transformed = TransformRect(matrix, child);
            bounds = any ? bounds.Merge(transformed) : transformed;
            any = true;
        }

        cache[characterId] = bounds;
        active.Remove(characterId);
        return bounds;
    }

    public static Image RenderStage(
        SwfDocument document, SwfRect stage, SwfComposeOptions options, int frameIndex = 0)
    {
        int factor = Mathf.Clamp(options.Supersample, 1, 4);
        int width = options.Width * factor;
        int height = options.Height * factor;
        var canvas = new SwfCanvas(width, height, options.Background);

        float stageWidth = Mathf.Max(stage.Width, 1);
        float stageHeight = Mathf.Max(stage.Height, 1);
        float scale = Mathf.Min(width / stageWidth, height / stageHeight);
        var fit = new Transform2D(
            new Vector2(scale, 0f),
            new Vector2(0f, scale),
            new Vector2(
                ((width - (stageWidth * scale)) * 0.5f) - (stage.XMin * scale),
                ((height - (stageHeight * scale)) * 0.5f) - (stage.YMin * scale)));

        var samplers = new SamplerCache(document, options);
        int budget = options.MaxDrawCalls;

        var byDepth = new SortedDictionary<int, SwfPlaceObject>();
        for (int i = 0; i <= frameIndex && i < document.RootFrames.Count; i++)
        {
            foreach (int depth in document.RootFrames[i].Removes)
            {
                byDepth.Remove(depth);
            }

            foreach (SwfPlaceObject place in document.RootFrames[i].Places)
            {
                byDepth[place.Depth] = place;
            }
        }

        foreach (SwfPlaceObject place in byDepth.Values)
        {
            if (place.CharacterId < 0)
            {
                continue;
            }

            Transform2D child = place.HasMatrix ? fit * ToTransform(place.Matrix) : fit;
            SwfColorTransform tint = place.HasColorTransform
                ? place.ColorTransform
                : SwfColorTransform.Identity;
            Draw(document, place.CharacterId, child, tint, canvas, options, 0,
                ref budget, new HashSet<int>(), samplers);
        }

        Image image = canvas.ToImage();
        if (factor > 1)
        {
            image.Resize(options.Width, options.Height, Image.Interpolation.Lanczos);
        }

        return image;
    }

    public static Image RenderCharacter(
        SwfDocument document, int characterId, SwfComposeOptions options)
    {
        int factor = Mathf.Clamp(options.Supersample, 1, 4);
        int width = options.Width * factor;
        int height = options.Height * factor;
        var canvas = new SwfCanvas(width, height, options.Background);
        Rect2 bounds = MeasureCharacter(document, characterId);
        if (bounds.Size.X <= 0f || bounds.Size.Y <= 0f)
        {
            return canvas.ToImage();
        }

        var samplers = new SamplerCache(document, options);

        float scale = Mathf.Min(width / bounds.Size.X, height / bounds.Size.Y);
        var fit = new Transform2D(
            new Vector2(scale, 0f),
            new Vector2(0f, scale),
            new Vector2(
                (width - (bounds.Size.X * scale)) * 0.5f - (bounds.Position.X * scale),
                (height - (bounds.Size.Y * scale)) * 0.5f - (bounds.Position.Y * scale)));

        int budget = options.MaxDrawCalls;
        Draw(document, characterId, fit, SwfColorTransform.Identity, canvas, options, 0, ref budget, new HashSet<int>(), samplers);
        Image image = canvas.ToImage();
        if (factor > 1)
        {
            image.Resize(options.Width, options.Height, Image.Interpolation.Lanczos);
        }

        return image;
    }

    private static void Draw(
        SwfDocument document, int characterId, Transform2D transform,
        SwfColorTransform colour, SwfCanvas canvas, SwfComposeOptions options, int depth,
        ref int budget, HashSet<int> active, SamplerCache samplers)
    {
        if (depth > options.MaxDepth || budget <= 0 || !active.Add(characterId))
        {
            return;
        }

        if (document.Shapes.TryGetValue(characterId, out SwfShape? shape) &&
            document.Geometry.TryGetValue(characterId, out SwfGeometry? geometry))
        {
            budget--;

            SamplerCache cache = samplers;
            SwfRasterizer.DrawInto(
                canvas, shape, geometry, transform, colour,
                cache.Embedded, cache.Host);
            active.Remove(characterId);
            return;
        }

        if (!document.Sprites.TryGetValue(characterId, out SwfSprite? sprite))
        {
            active.Remove(characterId);
            return;
        }

        foreach (SwfPlaceObject place in FlattenFirstFrame(sprite))
        {
            if (place.CharacterId < 0)
            {
                continue;
            }

            Transform2D child = place.HasMatrix
                ? transform * ToTransform(place.Matrix)
                : transform;
            SwfColorTransform tint = place.HasColorTransform
                ? Combine(colour, place.ColorTransform)
                : colour;
            Draw(document, place.CharacterId, child, tint, canvas, options, depth + 1, ref budget, active, samplers);
        }

        active.Remove(characterId);
    }

    private sealed class SamplerCache
    {
        private readonly Dictionary<int, SwfBitmapSampler?> _embedded = new();
        private readonly Dictionary<int, SwfBitmapSampler?> _host = new();
        private readonly SwfDocument _document;
        private readonly SwfComposeOptions _options;

        public SamplerCache(SwfDocument document, SwfComposeOptions options)
        {
            _document = document;
            _options = options;
        }

        public SwfBitmapSampler? Embedded(int id) =>
            Lookup(_embedded, id, () => _document.Bitmaps.GetValueOrDefault(id));

        public SwfBitmapSampler? Host(int id) =>
            Lookup(_host, id, () => _options.HostBitmapResolver?.Invoke(id));

        private static SwfBitmapSampler? Lookup(
            Dictionary<int, SwfBitmapSampler?> cache, int id, Func<Image?> load)
        {
            if (cache.TryGetValue(id, out SwfBitmapSampler? existing))
            {
                return existing;
            }

            Image? source = load();
            SwfBitmapSampler? sampler = source is null ? null : new SwfBitmapSampler(source);
            cache[id] = sampler;
            return sampler;
        }
    }

    private static List<SwfPlaceObject> FlattenFirstFrame(SwfSprite sprite)
    {
        var byDepth = new SortedDictionary<int, SwfPlaceObject>();
        if (sprite.Frames.Count != 0)
        {
            foreach (SwfPlaceObject place in sprite.Frames[0].Places)
            {
                byDepth[place.Depth] = place;
            }
        }

        return new List<SwfPlaceObject>(byDepth.Values);
    }

    public static SwfColorTransform Combine(SwfColorTransform outer, SwfColorTransform inner)
    {
        return new SwfColorTransform(
            outer.RedMultiply * inner.RedMultiply,
            outer.GreenMultiply * inner.GreenMultiply,
            outer.BlueMultiply * inner.BlueMultiply,
            outer.AlphaMultiply * inner.AlphaMultiply,
            outer.RedAdd + inner.RedAdd,
            outer.GreenAdd + inner.GreenAdd,
            outer.BlueAdd + inner.BlueAdd,
            outer.AlphaAdd + inner.AlphaAdd);
    }

    public static Transform2D ToTransform(SwfMatrix matrix) =>
        new(
            new Vector2(matrix.ScaleX, matrix.RotateSkew0),
            new Vector2(matrix.RotateSkew1, matrix.ScaleY),
            new Vector2(matrix.TranslateX, matrix.TranslateY));

    private static Rect2 TransformRect(Transform2D transform, Rect2 rect)
    {
        Vector2 a = transform * rect.Position;
        Vector2 b = transform * new Vector2(rect.Position.X + rect.Size.X, rect.Position.Y);
        Vector2 c = transform * new Vector2(rect.Position.X, rect.Position.Y + rect.Size.Y);
        Vector2 d = transform * (rect.Position + rect.Size);

        float minX = Mathf.Min(Mathf.Min(a.X, b.X), Mathf.Min(c.X, d.X));
        float minY = Mathf.Min(Mathf.Min(a.Y, b.Y), Mathf.Min(c.Y, d.Y));
        float maxX = Mathf.Max(Mathf.Max(a.X, b.X), Mathf.Max(c.X, d.X));
        float maxY = Mathf.Max(Mathf.Max(a.Y, b.Y), Mathf.Max(c.Y, d.Y));
        return new Rect2(minX, minY, maxX - minX, maxY - minY);
    }
}
