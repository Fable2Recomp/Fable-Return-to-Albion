namespace Refii.Tests.Ui;

using System.Collections.Generic;
using GdUnit4;
using Refii.Core;
using Refii.Titles.Fable2;
using Refii.Ui;
using static GdUnit4.Assertions;

/// <summary>
/// The expression wheel's items as SetItem states them (`@ExpressionWheel.Item`, TU1 `sub_826AC5F0`):
/// keyed by ring path and slot, drawn on `Layer.&lt;path&gt;&lt;slot&gt;`'s `Title` and `expressions`
/// children, the latest statement per slot winning. No Godot type is built.
/// </summary>
[TestSuite]
public sealed class WheelItemsTests
{
	private const string Menu = "wheelmenu.icons.icons.menu.item";
	private const string Submenu = "wheelmenu.icons.icons.submenu.item";

	private static BgfElement E(string name, int parent) =>
		new() { NameHash = Engine_GameHash.Sdbm(name), ParentOrdinal = parent };

	/// <summary>
	/// 0 Root, 1 Layer, 2 wheelmenu, 3 icons, 4 icons, 5 menu, 6 item0, 7 expressions, 8 Title,
	/// 9 item1, 10 Title, 11 expressions, 12 submenu, 13 item0, 14 Title, 15 expressions.
	/// </summary>
	private static Engine_BgfDocument Hud() => new()
	{
		Elements = new[]
		{
			E("Root", -1), E("Layer", 0), E("wheelmenu", 1), E("icons", 2), E("icons", 3), E("menu", 4),
			E("item0", 5), E("expressions", 6), E("Title", 6),
			E("item1", 5), E("Title", 9), E("expressions", 9),
			E("submenu", 4), E("item0", 12), E("Title", 13), E("expressions", 13),
		},
	};

	private static Engine_UiModel.GuiNativeCall Item(string path, int slot, string title, uint texture, string tag) =>
		new("@ExpressionWheel.Item", new List<Engine_UiModel.GuiNativeArgument>
		{
			new(Engine_UiModel.GuiNativeArgumentKind.String, path),
			new(Engine_UiModel.GuiNativeArgumentKind.String, slot.ToString()),
			new(Engine_UiModel.GuiNativeArgumentKind.String, title),
			new(Engine_UiModel.GuiNativeArgumentKind.String, texture.ToString("X8")),
			new(Engine_UiModel.GuiNativeArgumentKind.String, tag),
		});

	[TestCase]
	public void EachRingsSlotLandsOnItsOwnTitleAndExpressions()
	{
		var logged = new List<string>();
		var items = new Engine_UiWheelItems(logged.Add);
		Engine_BgfDocument hud = Hud();
		items.State(Item(Menu, 0, "Fun", 0x11111111u, "TEXT_FUN"));
		items.State(Item(Menu, 1, "Rude", 0x22222222u, "TEXT_RUDE"));
		items.State(Item(Submenu, 0, "Dance", 0x33333333u, "TEXT_DANCE"));

		string?[] texts = items.Texts(hud, new string?[hud.Elements.Length]);
		AssertThat(texts[8]).IsEqual("Fun");
		AssertThat(texts[10]).IsEqual("Rude");
		AssertThat(texts[14]).IsEqual("Dance");

		var textures = new uint[hud.Elements.Length];
		items.Textures(hud, textures);
		AssertThat(textures[7]).IsEqual(0x11111111u);
		AssertThat(textures[11]).IsEqual(0x22222222u);
		AssertThat(textures[15]).IsEqual(0x33333333u);
		AssertThat(logged.Count).IsEqual(0);
	}

	[TestCase]
	public void TheLatestStatementForASlotWinsAndTheToldTextIsKept()
	{
		var items = new Engine_UiWheelItems(_ => { });
		Engine_BgfDocument hud = Hud();
		items.State(Item(Menu, 0, "Fun", 1u, "TEXT_FUN"));
		int first = items.Generation;
		items.State(Item(Menu, 0, "Back", 2u, "GUI_BACK"));
		AssertThat(items.Generation > first).IsTrue();

		var told = new string?[hud.Elements.Length];
		told[2] = "kept";
		string?[] texts = items.Texts(hud, told);
		AssertThat(texts[8]).IsEqual("Back");
		AssertThat(texts[2]).IsEqual("kept");
		AssertThat(told[8]).IsNull();
	}

	[TestCase]
	public void ASlotTheFileLacksIsLoggedOnceAndNotDrawn()
	{
		var logged = new List<string>();
		var items = new Engine_UiWheelItems(logged.Add);
		Engine_BgfDocument hud = Hud();
		items.State(Item(Menu, 5, "Missing", 9u, "TEXT_X"));
		string?[] texts = items.Texts(hud, new string?[hud.Elements.Length]);
		items.Texts(hud, new string?[hud.Elements.Length]);
		AssertThat(System.Array.IndexOf(texts, "Missing")).IsEqual(-1);
		AssertThat(logged.Count).IsEqual(1);
	}
}
