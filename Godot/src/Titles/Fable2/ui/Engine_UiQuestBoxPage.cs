using System.Collections.Generic;

namespace Refii.Titles.Fable2;

using Refii.Ui;

/// <summary>
/// THE QUEST BOX (screen 5, `questrepeatable.bgf`), stated by the box's own Open.
///
/// It is not a screen class but a box page (vtable `off_820AE6C8`, kind 4 → screen 5) built by
/// the GUI native `DisplayQuestBoxParams(table, text)` (`sub_82304920` → `sub_826B96F8`, which
/// copies the table into the box). Its Open (`sub_823CFF60`) is hooked and states the box's fields
/// (`@DisplayBox.Open` kind 4, display_box_hooks.cpp); on show the root goes to slide `Type` and
/// the filler `sub_823CFBE0` writes, under `elements`:
/// <list type="bullet">
/// <item>`QuestDescription` ← `box+8`, the call's text (argument 2, localised with `%n`
/// substitution).</item>
/// <item>`Stars` slide `RiskLevel + 5`, only for a risk level 1..5 (0x823cfc3c..58) — the second
/// set of `1star`..`5star`.</item>
/// <item>`DetailsGroup.{Occupation,Appearance,Features,Name,Whereabouts,Reason}Amount` ← the
/// table's `Occupation`, `Appearance`, `Features`, `TargetName`, `Whereabouts`, `Reason`,
/// localised.</item>
/// <item>`Remember` shown iff there is a condition; `Condition1` / `Condition2` ← the table's
/// conditions, localised, shown iff there are at least one / two. A condition counts unless it
/// is `"nocondition"` (0x82304a44..7c): two when `Condition2` is real, else one when
/// `Condition1` is.</item>
/// </list>
/// `Name` is stored (page+4) and never drawn.
/// </summary>
public static class Engine_UiQuestBoxPage
{
	public const uint ScreenId = 5;

	/// <summary>One open box's page — every string localised.</summary>
	public sealed record Statement(
		int Type, int RiskLevel, string Description,
		string Occupation, string Appearance, string Features, string TargetName,
		string Whereabouts, string Reason, string Condition1, string Condition2, int Conditions);

	/// <summary>
	/// The page of a stated quest box, its tags localised as the filler localises them
	/// (`sub_8230E0E8`); null for no box. The fields are the hook's order: Type `+24`, RiskLevel
	/// `+28`, the condition count `+32` (0x82304a44..7c: two when `Condition2` is not
	/// `"nocondition"`, else one when `Condition1` is not), then the tags `+36` Condition1, `+40`
	/// Condition2, `+44` Occupation, `+48` Appearance, `+52` Features, `+56` TargetName, `+60`
	/// Whereabouts, `+64` Reason.
	/// </summary>
	public static Statement? Of(Engine_UiDisplayBoxes.Box? box, Refii.Text.Engine_BabelDatabase? strings)
	{
		if (box is not { Kind: Engine_UiDisplayBoxes.Quest })
		{
			return null;
		}

		string Text(int field) => Engine_UiGuiText.Resolve(box.Field(field), strings);
		int conditions = box.Number(2);
		return new Statement(
			box.Number(0), box.Number(1), box.Body,
			Text(5), Text(6), Text(7), Text(8), Text(9), Text(10),
			conditions > 0 ? Text(3) : string.Empty,
			conditions > 1 ? Text(4) : string.Empty,
			conditions);
	}

	private const string Elements = "elements.";

	/// <summary>The root element carries no authored name: it is addressed by ordinal.</summary>
	private const string Root = "#0";

	public static IReadOnlyDictionary<string, string> Values(Statement? stated)
	{
		var values = new Dictionary<string, string>();
		if (stated is not { } s)
		{
			return values;
		}

		values[Elements + "QuestDescription"] = s.Description;
		values[Elements + "DetailsGroup.OccupationAmount"] = s.Occupation;
		values[Elements + "DetailsGroup.AppearanceAmount"] = s.Appearance;
		values[Elements + "DetailsGroup.FeaturesAmount"] = s.Features;
		values[Elements + "DetailsGroup.NameAmount"] = s.TargetName;
		values[Elements + "DetailsGroup.WhereaboutsAmount"] = s.Whereabouts;
		values[Elements + "DetailsGroup.ReasonAmount"] = s.Reason;
		values[Elements + "Condition1"] = s.Condition1;
		values[Elements + "Condition2"] = s.Condition2;
		return values;
	}

	public static IReadOnlyDictionary<string, bool> Active(Statement? stated)
	{
		var active = new Dictionary<string, bool>();
		if (stated is not { } s)
		{
			return active;
		}

		active[Elements + "Remember"] = s.Conditions > 0;
		active[Elements + "Condition1"] = s.Conditions > 0;
		active[Elements + "Condition2"] = s.Conditions > 1;
		return active;
	}

	public static IReadOnlyDictionary<string, int> Slides(Statement? stated)
	{
		var slides = new Dictionary<string, int>();
		if (stated is not { } s)
		{
			return slides;
		}

		if (s.Type > 0)
		{
			slides[Root] = s.Type;
		}

		if (s.RiskLevel is >= 1 and <= 5)
		{
			slides[Elements + "Stars"] = s.RiskLevel + 5;
		}

		return slides;
	}
}
