namespace Refii.Tests.Ui;

using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Core;
using Refii.Ui;

/// <summary>
/// The menu cursor's select orb (`MenuHighlight` `green_top`/`green_top_translucent`, cut from
/// `art/gui/frames/frames_elements.tex`) is found by its atlas cell, covers the red B baked into
/// `rim_and_red` beneath it, and keeps its shipped art only on an Xbox pad with nothing remapped.
/// Cells are the shipped `.fac` rectangles.
/// </summary>
[TestSuite]
public class CursorOrbGlyphTests
{
	private const string Atlas = @"art\gui\frames\frames_elements.tex";

	[TestCase]
	public void TestTheGreenTopCellIsTheOrb()
	{
		var orb = Engine_UiKeyGlyphs.CursorOrb(Atlas, 0.016f, 0.125f, 0.721f, 0.830f);
		AssertThat(orb.HasValue).IsTrue();
		(float x, float y, float r) = orb!.Value;
		AssertThat(x).IsBetween(0.47f, 0.50f);
		AssertThat(y).IsBetween(0.44f, 0.47f);
		AssertThat(r).IsBetween(0.40f, 0.42f);
		AssertThat(x - r >= 0f && x + r <= 1f && y - r >= 0f && y + r <= 1f).IsTrue();
		AssertThat(Engine_UiKeyGlyphs.CursorOrb("Art/GUI/Frames/Frames_Elements.tex", 0.016f, 0.125f, 0.721f, 0.830f).HasValue).IsTrue();
	}

	[TestCase]
	public void TestNoOtherCellOfTheAtlasIsTheOrb()
	{
		// rim_and_red; the cut screens' stale rim and orb cells; the stars; the scroll arrow; a frame cap.
		AssertThat(Engine_UiKeyGlyphs.CursorOrb(Atlas, 0f, 0.25f, 0.171f, 0.687f).HasValue).IsFalse();
		AssertThat(Engine_UiKeyGlyphs.CursorOrb(Atlas, 0f, 0.25f, 0.190f, 0.440f).HasValue).IsFalse();
		AssertThat(Engine_UiKeyGlyphs.CursorOrb(Atlas, 0.026f, 0.226f, 0.438f, 0.638f).HasValue).IsFalse();
		AssertThat(Engine_UiKeyGlyphs.CursorOrb(Atlas, 0.014f, 0.108f, 0.888f, 0.982f).HasValue).IsFalse();
		AssertThat(Engine_UiKeyGlyphs.CursorOrb(Atlas, 0.25f, 0.375f, 0.160f, 0.285f).HasValue).IsFalse();
		AssertThat(Engine_UiKeyGlyphs.CursorOrb(Atlas, 0f, 0.125f, 0.004f, 0.164f).HasValue).IsFalse();
		AssertThat(Engine_UiKeyGlyphs.CursorOrb(Atlas, 0f, 1f, 0f, 1f).HasValue).IsFalse();
	}

	[TestCase]
	public void TestTheOrbCellOfAnotherTextureIsNotTheOrb()
	{
		AssertThat(Engine_UiKeyGlyphs.CursorOrb(@"art\gui\persistant\abyx.tex", 0.016f, 0.125f, 0.721f, 0.830f).HasValue).IsFalse();
		AssertThat(Engine_UiKeyGlyphs.CursorOrb(@"art\gui\frames\frames_04.tex", 0.016f, 0.125f, 0.721f, 0.830f).HasValue).IsFalse();
	}

	[TestCase]
	public void TestTheOrbCoversTheRedBBeneathIt()
	{
		// The pause book's MenuHighlight as laid out (y up): rim_and_red x[-470.4..-368] y[54.4..265.6]
		// over u[0..0.25] v[0.171..0.687]; green_top x[-437.6..-392.8] y[138.8..183.6]. The red B disc
		// is centred on texel (68, 216) of the 512 atlas, radius 21.5 with its dark edge.
		float redX = -470.4f + ((68f / 512f) / 0.25f * 102.4f);
		float redY = 265.6f - (((216f / 512f) - 0.171f) / (0.687f - 0.171f) * 211.2f);
		float redR = 21.5f * (102.4f / 128f) / 44.8f;
		float qx = (redX + 437.6f) / 44.8f, qy = (183.6f - redY) / 44.8f;

		(float x, float y, float r) = Engine_UiKeyGlyphs.CursorOrb(Atlas, 0.016f, 0.125f, 0.721f, 0.830f)!.Value;
		float offset = System.MathF.Sqrt(((qx - x) * (qx - x)) + ((qy - y) * (qy - y)));
		AssertThat(offset + redR).IsLess(r);
	}

	[TestCase]
	public void TestXboxWithNothingRemappedKeepsTheShippedAtlas()
	{
		WithPad("xbox", ControllerFamily.Xbox, remap =>
		{
			AssertThat(Engine_UiKeyGlyphs.AtlasShowsShipped(keys: false)).IsTrue();
			AssertThat(Engine_UiKeyGlyphs.AtlasShowsShipped(keys: true)).IsFalse();
		});
	}

	[TestCase]
	public void TestAnyOtherStyleOrARemapDrawsTheOrb()
	{
		WithPad("switch", ControllerFamily.Xbox, _ => AssertThat(Engine_UiKeyGlyphs.AtlasShowsShipped(false)).IsFalse());
		WithPad("playstation", ControllerFamily.Xbox, _ => AssertThat(Engine_UiKeyGlyphs.AtlasShowsShipped(false)).IsFalse());
		WithPad("auto", ControllerFamily.Nintendo, _ => AssertThat(Engine_UiKeyGlyphs.AtlasShowsShipped(false)).IsFalse());
		WithPad("auto", ControllerFamily.Xbox, _ => AssertThat(Engine_UiKeyGlyphs.AtlasShowsShipped(false)).IsTrue());
		WithPad("xbox", ControllerFamily.Xbox, remap =>
		{
			remap.Set(PadInput.A, new[] { PadInput.B });
			remap.Set(PadInput.B, new[] { PadInput.A });
			AssertThat(Engine_UiKeyGlyphs.AtlasShowsShipped(false)).IsFalse();
		});
	}

	private static void WithPad(string style, ControllerFamily family, System.Action<Engine_PadRemap> body)
	{
		string oldStyle = Engine_UiKeyGlyphs.StyleSetting, oldPack = Engine_UiKeyGlyphs.PackName;
		ControllerFamily oldFamily = Engine_UiKeyGlyphs.PadFamily;
		int oldPlayer = Engine_UiKeyGlyphs.GlyphPlayer;
		Engine_PadRemap remap = Engine_PadRemap.For(1);
		try
		{
			Engine_UiKeyGlyphs.StyleSetting = style;
			Engine_UiKeyGlyphs.PadFamily = family;
			Engine_UiKeyGlyphs.PackName = string.Empty;
			Engine_UiKeyGlyphs.GlyphPlayer = 1;
			remap.Reset();
			body(remap);
		}
		finally
		{
			remap.Reset();
			Engine_UiKeyGlyphs.StyleSetting = oldStyle;
			Engine_UiKeyGlyphs.PackName = oldPack;
			Engine_UiKeyGlyphs.PadFamily = oldFamily;
			Engine_UiKeyGlyphs.GlyphPlayer = oldPlayer;
		}
	}
}
