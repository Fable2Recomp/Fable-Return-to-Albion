namespace Refii.Tests.Titles;

using System.Collections.Generic;
using System.Linq;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Core;
using Refii.Titles.Fable3;
using Refii.Ui;
using Statement = Refii.Titles.Fable3.Fable3HudStatements.Statement;

/// <summary>
/// The HUD writers' statements (Fable3HudRegistry, hud_hooks.cpp / interaction_hooks.cpp) folded
/// over a document as Fable3HudDocument folds them: a registry name lands on every element bound
/// under it, a toggle is slide 2 on / 1 off, the reset rests every fader of its kind, an unfiled
/// name rests on slide 1, the d-pad and the info panel land on their paths, and the element
/// write a registry writer makes is not folded twice. No Godot type is built.
/// </summary>
[TestSuite]
public class Fable3HudRegistryTests
{
	// 0 root, 1 Layer, 2 Layer.dpad, 3 Layer.dpad.dpad, 4 Layer.dpad.dpad.dpadmenu,
	// 5 …group.up.up, 6 …group.up.Counter.Counter, 7 …group.up.fx, 8 Layer.TopBoxRefined,
	// 9 Layer.Corner, 10 Layer.LowerElements.InfoBox
	private static readonly string[] Paths =
	{
		string.Empty, "Layer", "Layer.dpad", "Layer.dpad.dpad", "Layer.dpad.dpad.dpadmenu",
		"Layer.dpad.dpad.dpadmenu.group.up.up", "Layer.dpad.dpad.dpadmenu.group.up.Counter.Counter",
		"Layer.dpad.dpad.dpadmenu.group.up.fx", "Layer.TopBoxRefined", "Layer.Corner",
		"Layer.LowerElements.InfoBox",
	};

	private static readonly uint TopTitle = Engine_GameHash.Fnv1("TopTitle");
	private static readonly uint TopBox = Engine_GameHash.Fnv1("TopBox");
	private static readonly uint Corner = Engine_GameHash.Fnv1("Corner");

	private static IReadOnlyList<int> Named(Engine_UiBindings.Kind kind, uint? hash)
	{
		var bound = new List<(Engine_UiBindings.Kind Kind, uint Hash, int Ordinal)>
		{
			(Engine_UiBindings.Kind.SlideToggle, TopTitle, 8),
			(Engine_UiBindings.Kind.SlideSet, TopBox, 8),
			(Engine_UiBindings.Kind.SlideFader2, Corner, 9),
			(Engine_UiBindings.Kind.SlideFader, Corner, 9),
		};
		return bound.Where(b => b.Kind == kind && (hash is null || b.Hash == hash)).Select(b => b.Ordinal).ToList();
	}

	private static Fable3HudStatements Make() =>
		new(Paths, (_, _) => System.Array.Empty<int>(), new[] { 4, 8, 9, 10 }, Named);

	private sealed class Channels
	{
		public int[] Slides { get; } = new int[Paths.Length];
		public float?[] Opacity { get; } = new float?[Paths.Length];
		public string?[] Texts { get; } = new string?[Paths.Length];
		public float?[] Times { get; } = new float?[Paths.Length];
		public float?[]?[] Transforms { get; } = new float?[]?[Paths.Length];
		public uint[] Textures { get; } = new uint[Paths.Length];
	}

	private static Channels Fold(Fable3HudStatements join, string document, IEnumerable<Statement>? elements = null)
	{
		var all = new List<Statement>(elements ?? System.Array.Empty<Statement>());
		all.AddRange(Fable3HudRegistry.StatementsFor(document));
		var channels = new Channels();
		join.Apply(all, channels.Slides, channels.Opacity, channels.Texts, channels.Times, channels.Transforms, channels.Textures);
		return channels;
	}

	[BeforeTest]
	public void Reset() => Fable3HudRegistry.Clear();

	[TestCase]
	public void AToggleIsSlideTwoOnAndOneOff()
	{
		Fable3HudStatements join = Make();
		Fable3HudRegistry.Slide("SlideToggleName", TopTitle, 2);
		AssertThat(Fold(join, Fable3HudRegistry.HudDocument).Slides[8]).IsEqual(2);

		Fable3HudRegistry.Slide("SlideToggleName", TopTitle, 1);
		AssertThat(Fold(join, Fable3HudRegistry.HudDocument).Slides[8]).IsEqual(1);
	}

	[TestCase]
	public void TheLaterWriterWinsAcrossRegistriesAndElementWrites()
	{
		Fable3HudStatements join = Make();
		Fable3HudRegistry.Slide("SlideSetName", TopBox, 3);
		ulong after = Fable3Elements.NextStamp();
		// A dotted-path element write made after the registry's: it wins.
		var later = new[] { new Statement("Layer.TopBoxRefined", 4, null, null, null, after) };
		AssertThat(Fold(join, Fable3HudRegistry.HudDocument, later).Slides[8]).IsEqual(4);

		Fable3HudRegistry.Slide("SlideSetName", TopBox, 1);
		AssertThat(Fold(join, Fable3HudRegistry.HudDocument, later).Slides[8]).IsEqual(1);
	}

