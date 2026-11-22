namespace Refii.Tests.Ui;

using System.Collections.Generic;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Ui;

/// <summary>
/// The `g_Menu:` statements the model takes beside the population: the input lock, the one-level
/// menu stack, the four statements about the highlighted row (named by the runner's
/// <see cref="Engine_UiMenuModel.HighlightedAttribute"/>), the ghost refresh and the script-init
/// boundary. Pure model code — every chunk here is a handled one, so nothing reaches Godot's log.
/// </summary>
[TestSuite]
public class MenuModelStatementsTests
{
	private const uint Shop = 0x4A001200u;

	private static Engine_UiModel.MenuChunk Chunk(string text, params (string Name, string Value)[] staged)
	{
		var bag = new Dictionary<string, string>(System.StringComparer.Ordinal);
		foreach ((string name, string value) in staged)
		{
			bag[name] = value;
		}

		return new Engine_UiModel.MenuChunk(Shop, text, bag);
	}

	private static Engine_UiModel.MenuChunk Spawn(string name, string parent = "Root") =>
		Chunk("g_Menu:SpawnButton()", ("String1", name), ("String2", name + " label"), ("String4", parent),
			("Float1", "3"), ("Float2", "0"), ("Float3", "7"));

	/// <summary>`SetExpandableMenuDisabled(true|false)` is the lock; a list boundary keeps it.</summary>
	[TestCase]
	public void InputLockFollowsTheTwoLiteralsAndOutlivesARepopulation()
	{
		var model = new Engine_UiMenuModel();
		model.Apply(Chunk("g_Menu:SetExpandableMenuDisabled(true)"));
		AssertThat(model.For(Shop).InputDisabled).IsTrue();

		model.Apply(Chunk("g_Menu:DeleteAllSpawnedMenuItems()"));
		AssertThat(model.For(Shop).InputDisabled).IsTrue();

		model.Apply(Chunk("g_Menu:SetExpandableMenuDisabled(false)"));
		AssertThat(model.For(Shop).InputDisabled).IsFalse();
	}

	/// <summary>The presentation's script init forgets everything the address held.</summary>
	[TestCase]
	public void ScriptInitStartsTheMenuOver()
	{
		var model = new Engine_UiMenuModel();
		model.Apply(Spawn("Sword"));
		model.Apply(Chunk("g_Menu:SetExpandableMenuDisabled(true)"));
		model.Apply(Chunk(Engine_UiMenuModel.PresentationInitChunk));

		AssertThat(model.Menus.ContainsKey(Shop)).IsFalse();
		AssertThat(model.For(Shop).Rows.Count).IsEqual(0);
		AssertThat(model.For(Shop).InputDisabled).IsFalse();
	}

	/// <summary>Push saves the menu and starts empty; Pop restores rows, script and cursor once.</summary>
	[TestCase]
	public void PushAndPopKeepOneSavedMenu()
	{
		var model = new Engine_UiMenuModel();
		model.Apply(Chunk("g_Menu:RepopulateMenuFromPopulationScript( 'ItemsMenu' )"));
		model.Apply(Spawn("Potions", "Root"));
		model.Apply(Spawn("Health Potion", "Potions"));
		model.Apply(Chunk("g_Menu:PushMenuState()", (Engine_UiMenuModel.HighlightedAttribute, "Health Potion")));

		Engine_UiMenuModel.Menu menu = model.For(Shop);
		AssertThat(menu.Pushed).IsTrue();
		AssertThat(menu.Rows.Count).IsEqual(0);

		model.Apply(Chunk("g_Menu:RepopulateMenuFromPopulationScript( 'AugmentMenu' )"));
		model.Apply(Spawn("Sharpness"));
		AssertThat(menu.PopulationScript).IsEqual("AugmentMenu");

		model.Apply(Chunk("g_Menu:PopMenuState()"));
		AssertThat(menu.Pushed).IsFalse();
		AssertThat(menu.Rows.Count).IsEqual(2);
		AssertThat(menu.Find("Health Potion")).IsNotNull();
		AssertThat(menu.Find("Sharpness")).IsNull();
		AssertThat(menu.PopulationScript).IsEqual("ItemsMenu");
		AssertThat(menu.TakeRestoredHighlight()).IsEqual("Health Potion");
		AssertThat(menu.TakeRestoredHighlight()).IsNull();

		// A second pop with nothing pushed changes nothing.
		ulong generation = menu.Generation;
		model.Apply(Chunk("g_Menu:PopMenuState()"));
		AssertThat(menu.Generation).IsEqual(generation);
		AssertThat(menu.Rows.Count).IsEqual(2);
	}

