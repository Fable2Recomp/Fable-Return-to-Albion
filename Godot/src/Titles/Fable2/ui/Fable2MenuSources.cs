using System.Collections.Generic;

namespace Refii.Titles.Fable2;

using Refii.Ui;
using Row = Refii.Ui.Engine_UiMenuSources.Row;

/// <summary>
/// Fable 2's own menu sources — the hero's inventory and weapons, the world map's quests, jobs,
/// regions, sales and family, the properties, the abilities and the shop — added to the shell's
/// (<see cref="Engine_UiMenuSources"/>) when the title installs.
/// </summary>
public static class Fable2MenuSources
{
	public static void Register()
	{
		// THE FIRST DATA-AREA SOURCE. `capture` and `spawn` both watch the guest build its own
		// menu; this one reads the hero's inventory and the shipped GDB directly, so the rows
		// exist without a Gameface screen ever having been built.
		Engine_UiMenuSources.RegisterGame(Engine_UiInventorySource.SourceName, Engine_UiInventorySource.Rows);

		// The weapons screen's two halves — one category, split by the item's WeaponType.
		Engine_UiMenuSources.RegisterGame(Engine_UiInventorySource.WeaponsSource, Engine_UiInventorySource.Weapons);

		// The world map's two halves of one list — see Engine_UiQuestSource.
		Engine_UiMenuSources.RegisterGame(Engine_UiQuestSource.QuestsSource, Engine_UiQuestSource.Quests);
		Engine_UiMenuSources.RegisterGame(Engine_UiQuestSource.JobsSource, Engine_UiQuestSource.Jobs);
		Engine_UiMenuSources.RegisterGame(Engine_UiQuestSource.RegionsSource, Engine_UiQuestSource.Regions);
		Engine_UiMenuSources.RegisterGame(Engine_UiQuestSource.SalesSource, Engine_UiQuestSource.Sales);
		Engine_UiMenuSources.RegisterGame(Engine_UiQuestSource.FamilySource, Engine_UiQuestSource.Family);
		Engine_UiMenuSources.RegisterGame(Engine_UiPropertySource.SourceName, Engine_UiPropertySource.Rows);
		Engine_UiMenuSources.RegisterGame(Engine_UiAbilitySource.SourceName, Engine_UiAbilitySource.Rows);

		// What a mod's menu lists without building the catalog: the hero's known expressions and
		// assignable wills, per player, as the guest tick states them (Engine_UiModListSources).
		Engine_UiMenuSources.RegisterGame(Engine_UiModListSources.ExpressionsSource, Engine_UiModListSources.ExpressionRows);
		Engine_UiMenuSources.RegisterGame(Engine_UiModListSources.SpellsKnownSource, Engine_UiModListSources.SpellRows);

		// The shop's six groups — the capture's rows with the TRANSACTION stamped on.
		Engine_UiMenuSources.RegisterGame(ShopSource, ShopRows);
	}

	/// <summary>`shop:<group>` — the capture under that group, each row carrying its commit.</summary>
	public const string ShopSource = "shop";

	/// <summary>The verb a shop row carries — `Shop:buy|sell:&lt;ticket&gt;`, see Engine_UiScreenActions.</summary>
	public const string ShopAction = "Shop:";

	/// <summary>
	/// The shop's rows are the capture's, with one thing added: WHICH TRANSACTION THE ROW IS.
	///
	/// A shop row's identity is a per-population TICKET, not a record hash — `sub_82880AC8`
	/// files the item behind a monotonic number and `sub_82880BC0` is the only way back. The
	/// ticket is what the guest puts in the row's `Float1|Float2`, which is exactly the pair
	/// the spawn capture already reads as `Id`, so the row arrives holding everything the
	/// commit needs.
	///
	/// Buy or sell is the GROUP, not the row: `sub_82994218` spawns into six parents whose
	/// names begin `Buy` or `Sell`, and the guest's own UI codes are 30 Buy / 31 Sell against
	/// the same split. So the command is stamped here rather than guessed at commit time.
	/// </summary>
	public static IReadOnlyList<Row> ShopRows(string group)
	{
		IReadOnlyList<Row> captured = Engine_UiMenuSources.CaptureRows(group);
		if (captured.Count == 0)
		{
			return captured;
		}

		bool selling = group.StartsWith("Sell", System.StringComparison.OrdinalIgnoreCase);
		var rows = new List<Row>(captured.Count);
		foreach (Row row in captured)
		{
			// A row with no ticket cannot be committed — ticket 0 is never issued
			// (`sub_82880AC8` pre-increments), so it stays a plain row rather than one whose
			// commit would be dropped by the pump with a puzzling message.
			rows.Add(row.Id == 0u
				? row
				: row with { Command = $"{ShopAction}{(selling ? "sell" : "buy")}:{row.Id}" });
		}

		return rows;
	}

}
