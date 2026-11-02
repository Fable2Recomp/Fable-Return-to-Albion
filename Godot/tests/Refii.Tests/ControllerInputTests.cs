namespace Refii.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Core;
using Refii.Game;
using Refii.Ui;

/// <summary>Controller detection, the game-button remap and the Controls page's binding rows — all pure.</summary>
[TestSuite]
public class ControllerInputTests
{
	[TestCase]
	public void TestVendorIdsDecideTheFamily()
	{
		AssertThat(Engine_ControllerFamily.Classify("Wireless Controller", null, 0x054C, 0x09CC)).IsEqual(ControllerFamily.PlayStation);
		AssertThat(Engine_ControllerFamily.Classify("Wireless Controller", null, 0x054C, 0x0CE6)).IsEqual(ControllerFamily.PlayStation5);
		AssertThat(Engine_ControllerFamily.Classify("Anything", null, 0x057E, 0x2009)).IsEqual(ControllerFamily.Nintendo);
		AssertThat(Engine_ControllerFamily.Classify("Anything", null, 0x045E, 0x028E)).IsEqual(ControllerFamily.Xbox);
	}

	[TestCase]
	public void TestTheSdlGuidCarriesTheIdsWhenThePlatformStatesNone()
	{
		// DualShock 4 over USB, Switch Pro over Bluetooth, Xbox 360 pad over USB.
		AssertThat(Engine_ControllerFamily.Classify("Wireless Controller", "030000004c050000cc09000011810000")).IsEqual(ControllerFamily.PlayStation);
		AssertThat(Engine_ControllerFamily.Classify("Pro", "050000007e0500000920000001800000")).IsEqual(ControllerFamily.Nintendo);
		AssertThat(Engine_ControllerFamily.Classify("Pad", "030000005e0400008e02000014010000")).IsEqual(ControllerFamily.Xbox);
		AssertThat(Engine_ControllerFamily.GuidIds("030000004c050000e60c000011810000")).IsEqual(((int, int)?)(0x054C, 0x0CE6));
		AssertThat(Engine_ControllerFamily.GuidIds("00000000000000000000000000000000")).IsNull();
		AssertThat(Engine_ControllerFamily.GuidIds("short")).IsNull();
	}

	[TestCase]
	public void TestNamesDecideWhenNoIdsDo()
	{
		AssertThat(Engine_ControllerFamily.ByName("DualSense Wireless Controller")).IsEqual(ControllerFamily.PlayStation5);
		AssertThat(Engine_ControllerFamily.ByName("PS4 Controller")).IsEqual(ControllerFamily.PlayStation);
		AssertThat(Engine_ControllerFamily.ByName("Sony DualShock 3")).IsEqual(ControllerFamily.PlayStation);
		AssertThat(Engine_ControllerFamily.ByName("Nintendo Switch Pro Controller")).IsEqual(ControllerFamily.Nintendo);
		AssertThat(Engine_ControllerFamily.ByName("Joy-Con (L/R)")).IsEqual(ControllerFamily.Nintendo);
		AssertThat(Engine_ControllerFamily.ByName("Xbox Series X Controller")).IsEqual(ControllerFamily.Xbox);
		AssertThat(Engine_ControllerFamily.ByName("Generic USB Joystick")).IsEqual(ControllerFamily.Unknown);
		AssertThat(Engine_ControllerFamily.ByName(null)).IsEqual(ControllerFamily.Unknown);
	}

	[TestCase]
	public void TestTheStyleSettingFollowsTheDeviceOrForcesOne()
	{
		AssertThat(Engine_ControllerFamily.Resolve("auto", ControllerFamily.PlayStation5)).IsEqual(GlyphStyle.PlayStation);
		AssertThat(Engine_ControllerFamily.Resolve("auto", ControllerFamily.Nintendo)).IsEqual(GlyphStyle.Switch);
		AssertThat(Engine_ControllerFamily.Resolve("", ControllerFamily.Unknown)).IsEqual(GlyphStyle.Xbox);
		AssertThat(Engine_ControllerFamily.Resolve("switch", ControllerFamily.Xbox)).IsEqual(GlyphStyle.Switch);
		AssertThat(Engine_ControllerFamily.Resolve("XBOX", ControllerFamily.PlayStation)).IsEqual(GlyphStyle.Xbox);
	}

