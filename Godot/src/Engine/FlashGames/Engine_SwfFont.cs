using System;
using System.Collections.Generic;

using Godot;

namespace Refii.FlashGames;

public static class Engine_SwfFont
{
    public const int PixelSize = 64;

    private const int AtlasWidth = 1024;
    private const int Padding = 2;

    public static FontFile? Build(SwfFont font)
    {
        if (font.Glyphs.Count == 0 || font.CodeToGlyph.Count == 0)
        {
            return null;
        }

        float unitsPerEm = font.UnitsPerEm;
        float k = PixelSize / unitsPerEm;

        var cells = new List<Cell>();
        foreach ((int code, int index) in font.CodeToGlyph)
        {
            if (index < 0 || index >= font.Glyphs.Count)
            {
                continue;
            }

            SwfGlyph glyph = font.Glyphs[index];
            float advance = glyph.Advance * k;
            if (glyph.Shape is null || glyph.Geometry is null || !Bounds(glyph.Geometry, out Rect2 units))
            {
                cells.Add(new Cell { Code = code, Advance = advance });
                continue;
            }

            var px = new Rect2(units.Position * 20f * k, units.Size * 20f * k);
            int width = Mathf.CeilToInt(px.Size.X) + (Padding * 2);
            int height = Mathf.CeilToInt(px.Size.Y) + (Padding * 2);
            if (width <= Padding * 2 || height <= Padding * 2)
            {
                cells.Add(new Cell { Code = code, Advance = advance });
                continue;
            }

            var canvas = new SwfCanvas(width, height, new Color(1f, 1f, 1f, 0f));
            var placement = new Transform2D(
                new Vector2(k, 0f), new Vector2(0f, k),
                new Vector2(Padding - px.Position.X, Padding - px.Position.Y));
            SwfRasterizer.DrawInto(
                canvas, glyph.Shape, glyph.Geometry, placement, SwfColorTransform.Identity, null);

            cells.Add(new Cell
            {
                Code = code,
                Advance = advance,
                Image = canvas.ToImage(),
                Offset = new Vector2(px.Position.X - Padding, px.Position.Y - Padding),
            });
        }

        if (!font.CodeToGlyph.ContainsKey(' '))
        {
            cells.Add(new Cell { Code = ' ', Advance = PixelSize * 0.25f });
        }

        int cursorX = 0, cursorY = 0, rowHeight = 0;
        foreach (Cell cell in cells)
        {
            if (cell.Image is null)
            {
                continue;
            }

            int w = cell.Image.GetWidth();
            int h = cell.Image.GetHeight();
            if (cursorX + w > AtlasWidth && cursorX > 0)
            {
                cursorX = 0;
                cursorY += rowHeight;
                rowHeight = 0;
            }

            cell.Rect = new Rect2I(cursorX, cursorY, w, h);
            cursorX += w;
            rowHeight = Math.Max(rowHeight, h);
        }

        int atlasHeight = Math.Max(1, cursorY + rowHeight);
        var atlas = Image.CreateEmpty(AtlasWidth, atlasHeight, false, Image.Format.Rgba8);
        atlas.Fill(new Color(1f, 1f, 1f, 0f));
        foreach (Cell cell in cells)
        {
            if (cell.Image is not null)
            {
                Image source = cell.Image;
                if (source.GetFormat() != Image.Format.Rgba8)
                {
                    source.Convert(Image.Format.Rgba8);
                }

                atlas.BlitRect(source, new Rect2I(0, 0, source.GetWidth(), source.GetHeight()), cell.Rect.Position);
            }
        }

        atlas.GenerateMipmaps();

        var file = new FontFile
        {
            FixedSize = PixelSize,
            FixedSizeScaleMode = TextServer.FixedSizeScaleMode.Enabled,
            GenerateMipmaps = true,
            Antialiasing = TextServer.FontAntialiasing.Gray,
            SubpixelPositioning = TextServer.SubpixelPositioning.Disabled,
            FontName = font.Name,
            FontStyle = (font.Bold ? TextServer.FontStyle.Bold : 0) | (font.Italic ? TextServer.FontStyle.Italic : 0),
        };

        var size = new Vector2I(PixelSize, 0);
        file.SetTextureImage(0, size, 0, atlas);

        float ascent = font.Ascent > 0 ? font.Ascent * k : PixelSize * 0.8f;
        float descent = font.Descent > 0 ? font.Descent * k : PixelSize * 0.2f;
        file.SetCacheAscent(0, PixelSize, ascent);
        file.SetCacheDescent(0, PixelSize, descent);
        file.SetCacheScale(0, PixelSize, 1f);
        file.SetCacheUnderlinePosition(0, PixelSize, descent * 0.5f);
        file.SetCacheUnderlineThickness(0, PixelSize, Math.Max(1f, PixelSize / 16f));

        foreach (Cell cell in cells)
        {
            int glyph = cell.Code;
            file.SetGlyphAdvance(0, PixelSize, glyph, new Vector2(cell.Advance, 0f));
            if (cell.Image is null)
            {
                file.SetGlyphTextureIdx(0, size, glyph, -1);
                file.SetGlyphSize(0, size, glyph, Vector2.Zero);
                file.SetGlyphOffset(0, size, glyph, Vector2.Zero);
                file.SetGlyphUVRect(0, size, glyph, new Rect2());
                continue;
            }

            file.SetGlyphTextureIdx(0, size, glyph, 0);
            file.SetGlyphUVRect(0, size, glyph, new Rect2(cell.Rect.Position, cell.Rect.Size));
            file.SetGlyphSize(0, size, glyph, cell.Rect.Size);
            file.SetGlyphOffset(0, size, glyph, cell.Offset);
        }

        return file;
    }

    public static float LeadingFor(SwfFont font, int fontHeightTwips, int leadingTwips)
    {
        float scale = fontHeightTwips / font.UnitsPerEm;
        float lineHeight = ((font.Ascent + font.Descent) * scale) + leadingTwips;
        float natural = (font.Ascent + font.Descent) * scale;
        return Math.Max(0f, lineHeight - natural) / SwfShapeGeometry.TwipsPerPixel;
    }

    private static bool Bounds(SwfGeometry geometry, out Rect2 bounds)
    {
        var min = new Vector2(float.MaxValue, float.MaxValue);
        var max = new Vector2(float.MinValue, float.MinValue);
        var any = false;
        foreach (SwfContour contour in geometry.Contours)
        {
            foreach (Vector2 point in contour.Points)
            {
                any = true;
                min = new Vector2(Math.Min(min.X, point.X), Math.Min(min.Y, point.Y));
                max = new Vector2(Math.Max(max.X, point.X), Math.Max(max.Y, point.Y));
            }
        }

        bounds = any ? new Rect2(min, max - min) : new Rect2();
        return any && bounds.Size.X > 0f && bounds.Size.Y > 0f;
    }

    private sealed class Cell
    {
        public int Code;
        public float Advance;
        public Image? Image;
        public Vector2 Offset;
        public Rect2I Rect;
    }
}
