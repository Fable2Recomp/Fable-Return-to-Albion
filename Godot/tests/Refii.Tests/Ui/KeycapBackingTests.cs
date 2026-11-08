namespace Refii.Tests.Ui;

using GdUnit4;
using Godot;
using static GdUnit4.Assertions;
using Refii.Titles.Fable3;
using Refii.Ui;

/// <summary>
/// A glyph drawn in place of Fable 3's flattened <c>complete</c> prompt sits on the art's
/// silhouette in black (the shadow layer the layered prompt draws as <c>A_shadow</c>); the orb of
/// a layered prompt and every Fable 2 button stay unbacked. Only the pure canvas is exercised.
/// </summary>
[TestSuite]
public class KeycapBackingTests
{
	private const string Buttons = "art/gui_memmount/hudassests/version4/buttons/";

	[TestCase]
	public void Fable3CompleteArtIsBacked()
	{
		var complete = Fable3Glyphs.ArtFor(Buttons + "a/24x24_norim/a_complete.tex");
		AssertThat(complete.HasValue).IsTrue();
		AssertThat(complete!.Value.Button).IsEqual("A");
		AssertThat(complete.Value.Layer).IsEqual(Engine_UiKeyGlyphs.GlyphLayer.Whole);
		AssertThat(complete.Value.Backed).IsTrue();
		AssertThat(Fable3Glyphs.ArtFor(Buttons + "a/32x32_norim/a_complete_pressed.tex")!.Value.Backed).IsTrue();

		// The layered prompt's orb has its own shadow element under it.
		var orb = Fable3Glyphs.ArtFor(Buttons + "a/24x24_norim/a_orb.tex");
		AssertThat(orb!.Value.Layer).IsEqual(Engine_UiKeyGlyphs.GlyphLayer.Whole);
		AssertThat(orb.Value.Backed).IsFalse();

		// The shadow itself is not a glyph: it stays the shipped art.
		AssertThat(Fable3Glyphs.ArtFor(Buttons + "a/24x24_norim/a_shadow.tex").HasValue).IsFalse();
	}

	[TestCase]
	public void TheBackingGoesUnderneath()
	{
		var canvas = new Engine_UiKeyGlyphs.Canvas(2, 1);
		canvas.Set(0, 0, new Color(1f, 1f, 1f, 1f));
		canvas.Set(1, 0, new Color(0f, 0f, 0f, 0f));

		canvas.Under(0, 0, Colors.Black, 1f);
		canvas.Under(1, 0, Colors.Black, 0.5f);

		// An opaque keycap pixel is unchanged; a transparent one takes the silhouette's alpha.
		AssertThat(canvas.Get(0, 0)).IsEqual(new Color(1f, 1f, 1f, 1f));
		Color under = canvas.Get(1, 0);
		AssertThat(under.R).IsEqual(0f);
		AssertThat(under.A).IsBetween(0.49f, 0.51f);
	}
}
