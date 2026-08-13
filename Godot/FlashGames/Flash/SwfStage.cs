using System;
using System.Collections.Generic;
using Godot;
using Refii.FlashGames.PubGames;

namespace Refii.FlashGames;

public sealed class SwfStage
{
    private readonly Dictionary<int, SwfBitmapSampler?> _embedded = new();
    private readonly Dictionary<int, SwfBitmapSampler?> _host = new();

    public SwfStage(SwfDocument document, SwfRect frameSize, Func<int, Image?>? hostResolver = null)
    {
        Document = document;
        FrameSize = frameSize;
        HostResolver = hostResolver;
        Root = new SwfClip(this, null, -1, 0, "_root");
        BuildRoot();
    }

    public SwfDocument Document { get; }

    public SwfRect FrameSize { get; }

    public Func<int, Image?>? HostResolver { get; set; }

    public PubGameFonts? Fonts { get; set; }

    public SwfClip Root { get; }

    public int MaxDrawCalls { get; set; } = 20000;

    public int MaxDepth { get; set; } = 12;

    private void BuildRoot()
    {
        if (Document.RootFrames.Count == 0)
        {
            return;
        }

        foreach (SwfPlaceObject place in Document.RootFrames[0].Places)
        {
            if (place.CharacterId < 0)
            {
                continue;
            }

            Root.AttachPlaced(place);
        }
    }

    public void Advance() => Root.Advance();

    public Image Render(int width, int height, Color background, int supersample = 1)
    {
        int factor = Mathf.Clamp(supersample, 1, 4);
        int w = width * factor;
        int h = height * factor;
        var canvas = new SwfCanvas(w, h, background);

        float stageWidth = Mathf.Max(FrameSize.Width, 1);
        float stageHeight = Mathf.Max(FrameSize.Height, 1);
        float scale = Mathf.Min(w / stageWidth, h / stageHeight);
        var fit = new Transform2D(
            new Vector2(scale, 0f),
            new Vector2(0f, scale),
            new Vector2(
                ((w - (stageWidth * scale)) * 0.5f) - (FrameSize.XMin * scale),
                ((h - (stageHeight * scale)) * 0.5f) - (FrameSize.YMin * scale)));

        int budget = MaxDrawCalls;
        DrawClip(Root, fit, SwfColorTransform.Identity, canvas, 0, ref budget);

        Image image = canvas.ToImage();
        if (factor > 1)
        {
            image.Resize(width, height, Image.Interpolation.Lanczos);
        }

        return image;
    }

    private void DrawClip(
        SwfClip clip, Transform2D parent, SwfColorTransform colour,
        SwfCanvas canvas, int depth, ref int budget)
    {
        if (!clip.Visible || budget <= 0 || depth > MaxDepth)
        {
            return;
        }

        Transform2D transform = clip.CharacterId < 0
            ? parent
            : parent * clip.LocalTransform();

        SwfColorTransform tint = SwfComposer.Combine(colour, clip.Tint);
        if (clip.Alpha < 100f)
        {
            tint = tint with { AlphaMultiply = tint.AlphaMultiply * (clip.Alpha / 100f) };
        }

        if (tint.AlphaMultiply <= 0f && tint.AlphaAdd <= 0f)
        {
            return;
        }

        if (Document.Shapes.TryGetValue(clip.CharacterId, out SwfShape? shape) &&
            Document.Geometry.TryGetValue(clip.CharacterId, out SwfGeometry? geometry))
        {
            budget--;
            SwfRasterizer.DrawInto(canvas, shape, geometry, transform, tint, Embedded, Host);
            return;
        }

        if (Document.EditTexts.TryGetValue(clip.CharacterId, out SwfEditText? field))
        {
            budget--;
            string content = clip.Text.Length != 0 ? clip.Text : field.InitialText;
            SwfFont? font = Fonts?.Resolve(field.FontId)
                ?? Document.Fonts.GetValueOrDefault(field.FontId);
            if (content.Length != 0 && font is not null && font.Glyphs.Count != 0)
            {
                SwfTextRenderer.Draw(canvas, field, font, content, transform, tint);
            }

            return;
        }

        foreach (SwfClip child in clip.Children.Values)
        {
            DrawClip(child, transform, tint, canvas, depth + 1, ref budget);
        }
    }

    public SwfBitmapSampler? EmbeddedSampler(int id) => Embedded(id);

    public SwfBitmapSampler? HostSampler(int id) => Host(id);

    private SwfBitmapSampler? Embedded(int id) =>
        Lookup(_embedded, id, () => Document.Bitmaps.GetValueOrDefault(id));

    private SwfBitmapSampler? Host(int id) =>
        Lookup(_host, id, () => HostResolver?.Invoke(id));

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
