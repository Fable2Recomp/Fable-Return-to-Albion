using System.Globalization;

namespace Refii.Titles.Fable2;

using Refii.Ui;

/// <summary>
/// The abilities book's unlearn question for a MOD ability (<see cref="Engine_UiModAbilities"/>), in the
/// book's own words: the GUI_SCREEN_ABILITY* strings of Fable 2's inventory text.
/// </summary>
public static class Fable2ModAbilityRefund
{
	/// <summary>
	/// Y on a mod ability's row, as the game's own unlearn runs it (TU1 sub_8246D510 and the
	/// screen's confirm before it): nothing to unlearn says so; otherwise the game's confirm
	/// question, and on yes the refund and the game's result notice — "some knowledge" for a
	/// level given up, "all knowledge" for the last. The game's own texts, from Babel, with the
	/// ability's name and the refund filled in.
	/// </summary>
	public static void AskRefund(Engine_UiModAbilities.Ability ability, int player = 0)
	{
		if (!ability.Refunds)
		{
			return;
		}

		int reader = Engine_UiModAbilities.Reader(player);
		Refii.Text.Engine_BabelDatabase? book = Engine_UiInventorySource.Strings;
		string Text(string tag)
		{
			string text = book is not null && book.TryGetString(tag, out string found) ? found : string.Empty;
			string name = book is not null && book.TryGetString(ability.NameTag, out string named) ? named : ability.Name;
			return text
				.Replace("%1", name, System.StringComparison.Ordinal)
				.Replace("%2", (Engine_UiModAbilities.RefundAmount(ability, reader) ?? 0).ToString(CultureInfo.InvariantCulture),
					System.StringComparison.Ordinal);
		}

		int level = Engine_UiModAbilities.Level(ability, reader);
		if (level <= 0)
		{
			Engine_UiHostBoxes.Tell(Text("GUI_SCREEN_ABILITY_CANNOT_DISCARD_ABILITY"));
			return;
		}

		string result = Text(level == 1
			? "GUI_SCREEN_ABILITY_DISCARD_ALL_KNOWLEDGE_OF_ABILITY"
			: "GUI_SCREEN_ABILITY_DISCARD_SOME_KNOWLEDGE_OF_ABILITY");
		Engine_UiHostBoxes.Ask(Text("GUI_SCREEN_ABILITIES_DISCARD_CONFIRM"), () =>
		{
			Engine_UiModAbilities.Refund(ability, reader);
			Engine_UiHostBoxes.Tell(result);
		});
	}
}
