using System;
using Godot;

namespace Refii.Ui;

/// <summary>
/// The glyph art the touch controls draw with: the keycaps' colours, the pad's own button atlas
/// (captured by <see cref="Wrap"/>) and text in the game's font.
/// </summary>
public static partial class Engine_UiKeyGlyphs
{
	/// <summary>The keycaps' dark plate — the colour the touch controls' plates share, so both read as the game's.</summary>
	public static Color PlateColour => CapFill;

	/// <summary>The keycaps' gold rim and lettering.</summary>
	public static Color TrimColour => CapRim;

	/// <summary>
	/// The controller's own button art, `art/gui/persistant/abyx.tex`, as the UI loaded it — null
	/// until it has. Its upper half is the four buttons, columns A, B, Y, X, idle above pressed.
	/// </summary>
	public static Texture2D? PadAtlas { get; private set; }

	/// <summary>The atlas cell drawing <paramref name="button"/> (A, B, X or Y), in texels, or null for any other.</summary>
	public static Rect2? PadAtlasCell(string button, bool pressed)
	{
		int column = Array.IndexOf(AtlasColumns, button);
		if (PadAtlas is not { } atlas || column < 0)
		{
			return null;
		}

		float w = atlas.GetWidth() / 4f, h = atlas.GetHeight() / 4f;
		return new Rect2(column * w, pressed ? h : 0f, w, h);
	}

	/// <summary>
	/// <paramref name="text"/> in the game's own font, <paramref name="pixelHeight"/> tall, in
	/// <paramref name="colour"/> over a one-pixel <paramref name="shadow"/> — for host UI drawn
	/// outside the guest's screens. Null until the UI has loaded the font.
	/// </summary>
	public static Image? TextImage(string text, int pixelHeight, Color colour, Color shadow)
	{
		if (Fonts?.For(18) is not { } selection || text.Length == 0 || pixelHeight <= 0)
		{
			return null;
		}

		Engine_BitmapFont font = selection.Font.Font;
		EnsureFontAtlas(font);
		if (_atlas is null)
		{
			return null;
		}

		float height = font.LineHeight + 1;
		float scale = pixelHeight / height;
		int pad = Math.Max(2, pixelHeight / 12);
		var canvas = new Canvas((int)Math.Ceiling(font.MeasureWidth(text) * scale) + (pad * 2), pixelHeight + (pad * 2));
		foreach ((float dx, float dy, Color ink) in new[] { (1f, 1f, shadow), (0f, 0f, colour) })
		{
			float penX = pad + dx;
			foreach (char c in text)
			{
				BitmapGlyph g = font.GetGlyph(c);
				float left = penX + (g.LeftBearing * scale);
				penX = left + (g.PostAdvance * scale);
				if (g.DrawWidth > 0)
				{
					BlitGlyph(canvas, g, left, pad + dy, g.DrawWidth * scale, height * scale, ink);
				}
			}
		}

		return canvas.ToImage(mipmaps: true);
	}
}