	[TestCase]
	public void TheResetRestsEveryFaderOfItsKindUntilTheNextWrite()
	{
		Fable3HudStatements join = Make();
		Fable3HudRegistry.Slide("SlideFader2Name", Corner, 4);
		Fable3HudRegistry.SlideReset("SlideFader2Name", 1);
		AssertThat(Fold(join, Fable3HudRegistry.HudDocument).Slides[9]).IsEqual(1);

		Fable3HudRegistry.Slide("SlideFader2Name", Corner, 2);
		AssertThat(Fold(join, Fable3HudRegistry.HudDocument).Slides[9]).IsEqual(2);
	}

	[TestCase]
	public void AnUnfiledNameRestsOnItsFirstSlide()
	{
		Fable3HudStatements join = Make();
		Fable3HudRegistry.Slide("SlideToggleName", TopTitle, 2);
		Fable3HudRegistry.Unfiled("SlideToggleName", TopTitle);
		AssertThat(Fold(join, Fable3HudRegistry.HudDocument).Slides[8]).IsEqual(1);
	}

	[TestCase]
	public void ANameNoElementBindsIsNotAMiss()
	{
		Fable3HudStatements join = Make();
		Fable3HudRegistry.Slide("SlideToggleName", Engine_GameHash.Fnv1("NotInThisDocument"), 2);
		Fold(join, Fable3HudRegistry.HudDocument);
		AssertThat(join.Unmapped).IsEqual(0);
	}

	[TestCase]
	public void TheDpadSlideAndSlotLandOnTheirPaths()
	{
		Fable3HudStatements join = Make();
		Fable3HudRegistry.DpadSlide(0, 2);
		Fable3HudRegistry.DpadSlot(0, 0, @"art\gui\wheelicons\expressions.tex", 12, true);
		Channels on = Fold(join, Fable3HudRegistry.HudDocument);
		AssertThat(on.Slides[4]).IsEqual(2);
		AssertThat(on.Textures[5]).IsEqual(Engine_GameHash.ResourcePath(@"art\gui\wheelicons\expressions.tex"));
		AssertThat(on.Texts[6]).IsEqual("12");
		AssertThat(on.Opacity[7]).IsEqual(100f);
		AssertThat(join.Unmapped).IsEqual(0);

		Fable3HudRegistry.DpadSlot(0, 0, @"art\gui\wheelicons\expressions.tex", 0, false);
		Fable3HudRegistry.DpadSlide(0, 1);
		Channels off = Fold(join, Fable3HudRegistry.HudDocument);
		AssertThat(off.Slides[4]).IsEqual(1);
		AssertThat(off.Texts[6]).IsEqual(string.Empty);
		AssertThat(off.Opacity[7]).IsEqual(0f);
	}

	[TestCase]
	public void TheSecondPlayersDpadIsDpad2AndAnotherDocumentsPathIsNotFolded()
	{
		Fable3HudStatements join = Make();
		Fable3HudRegistry.DpadSlide(1, 2);
		Fable3HudRegistry.InteractionPanel(5);
		Channels hud = Fold(join, Fable3HudRegistry.HudDocument);
		AssertThat(hud.Slides[4]).IsEqual(1);
		AssertThat(hud.Slides[10]).IsEqual(1);
		AssertThat(join.UnmappedPaths).ContainsExactly("dpad2.dpad.dpadmenu");
	}

	[TestCase]
	public void TheInfoPanelIsTheInfoBoxsSlide()
	{
		Fable3HudStatements join = Make();
		Fable3HudRegistry.InteractionPanel(6);
		AssertThat(Fold(join, Fable3HudRegistry.InteractionDocument).Slides[10]).IsEqual(6);
		Fable3HudRegistry.InteractionPanel(1);
		AssertThat(Fold(join, Fable3HudRegistry.InteractionDocument).Slides[10]).IsEqual(1);
	}

	[TestCase]
	public void APlaceIsKeptUntilItsRestore()
	{
		uint name = Engine_GameHash.Fnv1("SpeechBubble");
		Fable3HudRegistry.Place(name, 120f, -40f);
		AssertThat(Fable3HudRegistry.Placed[name]).IsEqual((120f, -40f));
		Fable3HudRegistry.Unplace(name);
		AssertThat(Fable3HudRegistry.Placed.ContainsKey(name)).IsFalse();
	}

	[TestCase]
	public void TheRegistryWritersOwnElementWritesAreNotFoldedTwice()
	{
		AssertThat(Fable3HudDocument.IsRegistryStated("SlideToggleName:TopTitle")).IsTrue();
		AssertThat(Fable3HudDocument.IsRegistryStated("TextName:TopMain")).IsTrue();
		AssertThat(Fable3HudDocument.IsRegistryStated("MeterName:TopBoxX")).IsTrue();
		AssertThat(Fable3HudDocument.IsRegistryStated("ScriptPositioned:SpeechBubble")).IsTrue();
		AssertThat(Fable3HudDocument.IsRegistryStated("ParticleEffect:fxgui_glass_glint_01")).IsFalse();
		AssertThat(Fable3HudDocument.IsRegistryStated("Layer.TopBoxRefined.TopBox")).IsFalse();
		AssertThat(Fable3HudDocument.IsRegistryStated("Options.Sections.A:B")).IsFalse();
	}
}
