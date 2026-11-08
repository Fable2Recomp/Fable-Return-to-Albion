using System;
using System.Collections.Generic;
using System.IO;
using GdUnit4;
using Refii.Bnk;
using Refii.Titles.Fable3;
using Refii.Ui;
using static GdUnit4.Assertions;

namespace Refii.Tests.Ui;

/// <summary>
/// A laid-out text box's lines (<see cref="Engine_UiTextBoxStack"/>) as Fable 3's fit stacks them
/// (`sub_82920278` → `sub_82920610`): the fitted box clamped to its ranges, line 0 at the chain's
/// first line for that height, each next line one measured block lower, an empty line dropping
/// nothing — and the shipped hud.bgf's Corner and Subtitles boxes with Fable3TextBoxes' ranges.
/// No Godot type is built.
/// </summary>
[TestSuite]
public sealed class TextBoxStackTests
{
	private static string? Bank(string title)
	{
		string data = Path.Combine("assets", title, "game", "data", "guiscripts.bnk");
		for (string? at = AppContext.BaseDirectory; at is not null; at = Path.GetDirectoryName(at))
		{
			if (File.Exists(Path.Combine(at, data)))
			{
				return Path.Combine(at, data);
			}

			string packages = Path.Combine(at, "out", "package");
			if (Directory.Exists(packages))
			{
				foreach (string package in Directory.EnumerateDirectories(packages))
				{
					if (File.Exists(Path.Combine(package, data)))
					{
						return Path.Combine(package, data);
					}
				}
			}
		}

		return null;
	}

	private static Engine_UiScreen? Hud(string title)
	{
		string? bank = Bank(title);
		if (bank is null || !BnkArchive.TryOpen(bank, out BnkArchive? archive, out _))
		{
			return null;
		}

		using (archive)
		{
			foreach (BnkEntry entry in archive!.Entries)
			{
				if (string.Equals(Path.GetFileName(entry.RelativePath.Replace('\\', '/')), "hud.bgf", StringComparison.OrdinalIgnoreCase))
				{
					return Engine_UiScreen.Parse("hud", archive.ReadEntryBytes(entry), null);
				}
			}
		}

		return null;
	}

	/// <summary>Scene(0) → Layer(1) → Box(2: x 20, y −100, pivot.y −50, BoxName 'Box').</summary>
	private static Engine_BgfDocument Synthetic()
	{
		Engine_BgfDocument doc = Engine_BgfEdit.NewDocument("Scene", 1280, 720, true);
		int layer = Engine_BgfEdit.AddChild(doc, 0, "Layer");
		int box = Engine_BgfEdit.AddChild(doc, layer, "Box", 20f, -100f);
		Engine_BgfEdit.SetFloat(doc, box, "pivot.x", 0f);
		Engine_BgfEdit.SetFloat(doc, box, "pivot.y", -50f);
		BgfElement element = doc.Elements[box];
		var attributes = new List<BgfAttribute>(element.Attributes)
		{
			new(Refii.Core.Engine_GameHash.AttributeId(Engine_UiTextBoxFit.BoxNameAttribute), BgfValueType.String,
				unchecked((uint)Engine_BgfEdit.StringIndex(doc, "Box"))),
		};
		element.Attributes = attributes.ToArray();
		return doc;
	}

	private static Engine_UiTextBoxStack.Measured Line(float width, float height) => new(width, height);

	/// <summary>
	/// The fit's clamps (`fsel` 0x82920540..0x829205A0): the widest line and the summed heights,
	/// each held to its range, the pad added to the height alone (0x829205B8).
	/// </summary>
	[TestCase]
	public void TheBoxIsTheLinesClampedToItsRanges()
	{
		var box = new Engine_UiTextBoxStack.Box("Box", false, new(100f, 300f), new(30f, 120f), 4f);
		AssertThat(Engine_UiTextBoxStack.Fitted(box, new[] { Line(150f, 18f), Line(90f, 18f) }))
			.IsEqual((150f, 40f));
		AssertThat(Engine_UiTextBoxStack.Fitted(box, new[] { Line(40f, 18f) })).IsEqual((100f, 34f));
		AssertThat(Engine_UiTextBoxStack.Fitted(box, new[] { Line(900f, 200f) })).IsEqual((300f, 124f));

		// The measure's extents are words: a fraction does not reach the box.
		AssertThat(Engine_UiTextBoxStack.Fitted(box, new[] { Line(150.9f, 18.9f), Line(0f, 18.9f) }))
			.IsEqual((150f, 40f));
	}

