using System;
using System.Collections.Generic;
using Godot;

namespace Refii.FlashGames;

public readonly record struct SwfGlyphPlacement(int GlyphIndex, float X, float Y);

public static class SwfTextRenderer
{
    public static string PlainText(string text, bool html)
    {
        if (!html || text.Length == 0)
        {
            return text;
        }

        var output = new System.Text.StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '<')
            {
                int close = text.IndexOf('>', i + 1);
                if (close < 0)
                {
                    break;
                }

                string tag = text[(i + 1)..close].Trim().ToLowerInvariant();
                if (tag.StartsWith("br", StringComparison.Ordinal) ||
                    tag.StartsWith("/p", StringComparison.Ordinal))
                {
                    output.Append('\n');
                }

                i = close + 1;
                continue;
            }

            if (c == '&')
            {
                int semi = text.IndexOf(';', i + 1);
                if (semi > i && semi - i <= 8)
                {
                    output.Append(Entity(text[(i + 1)..semi]));
                    i = semi + 1;
                    continue;
                }
            }

            output.Append(c);
            i++;
        }

        return output.ToString().Trim('\n');
    }

    private static string Entity(string name) => name switch
    {
        "amp" => "&",
        "lt" => "<",
        "gt" => ">",
        "quot" => "\"",
        "apos" => "'",
        "nbsp" => " ",
        _ => name.StartsWith("#", StringComparison.Ordinal) &&
             int.TryParse(name[1..], out int code)
            ? ((char)code).ToString()
            : string.Empty,
    };

    public static List<SwfGlyphPlacement> Layout(
        SwfEditText field, SwfFont font, string text, out float usedWidth)
    {
        var placements = new List<SwfGlyphPlacement>();
        usedWidth = 0f;

        if (text.Length == 0 || font.Glyphs.Count == 0 || field.FontHeight <= 0)
        {
            return placements;
        }

        float scale = field.FontHeight / font.UnitsPerEm;
        float boxWidth = field.Bounds.Width - field.LeftMargin - field.RightMargin;
        float lineHeight = ((font.Ascent + font.Descent) * scale) + field.Leading;
        if (lineHeight <= 0f)
        {
            lineHeight = field.FontHeight * 1.2f;
        }

        List<string> lines = field.WordWrap && boxWidth > 0
            ? WrapLines(text, font, scale, boxWidth)
            : new List<string>(text.Split('\n'));

        if (!field.Multiline && lines.Count > 1)
        {
            lines = new List<string> { lines[0] };
        }

        float baseline = field.Bounds.YMin + (font.Ascent * scale);

        foreach (string line in lines)
        {
            float width = MeasureLine(line, font, scale);
            usedWidth = Mathf.Max(usedWidth, width);

            float x = field.Align switch
            {
                SwfTextAlign.Right => field.Bounds.XMax - field.RightMargin - width,
                SwfTextAlign.Center =>
                    field.Bounds.XMin + field.LeftMargin + ((boxWidth - width) * 0.5f),
                _ => field.Bounds.XMin + field.LeftMargin + field.Indent,
            };

            foreach (char c in line)
            {
                int index = font.GlyphFor(c);
                if (index >= 0)
                {
                    placements.Add(new SwfGlyphPlacement(index, x, baseline));
                    x += font.Glyphs[index].Advance * scale;
                }
                else if (c == ' ')
                {
                    x += font.UnitsPerEm * 0.25f * scale;
                }
            }

            baseline += lineHeight;
        }

        return placements;
    }

    public static float MeasureLine(string line, SwfFont font, float scale)
    {
        float width = 0f;
        foreach (char c in line)
        {
            int index = font.GlyphFor(c);
            width += index >= 0
                ? font.Glyphs[index].Advance * scale
                : font.UnitsPerEm * 0.25f * scale;
        }

        return width;
    }

    private static List<string> WrapLines(string text, SwfFont font, float scale, float boxWidth)
    {
        var lines = new List<string>();
        foreach (string paragraph in text.Split('\n'))
        {
            string current = string.Empty;
            foreach (string word in paragraph.Split(' '))
            {
                string candidate = current.Length == 0 ? word : current + " " + word;
                if (current.Length != 0 && MeasureLine(candidate, font, scale) > boxWidth)
                {
                    lines.Add(current);
                    current = word;
                }
                else
                {
                    current = candidate;
                }
            }

            lines.Add(current);
        }

        return lines;
    }

    public static void Draw(
        SwfCanvas canvas, SwfEditText field, SwfFont font, string text,
        Transform2D fieldToCanvas, SwfColorTransform tint)
    {
        List<SwfGlyphPlacement> placements =
            Layout(field, font, PlainText(text, field.Html), out _);
        if (placements.Count == 0)
        {
            return;
        }

        float scale = field.FontHeight / font.UnitsPerEm;
        SwfColorTransform coloured = ApplyColour(tint, field.Color);

        foreach (SwfGlyphPlacement placement in placements)
        {
            SwfGlyph glyph = font.Glyphs[placement.GlyphIndex];
            if (glyph.Shape is null || glyph.Geometry is null)
            {
                continue;
            }

            var glyphToField = new Transform2D(
                new Vector2(scale, 0f),
                new Vector2(0f, scale),
                new Vector2(placement.X, placement.Y));

            SwfRasterizer.DrawInto(
                canvas, glyph.Shape, glyph.Geometry,
                fieldToCanvas * glyphToField, coloured, null);
        }
    }

    private static SwfColorTransform ApplyColour(SwfColorTransform tint, SwfColor colour)
    {
        return new SwfColorTransform(
            tint.RedMultiply * (colour.R / 255f),
            tint.GreenMultiply * (colour.G / 255f),
            tint.BlueMultiply * (colour.B / 255f),
            tint.AlphaMultiply * (colour.A / 255f),
            tint.RedAdd,
            tint.GreenAdd,
            tint.BlueAdd,
            tint.AlphaAdd);
    }
}
