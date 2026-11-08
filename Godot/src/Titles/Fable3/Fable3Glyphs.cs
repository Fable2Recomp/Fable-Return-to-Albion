namespace Refii.Titles.Fable3;

using Refii.Ui;

/// <summary>
/// Fable 3's button art for the shared glyph swap (<see cref="Engine_UiKeyGlyphs"/>): which of its
/// GUI textures draw a pad button, so the keycaps (keyboard and mouse) and the PlayStation /
/// Switch glyphs replace them exactly as Fable 2's do.
/// </summary>
/// <remarks>
/// TWO FAMILIES. `art/gui/controller/…` is Fable 2's own set, which Fable 3 still ships and the
/// shared table already knows; `art/gui_memmount/controller/…` is the same stick art in the memory
/// mount. `…/hudassests/version4/buttons/&lt;button&gt;/&lt;size&gt;/…` is Fable 3's: each button as
/// a `complete` texture (orb and letter in one) and as layers — the `orb`, the `text` (the letter)
/// over it, a `rim`, a `highlight` glow and a `shadow`. The HUD's prompts stack the layers
/// (`Icon.Container.A_Orb` under `A_Text`, set by sub_82961340 / sub_829614A0 through
/// sub_82818EF0), so the glyph goes on the `complete` art and on the orb, and the letter and rim
/// over the orb draw nothing while the glyph shows; the glow and shadow stay. The `complete`
/// art is that stack flattened — its alpha covers the shadow's disc — so a glyph in its place is
/// backed (<see cref="Engine_UiKeyGlyphs.GlyphArt.Backed"/>): it sits on the art's silhouette in
/// black, the shadow the layered prompt draws as its own element.
/// </remarks>
public static class Fable3Glyphs
{
	private const string Buttons = "/hudassests/version4/buttons/";
	private const string MountedController = "art/gui_memmount/controller/";

	/// <summary>The button art at a lower-cased, forward-slashed texture path, or null for any other art.</summary>
	public static Engine_UiKeyGlyphs.GlyphArt? ArtFor(string path)
	{
		int at = path.IndexOf(Buttons, System.StringComparison.Ordinal);
		if (at >= 0)
		{
			string rest = path[(at + Buttons.Length)..];
			int slash = rest.IndexOf('/');
			if (slash <= 0 || ButtonOf(rest[..slash]) is not { } button)
			{
				return null;
			}

			string name = System.IO.Path.GetFileNameWithoutExtension(rest);
			if (name.EndsWith("_24x24", System.StringComparison.Ordinal))
			{
				name = name[..^"_24x24".Length];
			}

			string part = name.Contains('_') ? name[(name.IndexOf('_') + 1)..] : name;
			return part switch
			{
				"complete" or "complete_pressed" =>
					new Engine_UiKeyGlyphs.GlyphArt(button, Engine_UiKeyGlyphs.GlyphLayer.Whole, Backed: true),
				"orb" => new Engine_UiKeyGlyphs.GlyphArt(button, Engine_UiKeyGlyphs.GlyphLayer.Whole),
				"text" or "textb" or "orbt" or "rim" or "stick" or "trigger" or "start" or "back" =>
					new Engine_UiKeyGlyphs.GlyphArt(button, Engine_UiKeyGlyphs.GlyphLayer.Overlay),
				_ => null,
			};
		}

		if (path.StartsWith(MountedController, System.StringComparison.Ordinal))
		{
			string name = System.IO.Path.GetFileNameWithoutExtension(path);
			string? stick = name.StartsWith("left", System.StringComparison.Ordinal) ? "LS" :
				name.StartsWith("right", System.StringComparison.Ordinal) ? "RS" : null;
			return stick is null ? null : new Engine_UiKeyGlyphs.GlyphArt(stick, Engine_UiKeyGlyphs.GlyphLayer.Whole);
		}

		return null;
	}

	private static string? ButtonOf(string folder) => folder switch
	{
		"a" => "A",
		"b" => "B",
		"x" => "X",
		"y" => "Y",
		"back" => "Back",
		"start" => "Start",
		"dpad" => "DPad",
		"leftbutton" => "LB",
		"rightbutton" => "RB",
		"lefttrigger" => "LT",
		"righttrigger" => "RT",
		"leftstick" => "LS",
		"rightstick" => "RS",
		_ => null,
	};
}