	/// <summary>
	/// Line 0 at `h · (0.5 − pivot.y) + pos.y` for the fitted h, x at pos.x + 37; every next line
	/// one measured block lower; an empty line (measure 0) drops nothing.
	/// </summary>
	[TestCase]
	public void TheLinesStackDownFromTheFirst()
	{
		Engine_BgfDocument doc = Synthetic();
		var box = new Engine_UiTextBoxStack.Box("Box", false, new(0f, 1000f), new(30f, 500f), 0f);

		// h = 19 + 0 + 38 = 57; pivot.y −0.5, so line 0 at 57 − 100.
		(float X, float Y)[] placed = Engine_UiTextBoxStack.Place(
			doc, box, new[] { Line(80f, 19f), Line(0f, 0f), Line(120f, 38f) })!;
		AssertThat(placed.Length).IsEqual(3);
		AssertThat(placed[0]).IsEqual((57f, -43f));
		AssertThat(placed[1]).IsEqual((57f, -62f));
		AssertThat(placed[2]).IsEqual((57f, -62f));

		// One short line: the box holds its minimum, the line its top at 30 − 100.
		AssertThat(Engine_UiTextBoxStack.Place(doc, box, new[] { Line(80f, 19f) })![0]).IsEqual((57f, -70f));

		// A centred box stands its lines at pos.x − w / 2.
		var centred = box with { Centred = true, Width = new(100f, 1000f) };
		AssertThat(Engine_UiTextBoxStack.Place(doc, centred, new[] { Line(300f, 19f) })![0].X).IsEqual(20f - 150f);
		AssertThat(Engine_UiTextBoxStack.Place(doc, centred, new[] { Line(10f, 19f) })![0].X).IsEqual(20f - 50f);

		AssertThat(Engine_UiTextBoxStack.Place(doc, box with { Name = "Nobody" }, new[] { Line(1f, 1f) })).IsNull();
	}

	/// <summary>
	/// FABLE 3'S CORNER COUNTERS. `Bar` [426] (BoxName 'Corner'): pos (261.5, −251) up the chain,
	/// pivot.y −0.5, so line 0 is at h − 251; the 360 runs the `Group` [422] widescreen adapter
	/// (x −135, no y), so the lines stand at 261.5 − 135 + 37 = 163.5. Two 18-tall counters fit
	/// h 36: tops at −215 and −233. One counter holds the box at its 30 minimum: top at −221.
	/// No slide moves line 0's y.
	/// </summary>
	[TestCase]
	public void FableThreesCountersStackFromTheirBox()
	{
		if (Hud("Fable3") is not { } screen)
		{
			Console.WriteLine("[text-box-stack] no Fable 3 bank beside the tree; the shipped Corner box is not checked here");
			return;
		}

		Engine_BgfDocument doc = screen.Document;
		AssertThat(Engine_UiTextBoxFit.BoxElement(doc, "Corner")).IsEqual(426);
		AssertThat(Engine_UiTextBoxFit.Moved(doc, "Corner").Count).IsEqual(0);
		System.Func<int, (float? X, float? Y)> wide = Adapters(doc);
		AssertThat(wide(422)).IsEqual(((float?)-135f, (float?)null));

		(float X, float Y)[] two = Engine_UiTextBoxStack.Place(
			doc, Fable3TextBoxes.Corner, new[] { Line(120f, 18f), Line(140f, 18f) }, wide)!;
		AssertThat(two[0]).IsEqual((163.5f, -215f));
		AssertThat(two[1]).IsEqual((163.5f, -233f));

		(float X, float Y)[] one = Engine_UiTextBoxStack.Place(doc, Fable3TextBoxes.Corner, new[] { Line(120f, 18f) }, wide)!;
		AssertThat(one[0]).IsEqual((163.5f, -221f));

		// Without the adapter the lines stand at the file's 261.5 + 37.
		AssertThat(Engine_UiTextBoxStack.Place(doc, Fable3TextBoxes.Corner, new[] { Line(120f, 18f) })![0].X)
			.IsEqual(298.5f);
	}

	/// <summary>
	/// FABLE 3'S SUBTITLES. `Bar` [505] (BoxName 'Subtitles'): pos (0, −166), pivot.y −0.5, the one
	/// box the fit centres. A two-row page 600 wide and 50 tall stands its top-left at (−300, −116)
	/// — its foot on −166; the cleared page (an empty line) holds the box at its minimum.
	/// </summary>
	[TestCase]
	public void FableThreesSubtitleStandsOnItsBox()
	{
		if (Hud("Fable3") is not { } screen)
		{
			Console.WriteLine("[text-box-stack] no Fable 3 bank beside the tree; the shipped Subtitles box is not checked here");
			return;
		}

		Engine_BgfDocument doc = screen.Document;
		AssertThat(Engine_UiTextBoxFit.BoxElement(doc, "Subtitles")).IsEqual(505);
		AssertThat(Engine_UiTextBoxFit.Moved(doc, "Subtitles").Count).IsEqual(0);
		System.Func<int, (float? X, float? Y)> wide = Adapters(doc);

		(float X, float Y) page = Engine_UiTextBoxStack.Place(doc, Fable3TextBoxes.Subtitles, new[] { Line(600f, 50f) }, wide)![0];
		AssertThat(page).IsEqual((-300f, -116f));

		(float X, float Y) cleared = Engine_UiTextBoxStack.Place(doc, Fable3TextBoxes.Subtitles, new[] { Line(0f, 0f) }, wide)![0];
		AssertThat(cleared.Y).IsEqual(30f - 166f);
		AssertThat(cleared.X).IsEqualApprox(-Fable3TextBoxes.Subtitles.Width.Min * 0.5f, 1e-3f);
	}

