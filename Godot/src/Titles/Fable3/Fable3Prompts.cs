namespace Refii.Titles.Fable3;

using Godot;
using Refii.Ui;

/// <summary>
/// Button prompts on Fable 3's canvas-drawn surfaces (the boxes, the interaction panel): the
/// shared keycap for the pad button (<see cref="Engine_UiKeyGlyphs"/>, which letters it for the
/// pad or relabels it for the keyboard exactly as Fable 2's prompts are) followed by the label.
/// </summary>
public static class Fable3Prompts
{
	/// <summary>The keycap's side as a multiple of the font's line height.</summary>
	private const float CapScale = 1.15f;
	private const float Gap = 8f;

	/// <summary>Points the shared keycaps at the title's fonts once they are loaded.</summary>
	public static void UseFonts(Engine_UiFontLibrary fonts)
	{
		if (Engine_UiKeyGlyphs.Fonts is null)
		{
			Engine_UiKeyGlyphs.Fonts = fonts;
		}
	}

	/// <summary>The width a prompt takes at <paramref name="scale"/>: keycap, gap and label.</summary>
	public static float Measure(UiFont font, string label, float scale) =>
		(font.Font.LineHeight * CapScale * scale) + (Gap * scale) + (Engine_UiCanvasText.Measure(font, label) * scale);

	/// <summary>Draws the prompt with its top-left at <paramref name="origin"/>; returns its width.</summary>
	public static float Draw(CanvasItem into, UiFont font, string button, string label, Vector2 origin, float scale, Color colour)
	{
		float line = font.Font.LineHeight * scale;
		float cap = line * CapScale;
		ImageTexture keycap = Engine_UiKeyGlyphs.KeycapFor(button);
		into.DrawTextureRect(keycap, new Rect2(origin + new Vector2(0f, (line - cap) * 0.5f), new Vector2(cap, cap)), false);
		float x = origin.X + cap + (Gap * scale);
		Engine_UiCanvasText.DrawLine(into, font, label, new Vector2(x, origin.Y), scale, colour);
		return Measure(font, label, scale);
	}
}
