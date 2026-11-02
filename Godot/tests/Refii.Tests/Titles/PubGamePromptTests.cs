namespace Refii.Tests.Titles;

using System.Collections.Generic;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Core;
using Refii.Game;
using Refii.Titles.Fable2.PubGames.Framework;
using Refii.Ui;

/// <summary>
/// The pub games' prompts: the shared `button` symbol's labels name the pad buttons, its shapes are
/// found from its own frames, and a prompt keeps its shipped art only where the core's glyphs would
/// (an Xbox pad, nothing remapped, no pack file); the board reads the core's pad 0, whose bits
/// are its own, with its left click as confirm. The symbol is character 117 of all three SWFs, as built.
/// </summary>
[TestSuite]
public class PubGamePromptTests
{
	/// <summary>Character 117's labels: four frames per button.</summary>
	private static readonly Dictionary<string, int> SymbolLabels = new()
	{
		["a"] = 1, ["b"] = 5, ["x"] = 9, ["y"] = 13, ["ls"] = 17, ["rs"] = 21,
		["lt"] = 25, ["rt"] = 29, ["lb"] = 33, ["rb"] = 37,
	};

	/// <summary>What each labelled frame places: the bezel (81) under the face buttons and sticks, a shape per button.</summary>
	private static IReadOnlyCollection<int> SymbolFrame(int frame) => frame switch
	{
		1 => new[] { 81, 85 },
		5 => new[] { 81, 88 },
		9 => new[] { 81, 91 },
		13 => new[] { 81, 93 },
		17 => new[] { 81, 96 },
		21 => new[] { 81, 99 },
		25 => new[] { 103 },
		29 => new[] { 107 },
		33 => new[] { 112 },
		_ => new[] { 116 },
	};

	[TestCase]
	public void TestEveryLabelNamesItsPadButton()
	{
		AssertThat(PubGamePrompts.ButtonOfLabel("a")).IsEqual("A");
		AssertThat(PubGamePrompts.ButtonOfLabel("b")).IsEqual("B");
		AssertThat(PubGamePrompts.ButtonOfLabel("x")).IsEqual("X");
		AssertThat(PubGamePrompts.ButtonOfLabel("y")).IsEqual("Y");
		AssertThat(PubGamePrompts.ButtonOfLabel("ls")).IsEqual("LS");
		AssertThat(PubGamePrompts.ButtonOfLabel("rs")).IsEqual("RS");
		AssertThat(PubGamePrompts.ButtonOfLabel("lt")).IsEqual("LT");
		AssertThat(PubGamePrompts.ButtonOfLabel("rt")).IsEqual("RT");
		AssertThat(PubGamePrompts.ButtonOfLabel("lb")).IsEqual("LB");
		AssertThat(PubGamePrompts.ButtonOfLabel("rb")).IsEqual("RB");
		AssertThat(PubGamePrompts.ButtonOfLabel("starValue")).IsNull();
		AssertThat(PubGamePrompts.ButtonOfLabel("A")).IsNull();
		foreach (string label in PubGamePrompts.Labels)
		{
			AssertThat(Engine_PadRemap.FromGlyphButton(PubGamePrompts.ButtonOfLabel(label)!).HasValue).IsTrue();
		}
	}

	[TestCase]
	public void TestTheShapesAreWhatOnlyOneButtonsFramePlaces()
	{
		Dictionary<int, string> faces = PubGamePrompts.Faces(SymbolLabels, SymbolFrame);
		AssertThat(faces.Count).IsEqual(10);
		AssertThat(faces.ContainsKey(81)).IsFalse();
		AssertThat(faces[85]).IsEqual("A");
		AssertThat(faces[88]).IsEqual("B");
		AssertThat(faces[91]).IsEqual("X");
		AssertThat(faces[93]).IsEqual("Y");
		AssertThat(faces[96]).IsEqual("LS");
		AssertThat(faces[99]).IsEqual("RS");
		AssertThat(faces[103]).IsEqual("LT");
		AssertThat(faces[107]).IsEqual("RT");
		AssertThat(faces[112]).IsEqual("LB");
		AssertThat(faces[116]).IsEqual("RB");
	}