	/// <summary>
	/// The ranges are the box element's size with its meters at 0 and 100: 100 × the scale
	/// product up the chain, the meter's `Slide1` track at its end keys — CornerY's `Bar` [426]
	/// scale.y 0.3 → 1.98, CornerX's `BoxMiddle` [425] scale.x 1.49 → 4.65 under the Bar's 0.79;
	/// SubtitlesY's `Bar` [505] scale.y 0.1 → 0.5 under `BoxMiddle` [503] 3, SubtitlesX's
	/// `Component` [504] scale.x 0.2 → 1.2 under [503] 8.3 and `Group` [501] at its widescreen
	/// 1.12 (adapter [502]).
	/// </summary>
	[TestCase]
	public void FableThreesBoxRangesAreTheirMetersEnds()
	{
		if (Hud("Fable3") is not { } screen)
		{
			Console.WriteLine("[text-box-stack] no Fable 3 bank beside the tree; the box ranges are not checked here");
			return;
		}

		Engine_BgfDocument doc = screen.Document;
		AssertRange(doc, Fable3TextBoxes.Corner.Height, 426, "scale.y", 426, "scale.y");
		AssertRange(doc, Fable3TextBoxes.Corner.Width, 426, "scale.x", 425, "scale.x");
		AssertRange(doc, Fable3TextBoxes.Subtitles.Height, 505, "scale.y", 505, "scale.y");
		AssertRange(doc, Fable3TextBoxes.Subtitles.Width, 505, "scale.x", 504, "scale.x");
	}

	/// <summary>
	/// 100 × the product of <paramref name="axis"/> from <paramref name="box"/> to the root, the
	/// tracked element at its track's first and last key, the adapters' widths in place.
	/// </summary>
	private static void AssertRange(Engine_BgfDocument doc, Engine_UiTextBoxStack.Range range, int box, string axis,
		int tracked, string trackAxis)
	{
		uint id = Refii.Core.Engine_GameHash.AttributeId(axis);
		uint trackId = Refii.Core.Engine_GameHash.AttributeId(trackAxis);
		BgfAnimationKey[]? keys = null;
		foreach (BgfAnimation animation in doc.Animations)
		{
			if (animation.ElementOrdinal == tracked && animation.AttributeId == trackId && animation.Keys.Length >= 2)
			{
				keys = animation.Keys;
			}
		}

		AssertThat(keys).IsNotNull();
		var widths = new Dictionary<int, float>();
		foreach (Engine_UiWidescreen.Override over in Engine_UiWidescreen.Read(doc).Overrides)
		{
			if (over.ScaleX is { } width)
			{
				widths[over.ParentOrdinal] = width;
			}
		}

		float low = 100f, high = 100f;
		for (int at = box; at >= 0; at = doc.Elements[at].ParentOrdinal)
		{
			if (at == tracked)
			{
				low *= keys![0].Value;
				high *= keys[^1].Value;
				continue;
			}

			float own = axis == "scale.x" && widths.TryGetValue(at, out float wide) ? wide
				: doc.Elements[at].TryGetAttribute(id, out BgfAttribute attribute) && attribute.Type == BgfValueType.Float
					? attribute.AsFloat() : 1f;
			low *= own;
			high *= own;
		}

		AssertThat(range.Min).IsEqualApprox(low, 1e-2f);
		AssertThat(range.Max).IsEqualApprox(high, 1e-2f);
	}

	/// <summary>The adapters' position writes per moved ordinal, as Fable3HudDocument states them.</summary>
	private static System.Func<int, (float? X, float? Y)> Adapters(Engine_BgfDocument doc)
	{
		var moved = new Dictionary<int, (float? X, float? Y)>();
		foreach (Engine_UiWidescreen.Override over in Engine_UiWidescreen.Read(doc).Overrides)
		{
			moved[over.ParentOrdinal] = (over.X, over.Y);
		}

		return ordinal => moved.TryGetValue(ordinal, out (float? X, float? Y) at) ? at : (null, null);
	}
}