	[TestCase]
	public void TestLabelsArePositional()
	{
		// The south button: Cross on PlayStation, B on Switch, A on Xbox.
		AssertThat(Engine_ControllerFamily.Label(GlyphStyle.PlayStation, PadInput.A)).IsEqual("Cross");
		AssertThat(Engine_ControllerFamily.Label(GlyphStyle.Switch, PadInput.A)).IsEqual("B");
		AssertThat(Engine_ControllerFamily.Label(GlyphStyle.Switch, PadInput.Y)).IsEqual("X");
		AssertThat(Engine_ControllerFamily.Label(GlyphStyle.Xbox, PadInput.A)).IsEqual("A");
		AssertThat(Engine_ControllerFamily.Label(GlyphStyle.PlayStation, PadInput.Back)).IsEqual("Share");
		AssertThat(Engine_ControllerFamily.Label(GlyphStyle.PlayStation, PadInput.Back, ps5: true)).IsEqual("Create");
		AssertThat(Engine_ControllerFamily.Label(GlyphStyle.Switch, PadInput.RT)).IsEqual("ZR");
	}

	[TestCase]
	public void TestTheDefaultRemapLeavesEveryButtonAlone()
	{
		var remap = new Engine_PadRemap();
		AssertThat(remap.IsDefault).IsTrue();
		uint buttons = GameInput.ButtonA | GameInput.DpadUp, lt = 90, rt = 0;
		remap.Apply(ref buttons, ref lt, ref rt);
		AssertThat(buttons).IsEqual(GameInput.ButtonA | GameInput.DpadUp);
		AssertThat(lt).IsEqual(90u);
	}

	[TestCase]
	public void TestASwapIsTwoAssignments()
	{
		var remap = new Engine_PadRemap();
		remap.Set(PadInput.A, new[] { PadInput.B });
		remap.Set(PadInput.B, new[] { PadInput.A });
		uint buttons = GameInput.ButtonB, lt = 0, rt = 0;
		remap.Apply(ref buttons, ref lt, ref rt);
		AssertThat(buttons).IsEqual(GameInput.ButtonA);
		buttons = GameInput.ButtonA | GameInput.ButtonX;
		remap.Apply(ref buttons, ref lt, ref rt);
		AssertThat(buttons).IsEqual(GameInput.ButtonB | GameInput.ButtonX);
	}

	[TestCase]
	public void TestTwoSourcesAndUnbound()
	{
		var remap = new Engine_PadRemap();
		remap.Set(PadInput.X, new[] { PadInput.X, PadInput.RB });
		remap.Set(PadInput.Back, Array.Empty<PadInput>());
		uint buttons = GameInput.RightShoulder, lt = 0, rt = 0;
		remap.Apply(ref buttons, ref lt, ref rt);
		AssertThat(buttons).IsEqual(GameInput.ButtonX | GameInput.RightShoulder);
		buttons = GameInput.Back;
		remap.Apply(ref buttons, ref lt, ref rt);
		AssertThat(buttons).IsEqual(0u);
		AssertThat(remap.Shown(PadInput.Back)).IsNull();
		AssertThat(remap.Shown(PadInput.X)).IsEqual((PadInput?)PadInput.X);
		AssertThat(remap.Targets(PadInput.RB).ToArray()).IsEqual(new[] { PadInput.X, PadInput.RB });
	}

	[TestCase]
	public void TestTriggersAndButtonsCrossOver()
	{
		var remap = new Engine_PadRemap();
		remap.Set(PadInput.LT, new[] { PadInput.LB });
		remap.Set(PadInput.A, new[] { PadInput.RT });
		remap.Set(PadInput.RT, new[] { PadInput.RT });
		uint buttons = GameInput.LeftShoulder, lt = 0, rt = 20;
		remap.Apply(ref buttons, ref lt, ref rt);
		AssertThat(lt).IsEqual(255u);
		AssertThat(buttons & GameInput.ButtonA).IsEqual(0u); // 20 is under the press threshold
		AssertThat(rt).IsEqual(20u); // a trigger pressing a trigger keeps its travel
		buttons = 0;
		lt = 0;
		rt = 200;
		remap.Apply(ref buttons, ref lt, ref rt);
		AssertThat(buttons & GameInput.ButtonA).IsEqual(GameInput.ButtonA);
		AssertThat(remap.Shown(PadInput.A)).IsEqual((PadInput?)PadInput.RT);
	}