	[TestCase]
	public void TestASpriteWithoutTheFourFaceLabelsIsNotTheSymbol()
	{
		var tutorial = new Dictionary<string, int> { ["a"] = 1, ["b"] = 2, ["lt"] = 3 };
		AssertThat(PubGamePrompts.Faces(tutorial, SymbolFrame).Count).IsEqual(0);
		AssertThat(PubGamePrompts.Faces(new Dictionary<string, int>(), SymbolFrame).Count).IsEqual(0);
	}

	[TestCase]
	public void TestXboxPadWithNothingRemappedKeepsTheShippedArt()
	{
		WithPad("xbox", ControllerFamily.Xbox, keyboard: false, _ =>
		{
			foreach (string label in PubGamePrompts.Labels)
			{
				AssertThat(Engine_UiKeyGlyphs.PromptShowsShipped(PubGamePrompts.ButtonOfLabel(label)!)).IsTrue();
			}
		});
		WithPad("auto", ControllerFamily.Unknown, keyboard: false,
			_ => AssertThat(Engine_UiKeyGlyphs.PromptShowsShipped("A")).IsTrue());
	}

	[TestCase]
	public void TestAnyOtherStyleDrawsTheGlyph()
	{
		WithPad("playstation", ControllerFamily.Xbox, keyboard: false,
			_ => AssertThat(Engine_UiKeyGlyphs.PromptShowsShipped("A")).IsFalse());
		WithPad("switch", ControllerFamily.Xbox, keyboard: false,
			_ => AssertThat(Engine_UiKeyGlyphs.PromptShowsShipped("LT")).IsFalse());
		WithPad("auto", ControllerFamily.PlayStation5, keyboard: false,
			_ => AssertThat(Engine_UiKeyGlyphs.PromptShowsShipped("LS")).IsFalse());
		WithPad("auto", ControllerFamily.Nintendo, keyboard: false,
			_ => AssertThat(Engine_UiKeyGlyphs.PromptShowsShipped("Y")).IsFalse());
	}

	[TestCase]
	public void TestARemapDrawsOnlyTheButtonsItMoved()
	{
		WithPad("xbox", ControllerFamily.Xbox, keyboard: false, remap =>
		{
			remap.Set(PadInput.A, new[] { PadInput.B });
			remap.Set(PadInput.B, new[] { PadInput.A });
			remap.Set(PadInput.LT, System.Array.Empty<PadInput>());
			AssertThat(Engine_UiKeyGlyphs.PromptShowsShipped("A")).IsFalse();
			AssertThat(Engine_UiKeyGlyphs.PromptShowsShipped("B")).IsFalse();
			AssertThat(Engine_UiKeyGlyphs.PromptShowsShipped("LT")).IsFalse();
			AssertThat(Engine_UiKeyGlyphs.PromptShowsShipped("X")).IsTrue();
			AssertThat(Engine_UiKeyGlyphs.PromptShowsShipped("RT")).IsTrue();
		});

		// A second source beside the button itself still shows the button: its art is true.
		WithPad("xbox", ControllerFamily.Xbox, keyboard: false, remap =>
		{
			remap.Set(PadInput.X, new[] { PadInput.X, PadInput.RB });
			AssertThat(Engine_UiKeyGlyphs.PromptShowsShipped("X")).IsTrue();
		});
	}

	[TestCase]
	public void TestTheKeyboardDrawsTheKeycap()
	{
		WithPad("xbox", ControllerFamily.Xbox, keyboard: true, _ =>
		{
			foreach (string label in PubGamePrompts.Labels)
			{
				AssertThat(Engine_UiKeyGlyphs.PromptShowsShipped(PubGamePrompts.ButtonOfLabel(label)!)).IsFalse();
			}
		});
	}

	[TestCase]
	public void TestAPackFileForTheInputDrawsIt()
	{
		AssertThat(Engine_UiKeyGlyphs.KeepsShippedArt(false, GlyphStyle.Xbox, true, packed: false)).IsTrue();
		AssertThat(Engine_UiKeyGlyphs.KeepsShippedArt(false, GlyphStyle.Xbox, true, packed: true)).IsFalse();
		AssertThat(Engine_UiKeyGlyphs.KeepsShippedArt(true, GlyphStyle.Xbox, true, packed: false)).IsFalse();
		AssertThat(Engine_UiKeyGlyphs.KeepsShippedArt(false, GlyphStyle.Xbox, false, packed: false)).IsFalse();
		AssertThat(Engine_UiKeyGlyphs.KeepsShippedArt(false, GlyphStyle.PlayStation, true, packed: false)).IsFalse();
		AssertThat(Engine_UiKeyGlyphs.KeepsShippedArt(false, GlyphStyle.Switch, true, packed: false)).IsFalse();
	}

