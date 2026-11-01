namespace Refii.Tests.Titles;

using System.Collections.Generic;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Titles.Fable3;
using Statement = Refii.Titles.Fable3.Fable3HudStatements.Statement;

/// <summary>
/// Fable 3's element statements joined to a document by ordinal (Fable3HudStatements): the three
/// spellings a statement arrives under, the first-slide rest state, and the later statement winning.
/// </summary>
[TestSuite]
public class Fable3HudStatementsTests
{
	// 0 root, 1 Layer, 2 Layer.dpad, 3 Layer.dpad.dpad, 4 Layer.dpad.dpad.dpadmenu,
	// 5 Layer.ReceivedItems, 6 Layer.ReceivedItems.ReceivedItemAlert, 7 Layer.TopBoxRefined
	private static readonly string[] Paths =
	{
		string.Empty, "Layer", "Layer.dpad", "Layer.dpad.dpad", "Layer.dpad.dpad.dpadmenu",
		"Layer.ReceivedItems", "Layer.ReceivedItems.ReceivedItemAlert", "Layer.TopBoxRefined",
	};

	private static IReadOnlyList<int> Bound(string attribute, string value) =>
		(attribute, value) switch
		{
			("SlideToggleName", "ItemReceived") => new[] { 6 },
			("SlideToggleName", "DPadUp") => new[] { 3, 4 },
			("SlideSetName", "TopBox") => new[] { 7 },
			_ => System.Array.Empty<int>(),
		};

	private static Fable3HudStatements Make() => new(Paths, Bound, new[] { 0, 2, 4, 5, 6, 7 });

	[TestCase]
	public void ResolvesRootLayerPathsBeneathLayerAndRegistryKeys()
	{
		Fable3HudStatements join = Make();
		AssertThat(join.Resolve("Scene")).ContainsExactly(0);
		AssertThat(join.Resolve("Layer.TopBoxRefined")).ContainsExactly(7);
		AssertThat(join.Resolve("dpad.dpad.dpadmenu")).ContainsExactly(4);
		AssertThat(join.Resolve("SlideToggleName:ItemReceived")).ContainsExactly(6);
		AssertThat(join.Resolve("SlideToggleName:DPadUp")).ContainsExactly(3, 4);
		AssertThat(join.Resolve("Options.Sections.Options").Count).IsEqual(0);
		AssertThat(join.Resolve("TextName:Nothing").Count).IsEqual(0);
	}

	[TestCase]
	public void UnstatedOwnersRestOnTheirFirstSlide()
	{
		Fable3HudStatements join = Make();
		int[] slides = new int[Paths.Length];
		float?[] opacity = new float?[Paths.Length];
		string?[] texts = new string?[Paths.Length];
		float?[] times = new float?[Paths.Length];
		join.Apply(System.Array.Empty<Statement>(), slides, opacity, texts, times);

		AssertThat(slides[2]).IsEqual(1);
		AssertThat(slides[6]).IsEqual(1);
		AssertThat(slides[1]).IsEqual(0);
		AssertThat(slides[3]).IsEqual(0);
		AssertThat(opacity[4]).IsNull();
	}

	[TestCase]
	public void StatementsFoldPerChannelLaterWins()
	{
		Fable3HudStatements join = Make();
		int[] slides = new int[Paths.Length];
		float?[] opacity = new float?[Paths.Length];
		string?[] texts = new string?[Paths.Length];
		float?[] times = new float?[Paths.Length];
		var statements = new List<Statement>
		{
			// Out of order on purpose: the stamps decide.
			new("Layer.TopBoxRefined", 3, null, null, null, 9),
			new("SlideSetName:TopBox", 2, 50f, null, null, 4),
			new("SlideToggleName:ItemReceived", 2, null, null, null, 1),
			new("Scene", 2, null, null, null, 2),
			new("dpad.dpad.dpadmenu", null, 0.09765625f, "x", 1000f, 3),
			new("Options.Sections.Options", 1, null, null, null, 5),
		};

		join.Apply(statements, slides, opacity, texts, times);

		AssertThat(slides[7]).IsEqual(3);
		AssertThat(opacity[7]).IsEqual(50f);
		AssertThat(slides[6]).IsEqual(2);
		AssertThat(slides[0]).IsEqual(2);
		AssertThat(slides[4]).IsEqual(1);
		AssertThat(opacity[4]).IsEqual(0.09765625f);
		AssertThat(texts[4]).IsEqual("x");
		AssertThat(times[4]).IsEqual(1000f);
		AssertThat(join.Mapped).IsEqual(5);
		AssertThat(join.Unmapped).IsEqual(1);
		AssertThat(join.UnmappedPaths).ContainsExactly("Options.Sections.Options");
	}

	[TestCase]
	public void PositionsFillTransformRowsAndTexturesHashTheirPath()
	{
		Fable3HudStatements join = Make();
		int[] slides = new int[Paths.Length];
		float?[] opacity = new float?[Paths.Length];
		string?[] texts = new string?[Paths.Length];
		float?[] times = new float?[Paths.Length];
		float?[]?[] transforms = new float?[]?[Paths.Length];
		uint[] textures = new uint[Paths.Length];
		const string path = @"art\gui_memmount\hudassests\version4\buttons\b\24x24_norim\b_text.tex";
		var statements = new List<Statement>
		{
			new("Layer.TopBoxRefined", null, null, null, null, 1, (12f, 300f, -1f)),
			new("dpad.dpad.dpadmenu", null, null, null, null, 2, Texture: path),
		};

		join.Apply(statements, slides, opacity, texts, times, transforms, textures);

		AssertThat(transforms[7]![0]).IsEqual(12f);
		AssertThat(transforms[7]![1]).IsEqual(300f);
		AssertThat(transforms[7]![2]).IsEqual(-1f);
		AssertThat(transforms[7]![3]).IsNull();
		AssertThat(transforms[4]).IsNull();
		AssertThat(textures[4]).IsEqual(Refii.Core.Engine_GameHash.ResourcePath(path));
		AssertThat(textures[7]).IsEqual(0u);
	}
}