	[TestCase]
	public void TestSettingsReadPerPlayer()
	{
		var values = new Dictionary<string, string>
		{
			["padmap_a"] = "B",
			["padmap_y"] = "none",
			["padmap_x"] = " \"X, RB\" ",
			["padmap2_start"] = "options",
			["padmap_lb"] = "nonsense",
		};
		var one = new Engine_PadRemap();
		var two = new Engine_PadRemap();
		one.Read(values, 1);
		two.Read(values, 2);
		AssertThat(one.Sources(PadInput.A).ToArray()).IsEqual(new[] { PadInput.B });
		AssertThat(one.Sources(PadInput.Y).Count).IsEqual(0);
		AssertThat(one.Sources(PadInput.X).ToArray()).IsEqual(new[] { PadInput.X, PadInput.RB });
		AssertThat(one.IsDefaultFor(PadInput.LB)).IsTrue();
		AssertThat(one.IsDefaultFor(PadInput.Start)).IsTrue();
		AssertThat(two.IsDefault).IsTrue(); // "options" is Start itself
		AssertThat(Engine_PadRemap.SettingKey(1, PadInput.DpadUp)).IsEqual("padmap_dpadup");
		AssertThat(Engine_PadRemap.SettingKey(2, PadInput.A)).IsEqual("padmap2_a");
		AssertThat(Engine_PadRemap.SettingKey(1, PadInput.A).StartsWith(Refii.Mods.ModInput.PadPrefix)).IsFalse();
	}

	[TestCase]
	public void TestStoredFormsRoundTrip()
	{
		AssertThat(Engine_PadRemap.ParseStored("")).IsNull();
		AssertThat(Engine_PadRemap.ParseStored("NONE")!.Count).IsEqual(0);
		AssertThat(Engine_PadRemap.ParseStored("12")).IsNull();
		AssertThat(Engine_PadRemap.ParseStored("L1, zr, LeftStick, minus")!.ToArray())
			.IsEqual(new[] { PadInput.LB, PadInput.RT, PadInput.L3, PadInput.Back });
		AssertThat(Engine_PadRemap.Format(new[] { PadInput.X, PadInput.DpadLeft })).IsEqual("X,DpadLeft");
		AssertThat(Engine_PadRemap.Format(Array.Empty<PadInput>())).IsEqual("none");
		AssertThat(Engine_PadRemap.FromGlyphButton("LS")).IsEqual((PadInput?)PadInput.L3);
		AssertThat(Engine_PadRemap.FromGlyphButton("DPad")).IsNull();
	}

	private static Engine_UiControlsPage.PadLine PadRow(PadInput game, GlyphStyle style = GlyphStyle.Xbox) =>
		new(1, game, () => style, () => false, "Melee, block, flourish");

	private static Func<string, string> Stored(string key, string value) => k => k == key ? value : string.Empty;

	[TestCase]
	public void TestARemapRowReadsAndWritesItsSetting()
	{
		Engine_UiControlsPage.PadLine row = PadRow(PadInput.X);
		AssertThat(row.IndexOfStored(_ => string.Empty)).IsEqual(Engine_UiControlsPage.PadLine.DefaultPosition);
		AssertThat(row.IndexOfStored(Stored("padmap_x", "none"))).IsEqual(Engine_UiControlsPage.PadLine.UnboundIndex);
		int y = row.IndexOfStored(Stored("padmap_x", "Y"));
		AssertThat(y).IsEqual(Engine_UiControlsPage.PadLine.FirstInput + (int)PadInput.Y);

		var values = new Dictionary<string, string?>();
		row.Store(y, values);
		AssertThat(values["padmap_x"]).IsEqual("Y");
		row.Store(Engine_UiControlsPage.PadLine.DefaultPosition, values);
		AssertThat(values["padmap_x"]).IsNull();
		row.Store(Engine_UiControlsPage.PadLine.FirstInput + (int)PadInput.X, values);
		AssertThat(values["padmap_x"]).IsNull(); // X on X is the default: nothing stored
	}

	[TestCase]
	public void TestATwoSourceRowIsKeptWhole()
	{
		Engine_UiControlsPage.PadLine row = PadRow(PadInput.X);
		int at = row.IndexOfStored(Stored("padmap_x", "X,RB"));
		AssertThat(at).IsEqual(row.Choices - 1);
		AssertThat(row.Caption(at)).IsEqual("X + RB");
		var values = new Dictionary<string, string?>();
		row.Store(at, values);
		AssertThat(values["padmap_x"]).IsEqual("X,RB");
	}

	[TestCase]
	public void TestARemapRowSpeaksThePlayersStyle()
	{
		Engine_UiControlsPage.PadLine row = PadRow(PadInput.X, GlyphStyle.PlayStation);
		AssertThat(row.Label).IsEqual("Square: Melee, block, flourish");
		AssertThat(row.Caption(Engine_UiControlsPage.PadLine.DefaultPosition)).IsEqual("Default (Square)");
		AssertThat(row.Caption(Engine_UiControlsPage.PadLine.FirstInput + (int)PadInput.A)).IsEqual("Cross");
		AssertThat(row.IndexOfCaptured("RT")).IsEqual(Engine_UiControlsPage.PadLine.FirstInput + (int)PadInput.RT);
		AssertThat(row.IndexOfCaptured("X")).IsEqual(Engine_UiControlsPage.PadLine.DefaultPosition);
		AssertThat(row.IndexOfCaptured("Guide")).IsEqual(-1);
	}