	/// <summary>
	/// The shop's sold-out sequence `sub_826B0B90`: ghost the selected row, clear its emphasis,
	/// refresh the ghosting, rename it `SOLD` — every step on the row the runner named.
	/// </summary>
	[TestCase]
	public void SelectedStatementsActOnTheStatedHighlightedRow()
	{
		var model = new Engine_UiMenuModel();
		model.Apply(Spawn("Sword"));
		model.Apply(Spawn("Axe"));
		Engine_UiMenuModel.Menu menu = model.For(Shop);
		menu.Find("Sword")!.Emphasis = Engine_UiMenuConstants.Emphasis.SuggestedMenuItem;

		const string selected = Engine_UiMenuModel.HighlightedAttribute;
		model.Apply(Chunk("g_Menu:SetSelectedItemGhosting()", ("Float1", "1"), (selected, "Sword")));
		model.Apply(Chunk("g_Menu:SetSelectedItemEmphasis()", ("Float1", "0"), (selected, "Sword")));
		model.Apply(Chunk("g_MenuFormatter:UpdateAllGhosting()"));
		model.Apply(Chunk("g_Menu:RenameSelectedButton()", ("String1", "SOLD"), (selected, "Sword")));

		AssertThat(menu.Find("Sword")).IsNull();
		Engine_UiMenuModel.Row sold = menu.Find("SOLD")!;
		AssertThat(sold.Ghosted).IsTrue();
		AssertThat(sold.Emphasis).IsEqual(Engine_UiMenuConstants.Emphasis.None);
		AssertThat(sold.Label).IsEqual("Sword label");
		AssertThat(menu.Ghosting["Sword"]).IsTrue();
		AssertThat(menu.Emphases["Sword"]).IsEqual(Engine_UiMenuConstants.Emphasis.None);
		AssertThat(menu.Renames["Sword"]).IsEqual("SOLD");
		AssertThat(menu.GhostRefreshes).IsEqual(1);
		AssertThat(menu.Find("Axe")!.Ghosted).IsFalse();
	}

	private static Engine_UiModel.MenuChunk SpawnItem(string name, string parent, int ticket, string label) =>
		Chunk("g_Menu:SpawnButton()", ("String1", name), ("String2", label), ("String4", parent),
			("Float1", ticket.ToString()), ("Float2", "0"), ("Float3", "31"));

	/// <summary>
	/// The shop spawns one item name under Buy and under Sell (`sub_826CE230`): two rows, and the
	/// Buy rows' restate after a sale (`RefreshChildsButtonText`, scoped by `String4`) leaves the
	/// Sell row's caption alone.
	/// </summary>
	[TestCase]
	public void SameNamedRowsUnderTwoGroupsStayTwoRows()
	{
		var model = new Engine_UiMenuModel();
		model.Apply(SpawnItem("Tuna", "BuyItems", 676, "Tuna"));
		model.Apply(SpawnItem("Tuna", "SellItems", 603, "Tuna x2"));
		Engine_UiMenuModel.Menu menu = model.For(Shop);
		AssertThat(menu.Rows.Count).IsEqual(2);

		model.Apply(Chunk("g_Menu:RefreshChildsButtonText()",
			("String1", "Tuna"), ("String2", "Tuna<exclamation.img>"), ("String4", "BuyItems")));
		AssertThat(menu.Find("Tuna", 676u)!.Label).IsEqual("Tuna<exclamation.img>");
		AssertThat(menu.Find("Tuna", 603u)!.Label).IsEqual("Tuna x2");

		// The same row stated again is still one row.
		model.Apply(SpawnItem("Tuna", "SellItems", 603, "Tuna"));
		AssertThat(menu.Rows.Count).IsEqual(2);
	}

