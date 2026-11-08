using System;
using Godot;

namespace Refii.Launcher;

/// <summary>
/// The launcher's close crosses and settings gear, drawn, on a phone.
/// </summary>
/// <remarks>
/// The scene writes them as text, "✕" (U+2715) and "⚙" (U+2699), which the bundled faces lack, so
/// the text server looked the glyphs up in the system's fonts and kept the font it found for the
/// rest of the process: 7.3 MB on a OnePlus 7 Pro (native heap sample, from a launcher Button).
/// The buttons show the same shapes as icons in the same colours instead, so no glyph sends the
/// text server to the system fonts. Replaced before the buttons are first measured or drawn.
/// </remarks>
public static class AndroidLauncherGlyphs
{
    /// <summary>The shapes drawn.</summary>
    public enum Glyph { Cross, Gear }

    /// <summary>A white icon of <paramref name="glyph"/> on <paramref name="button"/>, sized to its font, in its text colours.</summary>
    public static void Apply(Button button, Glyph glyph)
    {
        button.Text = string.Empty;
        int size = Math.Max(8, button.GetThemeFontSize("font_size"));
        if (button.Icon is ImageTexture current && current.GetWidth() == size) return;
        byte[] alpha = Coverage(glyph, size);
        var rgba = new byte[size * size * 4];
        for (int i = 0; i < alpha.Length; i++)
        {
            rgba[i * 4] = rgba[i * 4 + 1] = rgba[i * 4 + 2] = 255;
            rgba[i * 4 + 3] = alpha[i];
        }

        button.Icon = ImageTexture.CreateFromImage(Image.CreateFromData(size, size, false, Image.Format.Rgba8, rgba));
        button.IconAlignment = HorizontalAlignment.Center;
        foreach ((string icon, string font) in new[]
        {
            ("icon_normal_color", "font_color"), ("icon_hover_color", "font_hover_color"),
            ("icon_pressed_color", "font_pressed_color"), ("icon_focus_color", "font_focus_color"),
            ("icon_hover_pressed_color", "font_hover_pressed_color"), ("icon_disabled_color", "font_disabled_color"),
        })
        {
            button.AddThemeColorOverride(icon, button.GetThemeColor(font));
        }
    }

    /// <summary>The shape's coverage, 0-255, row by row in a <paramref name="size"/> square, 4x4 samples a pixel.</summary>
    public static byte[] Coverage(Glyph glyph, int size)
    {
        var coverage = new byte[size * size];
        const int Samples = 4;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int inside = 0;
                for (int sy = 0; sy < Samples; sy++)
                {
                    for (int sx = 0; sx < Samples; sx++)
                    {
                        // Centred unit square, y down.
                        float u = (x + (sx + 0.5f) / Samples) / size - 0.5f;
                        float v = (y + (sy + 0.5f) / Samples) / size - 0.5f;
                        if (glyph == Glyph.Cross ? InCross(u, v) : InGear(u, v)) inside++;
                    }
                }

                coverage[y * size + x] = (byte)(inside * 255 / (Samples * Samples));
            }
        }

        return coverage;
    }

    /// <summary>Two strokes corner to corner, as the text cross stood in the button.</summary>
    private static bool InCross(float u, float v)
    {
        const float Half = 0.28f, Stroke = 0.075f;
        if (MathF.Abs(u) > Half + Stroke || MathF.Abs(v) > Half + Stroke) return false;
        float along = MathF.Min(MathF.Max(-Half, (u + v) * 0.5f), Half);
        float across = MathF.Min(MathF.Max(-Half, (u - v) * 0.5f), Half);
        float a = MathF.Sqrt((u - along) * (u - along) + (v - along) * (v - along));
        float b = MathF.Sqrt((u - across) * (u - across) + (v + across) * (v + across));
        return MathF.Min(a, b) <= Stroke;
    }

    /// <summary>Eight teeth round a ring with a hole.</summary>
    private static bool InGear(float u, float v)
    {
        const float Hole = 0.13f, Body = 0.31f, Tip = 0.45f, Teeth = 8, ToothHalf = 0.21f;
        float r = MathF.Sqrt(u * u + v * v);
        if (r < Hole || r > Tip) return false;
        if (r <= Body) return true;
        float turn = MathF.Atan2(v, u) / (2 * MathF.PI) * Teeth;
        float offset = turn - MathF.Round(turn);
        // The tooth narrows a little toward its tip.
        return MathF.Abs(offset) <= ToothHalf * (1.0f - 0.35f * (r - Body) / (Tip - Body));
    }
}