	private static readonly string[] Keys = { "E", "F", "Space", "MouseLeft" };

	[TestCase]
	public void TestAKeyRowReadsWritesAndKeepsWhatItCannotStep()
	{
		var row = new Engine_UiControlsPage.KeyLine("interact", "E,Space,Enter", Keys);
		AssertThat(row.Label).IsEqual("Interact");
		AssertThat(row.IndexOfStored(_ => string.Empty)).IsEqual(Engine_UiControlsPage.KeyLine.DefaultPosition);
		AssertThat(row.IndexOfStored(Stored("key_interact", "E,Space,Enter"))).IsEqual(Engine_UiControlsPage.KeyLine.DefaultPosition);
		AssertThat(row.IndexOfStored(Stored("key_interact", "none"))).IsEqual(Engine_UiControlsPage.KeyLine.UnboundIndex);
		AssertThat(row.IndexOfStored(Stored("key_interact", "f"))).IsEqual(Engine_UiControlsPage.KeyLine.FirstInput + 1);

		int pair = row.IndexOfStored(Stored("key_interact", "F,G"));
		AssertThat(pair).IsEqual(row.Choices - 1);
		var values = new Dictionary<string, string?>();
		row.Store(pair, values);
		AssertThat(values["key_interact"]).IsEqual("F,G");
		row.Store(Engine_UiControlsPage.KeyLine.UnboundIndex, values);
		AssertThat(values["key_interact"]).IsEqual("none");
		row.Store(Engine_UiControlsPage.KeyLine.DefaultPosition, values);
		AssertThat(values["key_interact"]).IsNull();
	}

	[TestCase]
	public void TestAKeyRowTakesACapturedKeyEvenUnlisted()
	{
		var row = new Engine_UiControlsPage.KeyLine("melee", "F", Keys);
		AssertThat(row.IndexOfCaptured("MouseLeft")).IsEqual(Engine_UiControlsPage.KeyLine.FirstInput + 3);
		int other = row.IndexOfCaptured("KpAdd");
		AssertThat(other).IsEqual(Engine_UiControlsPage.KeyLine.FirstInput + Keys.Length);
		AssertThat(row.Text(other)).IsEqual("KpAdd");
		AssertThat(row.Bound(Engine_UiControlsPage.KeyLine.DefaultPosition).ToArray()).IsEqual(new[] { "key:f" });
	}

	[TestCase]
	public void TestDesignedSharedDefaultsAreNotConflicts()
	{
		var pause = new Engine_UiControlsPage.KeyLine("pause", "Escape,Tab", Keys);
		AssertThat(pause.DefaultsBind("key:escape")).IsTrue();
		AssertThat(pause.DefaultsBind("key:f")).IsFalse();
	}

	[TestCase]
	public void TestAChoiceRowKeepsAnUnofferedValue()
	{
		var row = new Engine_UiControlsPage.ChoiceLine("Glyph pack", "glyph_pack",
			new[] { (string.Empty, "Built-in"), ("clean", "clean") });
		AssertThat(row.IndexOfStored(_ => string.Empty)).IsEqual(0);
		AssertThat(row.IndexOfStored(Stored("glyph_pack", "clean"))).IsEqual(1);
		int gone = row.IndexOfStored(Stored("glyph_pack", "removed"));
		AssertThat(gone).IsEqual(2);
		AssertThat(row.Choices).IsEqual(3);
		var values = new Dictionary<string, string?>();
		row.Store(gone, values);
		AssertThat(values["glyph_pack"]).IsEqual("removed");
		row.Store(0, values);
		AssertThat(values["glyph_pack"]).IsNull();
	}

	[TestCase]
	public void AFormerDefaultReadsAsTodaysDefault()
	{
		// The launcher wrote every key of its day: an install holds target_lock = "MouseRight", which
		// must take today's "MouseRight,T"; a key the player chose is kept as chosen.
		var action = new GameInput.KeyAction("target_lock", "MouseRight,T", "", new[] { "MouseRight" });
		AssertThat(action.IsDefault("MouseRight")).IsTrue();
		AssertThat(action.IsDefault("\"mouseright\"")).IsTrue();
		AssertThat(action.IsDefault(" MouseRight , T ")).IsTrue();
		AssertThat(action.IsDefault("")).IsTrue();
		AssertThat(action.Bound("MouseRight")).IsEqual("MouseRight,T");
		AssertThat(action.IsDefault("G")).IsFalse();
		AssertThat(action.Bound("G")).IsEqual("G");
		AssertThat(action.IsDefault("none")).IsFalse();
		AssertThat(new GameInput.KeyAction("melee", "F", "").IsDefault("F")).IsTrue();
	}
}