	[TestCase]
	public void TestTheBoardReadsTheCoresPadAndItsOwnClick()
	{
		var pad = new GameInput.PadState
		{
			Buttons = GameInput.PadB | GameInput.PadRight, LeftTrigger = 255, RightTrigger = 40,
			LeftX = -32767, LeftY = 16000, RightX = 9000, RightY = -9000,
		};
		PubGameInput.Sample board = PubGameInput.FromPad(pad, boardClick: false);
		AssertThat(board.Buttons).IsEqual(GamePad.BButton | GamePad.DpadRight);
		AssertThat(board.LeftTrigger).IsEqual(255);
		AssertThat(board.RightTrigger).IsEqual(40);
		AssertThat(board.ThumbLX).IsEqual(-32767);
		AssertThat(board.ThumbLY).IsEqual(16000);
		AssertThat(board.ThumbRX).IsEqual(9000);
		AssertThat(board.ThumbRY).IsEqual(-9000);

		// The board's left click is its confirm, on top of whatever the pad holds.
		AssertThat(PubGameInput.FromPad(pad, boardClick: true).Buttons).IsEqual(GamePad.AButton | GamePad.BButton | GamePad.DpadRight);
		AssertThat(PubGameInput.FromPad(new GameInput.PadState(), boardClick: false)).IsEqual(default(PubGameInput.Sample));
	}

	[TestCase]
	public void TestTheCoresPadBitsAreThePubGamesBits()
	{
		AssertThat((int)GameInput.PadA).IsEqual(GamePad.AButton);
		AssertThat((int)GameInput.PadB).IsEqual(GamePad.BButton);
		AssertThat((int)GameInput.PadX).IsEqual(GamePad.XButton);
		AssertThat((int)GameInput.PadY).IsEqual(GamePad.YButton);
		AssertThat((int)GameInput.PadLeftShoulder).IsEqual(GamePad.LeftShoulderButton);
		AssertThat((int)GameInput.PadRightShoulder).IsEqual(GamePad.RightShoulderButton);
		AssertThat((int)GameInput.PadStart).IsEqual(GamePad.Start);
		AssertThat((int)GameInput.PadUp).IsEqual(GamePad.DpadUp);
		AssertThat((int)GameInput.PadDown).IsEqual(GamePad.DpadDown);
		AssertThat((int)GameInput.PadLeft).IsEqual(GamePad.DpadLeft);
		AssertThat((int)GameInput.PadRight).IsEqual(GamePad.DpadRight);
	}

	private static void WithPad(string style, ControllerFamily family, bool keyboard, System.Action<Engine_PadRemap> body)
	{
		string oldStyle = Engine_UiKeyGlyphs.StyleSetting, oldPack = Engine_UiKeyGlyphs.PackName;
		ControllerFamily oldFamily = Engine_UiKeyGlyphs.PadFamily;
		int oldPlayer = Engine_UiKeyGlyphs.GlyphPlayer;
		bool oldKeyboard = Engine_UiInputMode.KeyboardMouse;
		Engine_PadRemap remap = Engine_PadRemap.For(1);
		try
		{
			Engine_UiKeyGlyphs.StyleSetting = style;
			Engine_UiKeyGlyphs.PadFamily = family;
			Engine_UiKeyGlyphs.PackName = string.Empty;
			Engine_UiKeyGlyphs.GlyphPlayer = 1;
			Engine_UiInputMode.KeyboardMouse = keyboard;
			remap.Reset();
			body(remap);
		}
		finally
		{
			remap.Reset();
			Engine_UiInputMode.KeyboardMouse = oldKeyboard;
			Engine_UiKeyGlyphs.StyleSetting = oldStyle;
			Engine_UiKeyGlyphs.PackName = oldPack;
			Engine_UiKeyGlyphs.PadFamily = oldFamily;
			Engine_UiKeyGlyphs.GlyphPlayer = oldPlayer;
		}
	}
}