	/// <summary>
	/// With the selected row's id stated beside its name, the sold-out sequence lands on THAT row:
	/// the Sell row, not the same-named Buy row spawned before it.
	/// </summary>
	[TestCase]
	public void SelectedStatementsWithAnIdActOnThatRowOnly()
	{
		var model = new Engine_UiMenuModel();
		model.Apply(SpawnItem("Tuna", "BuyItems", 676, "Tuna"));
		model.Apply(SpawnItem("Tuna", "SellItems", 603, "Tuna x2"));
		Engine_UiMenuModel.Menu menu = model.For(Shop);

		(string, string) selected = (Engine_UiMenuModel.HighlightedAttribute, "Tuna");
		(string, string) id = (Engine_UiMenuModel.HighlightedIdAttribute, "603");
		model.Apply(Chunk("g_Menu:RefreshSelectedButtonText()",
			("String1", "Tuna"), ("String2", "Tuna"), selected, id));
		AssertThat(menu.Find("Tuna", 603u)!.Label).IsEqual("Tuna");

		model.Apply(Chunk("g_Menu:SetSelectedItemGhosting()", ("Float1", "1"), selected, id));
		model.Apply(Chunk("g_Menu:SetSelectedItemEmphasis()", ("Float1", "0"), selected, id));
		model.Apply(Chunk("g_Menu:RenameSelectedButton()", ("String1", "SOLD"), selected, id));

		Engine_UiMenuModel.Row buy = menu.Find("Tuna", 676u)!;
		AssertThat(buy.Parent).IsEqual("BuyItems");
		AssertThat(buy.Ghosted).IsFalse();
		Engine_UiMenuModel.Row sold = menu.Find("SOLD", 603u)!;
		AssertThat(sold.Parent).IsEqual("SellItems");
		AssertThat(sold.Ghosted).IsTrue();

		// Kept by identity, under both names the row has had, and out of the by-name stores.
		AssertThat(menu.Selected.Count).IsEqual(1);
		Engine_UiMenuModel.SelectedRow kept = menu.Selected[0];
		AssertThat(kept.Name).IsEqual("SOLD");
		AssertThat(kept.Names("Tuna", 603u)).IsTrue();
		AssertThat(kept.Names("SOLD", 603u)).IsTrue();
		AssertThat(kept.Names("Tuna", 676u)).IsFalse();
		AssertThat(kept.Ghosted).IsEqual(true);
		AssertThat(kept.Label).IsEqual("Tuna");
		AssertThat(menu.Ghosting.Count + menu.Emphases.Count + menu.Renames.Count + menu.Labels.Count)
			.IsEqual(0);

		// A list boundary drops them with the rows.
		model.Apply(Chunk("g_Menu:DeleteAllSpawnedMenuItems()"));
		AssertThat(menu.Selected.Count).IsEqual(0);
	}

	/// <summary>`RefreshSelectedButtonText` re-captions only when the highlighted row is `String1`.</summary>
	[TestCase]
	public void RefreshSelectedTextNeedsTheNamedRowHighlighted()
	{
		var model = new Engine_UiMenuModel();
		model.Apply(Spawn("Sword"));
		model.Apply(Spawn("Axe"));
		Engine_UiMenuModel.Menu menu = model.For(Shop);
		const string selected = Engine_UiMenuModel.HighlightedAttribute;

		model.Apply(Chunk("g_Menu:RefreshSelectedButtonText()",
			("String1", "Sword"), ("String2", "Sword (2)"), (selected, "Axe")));
		AssertThat(menu.Find("Sword")!.Label).IsEqual("Sword label");
		AssertThat(menu.Labels.Count).IsEqual(0);

		model.Apply(Chunk("g_Menu:RefreshSelectedButtonText()",
			("String1", "Sword"), ("String2", "Sword (2)"), (selected, "Sword")));
		AssertThat(menu.Find("Sword")!.Label).IsEqual("Sword (2)");
		AssertThat(menu.Labels["Sword"]).IsEqual("Sword (2)");
	}

	/// <summary>With no row stated as highlighted the script's `nil` arm runs: nothing changes.</summary>
	[TestCase]
	public void SelectedStatementsWithNoStatedRowChangeNothing()
	{
		var model = new Engine_UiMenuModel();
		model.Apply(Spawn("Sword"));
		Engine_UiMenuModel.Menu menu = model.For(Shop);
		ulong generation = menu.Generation;

		model.Apply(Chunk("g_Menu:SetSelectedItemGhosting()", ("Float1", "1")));
		model.Apply(Chunk("g_Menu:SetSelectedItemEmphasis()", ("Float1", "2")));
		model.Apply(Chunk("g_Menu:RenameSelectedButton()", ("String1", "SOLD")));
		model.Apply(Chunk("g_Menu:RefreshSelectedButtonText()", ("String1", "Sword"), ("String2", "x")));

		AssertThat(menu.Generation).IsEqual(generation);
		AssertThat(menu.Find("Sword")!.Ghosted).IsFalse();
		AssertThat(menu.Ghosting.Count + menu.Emphases.Count + menu.Renames.Count + menu.Labels.Count)
			.IsEqual(0);
	}

	/// <summary>A list boundary drops the by-name statements with the rows they were about.</summary>
	[TestCase]
	public void ListBoundaryDropsByNameStatements()
	{
		var model = new Engine_UiMenuModel();
		model.Apply(Spawn("Sword"));
		const string selected = Engine_UiMenuModel.HighlightedAttribute;
		model.Apply(Chunk("g_Menu:SetSelectedItemEmphasis()", ("Float1", "1"), (selected, "Sword")));
		model.Apply(Chunk("g_Menu:RenameSelectedButton()", ("String1", "SOLD"), (selected, "Sword")));
		model.Apply(Chunk("g_Menu:DeleteAllSpawnedMenuItems()"));

		Engine_UiMenuModel.Menu menu = model.For(Shop);
		AssertThat(menu.Rows.Count).IsEqual(0);
		AssertThat(menu.Emphases.Count).IsEqual(0);
		AssertThat(menu.Renames.Count).IsEqual(0);
	}

	/// <summary>
	/// The formatter's end fades are formatter globals: a list boundary and a Push/Pop keep them,
	/// the script init clears them. `HandleEnter` (an empty function) and the read-backs change
	/// nothing.
	/// </summary>
	[TestCase]
	public void EndFadesOutliveTheListAndDieWithTheScript()
	{
		var model = new Engine_UiMenuModel();
		model.Apply(Spawn("Sword"));
		model.Apply(Chunk("g_MenuFormatter:FadeOutBottomItemEarly()"));
		Engine_UiMenuModel.Menu menu = model.For(Shop);
		AssertThat(menu.FadesTopEnd).IsFalse();
		AssertThat(menu.FadesBottomEnd).IsTrue();

		model.Apply(Chunk("g_MenuFormatter:FadeOutTopItemEarly()"));
		model.Apply(Chunk("g_Menu:DeleteAllSpawnedMenuItems()"));
		model.Apply(Chunk("g_Menu:PushMenuState()"));
		model.Apply(Chunk("g_Menu:PopMenuState()"));
		AssertThat(menu.FadesTopEnd).IsTrue();
		AssertThat(menu.FadesBottomEnd).IsTrue();

		ulong generation = menu.Generation;
		model.Apply(Chunk("g_Menu:HandleEnter()"));
		model.Apply(Chunk("g_Menu:GetHighlightedMenuItemData()"));
		AssertThat(menu.Generation).IsEqual(generation);

		model.Apply(Chunk(Engine_UiMenuModel.PresentationInitChunk));
		AssertThat(model.For(Shop).FadesTopEnd).IsFalse();
		AssertThat(model.For(Shop).FadesBottomEnd).IsFalse();
	}
}
