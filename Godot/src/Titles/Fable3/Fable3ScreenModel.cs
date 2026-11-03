namespace Refii.Titles.Fable3;

using System.Collections.Generic;
using System.Globalization;

/// <summary>
/// The pages of Fable 3's element-written box-manager screens — hero status, stats, promises,
/// quest summary, king's schedule — from the element values the guest STATES through the hooked
/// element setters (gui_element_hooks.cpp: state, opacity/visibility, text, timeline). Pure, so
/// the tests can run it against a stated set. Every path, value and route below is the screen
/// class's own (Fable 3 IDB; the class vtables are display_box_hooks.cpp's `screen:*` kinds).
/// </summary>
/// <remarks>
/// <para>THE CLASSES. Slot 1 Show, 2 Close, 4 input (EGameAction in `*ev`), 5 Update:
/// HeroStatus 0x820C345C (sub_829758C8 / sub_829759D0 / sub_82975A58 / sub_82975958),
/// Stats 0x820C2E28 (sub_8296D4A0 / sub_8296D7A0 / sub_8296DA10 / sub_8296D8B0),
/// Promises 0x820AFD94 (sub_827A3398 / sub_829759D0 / sub_827A3600 / sub_827A3580),
/// QuestSummary 0x820C3214 (sub_829739D8 / sub_82974500 / sub_829745B0),
/// KingsSchedule 0x820C30E0 (sub_82972FD8 / sub_829732A0 / sub_82973428). Paths are the
/// classes' static CCharStrings (their initialisers concatenate a root and a suffix, e.g.
/// sub_831DD058 = `StatsBox` + `.Buttons.A_Button`); a row index is `%d` (sub_822E65C8).</para>
/// <para>Labels the guest never writes are the screens' .bgf tags (herostatus.bgf, statsscreen.bgf,
/// questsummary.bgf, kingsschedule.bgf, promises.bgf), named after the element they label.</para>
/// </remarks>
public static class Fable3ScreenModel
{
	/// <summary>One element's stated values: state, hidden (visibility toggle), text, timeline position.</summary>
	public readonly record struct Probe(int? State, bool Hidden, string? Text, float? Time);

	public const string HeroStatus = "HeroStatus";
	public const string Stats = "StatsBox";
	public const string Promises = "Promises";
	public const string Quests = "QuestSummary";
	public const string Schedule = "KingsSchedule";

	private const string Gap = "    ";

	/// <summary>The page of the screen whose element root is <paramref name="root"/>; null for an unknown root.</summary>
	public static Fable3ScreenPage? Compose(string root, IReadOnlyDictionary<string, Probe> stated, System.Func<string, string> resolve) => root switch
	{
		HeroStatus => HeroStatusPage(stated, resolve),
		Stats => StatsPage(stated, resolve),
		Promises => PromisesPage(stated, resolve),
		Quests => QuestsPage(stated, resolve),
		Schedule => SchedulePage(stated, resolve),
		_ => Fable3BoxScreenModel.Compose(root, stated, resolve),
	};

	/// <summary>The element root of a display_box_hooks.cpp `screen:*` kind, or null (Options draws its own object).</summary>
	public static string? RootOf(string kind) => kind switch
	{
		"screen:herostatus" => HeroStatus,
		"screen:promises" => Promises,
		"screen:stats" => Stats,
		"screen:quests" => Quests,
		"screen:schedule" => Schedule,
		_ => Fable3BoxScreenModel.RootOf(kind),
	};

	/// <summary><paramref name="filled"/> of <paramref name="of"/> pips.</summary>
	public static string Pips(int filled, int of)
	{
		int n = filled < 0 ? 0 : filled > of ? of : filled;
		return new string('■', n) + new string('□', of - n);
	}

	/// <summary>
	/// HERO STATUS. The Show sub_829758C8 writes three sections (0x829758EC..0x829758FC):
	/// sub_82975A88 states Strength / Will / Skill as the pip count `min(|v*6|, 6)` of the hero's
	/// component-15 floats +912 / +920 / +916 (0x82975D28 / 0x82975D4C / 0x82975D70, element state);
	/// sub_82975D88 states Morality (`(v-min)/(max-min)`, 0x82975F10), Attractiveness (0x8297608C)
	/// and Weight (+908, 0x829760A0) as meter fractions through sub_8281AD48 — timeline = fraction ×
	/// 1000; sub_82976358 / sub_82976790 state each weapon slot (Melee, Ranged, Spell, SpellCombined):
	/// its name as the `Titles.*` text and its level sub_826C7DF8 as the `Values.*` state
	/// (0x829768F4 / 0x82976920), or an empty name and the value hidden when the slot is empty
	/// (0x82976950 / 0x82976968). Only B closes it (sub_82975A58: action 75 sets +28, the Update
	/// closes, 0x82975998).
	/// </summary>
	private static Fable3ScreenPage HeroStatusPage(IReadOnlyDictionary<string, Probe> stated, System.Func<string, string> resolve)
	{
		var rows = new List<Fable3ScreenPage.Row>();
		foreach ((string name, string tag) in new[] { ("Strength", "TEXT_GUI_STRENGTH"), ("Will", "TEXT_GUI_WILL"), ("Skill", "TEXT_GUI_SKILL") })
		{
			if (At(stated, HeroStatus + ".Section1.Values." + name) is { State: { } pips })
			{
				rows.Add(new(name, resolve(tag) + Gap + Pips(pips, 6)));
			}
		}

		foreach ((string name, string tag) in new[] { ("Morality", "TEXT_GUI_MORALITY"), ("Attractiveness", "TEXT_GUI_ATTRACTIVENESS"), ("Weight", "TEXT_GUI_WEIGHT") })
		{
			if (At(stated, HeroStatus + ".Section2.Values." + name) is { Time: { } time })
			{
				rows.Add(new(name, resolve(tag) + Gap + Fable3OptionsModel.Slider(time / 1000f)));
			}
		}

		foreach ((string title, string value) in new[] { ("Melee", "Melee"), ("Ranged", "Ranged"), ("Spell", "Spells"), ("SpellCombined", "SpellCombined") })
		{
			Probe? name = At(stated, HeroStatus + ".Section3.Titles." + title);
			Probe? level = At(stated, HeroStatus + ".Section3.Values." + value);
			if (name is { Text: { Length: > 0 } weapon } && level is not { Hidden: true })
			{
				rows.Add(new(title, level is { State: > 0 } ? weapon + Gap + new string('■', level.Value.State!.Value) : weapon));
			}
		}

		return new Fable3ScreenPage(resolve("GUI_TEXT_HERO_STATUS_TITLE"), rows, -1, string.Empty,
			new[] { new Fable3ScreenPage.Prompt("B", resolve("GUI_BACK")) }, false);
	}

	/// <summary>
	/// STATS. The root's state is the page (sub_8296EC88 at 0x8296ECC8; the Show writes 2 at
	/// 0x8296D54C): 2 the categories, `…Categories.Details.CompStatisticCategories%d` rows, label
	/// `.Categories.Category01`, state 2 highlighted / 1 shown / 3 unused (sub_8296ED30,
	/// 0x8296EE34..0x8296EE54); 3 a category's statistics, `…FullStatistics.Details.
	/// CompStatisticDetails%d` rows with `Statistics.Title.Statistic` / `.Score` / `.Rank` texts,
	/// the highlighted row state 3 online or 6 offline, the others 1 or 5 (sub_8296EF78,
	/// 0x8296F584..0x8296F60C); 4 the friends' board, `…FriendsStatistics.Details.
	/// CompStatisticFriendsOnly%d` rows with `Line1.GamerTag` / `.Score` / `.Rank`, state 2
	/// highlighted / 1 shown / 3 unused (sub_829708B0, 0x82970C40..0x82970CC4). Input
	/// sub_8296DA10 by page: categories A (72) opens the statistics, B (73) closes (sub_8296DC00);
	/// statistics B back to the categories, A the friends' board (sub_8296DD90).
	/// </summary>
	private static Fable3ScreenPage StatsPage(IReadOnlyDictionary<string, Probe> stated, System.Func<string, string> resolve)
	{
		int page = At(stated, Stats)?.State ?? 2;
		var rows = new List<Fable3ScreenPage.Row>();
		int selected = -1;
		string title = Text(stated, Stats + ".Title") ?? resolve("TEXT_GUI_STATS_SCREEN_TITLE");
		string accept = Text(stated, Stats + ".Buttons.A_Button.Group.Text") ?? resolve("GUI_TEXT_MORE_DETAILS");
		var prompts = new List<Fable3ScreenPage.Prompt>();

		switch (page)
		{
			case 3:
				foreach ((int index, string path, Probe row) in Indexed(stated, Stats + ".Table.FullStatistics.Details.CompStatisticDetails"))
				{
					if (Text(stated, path + ".Statistics.Title.Statistic") is not { } statistic)
					{
						continue;
					}

					string line = statistic + Gap + (Text(stated, path + ".Statistics.Title.Score") ?? string.Empty);
					if (Text(stated, path + ".Statistics.Title.Rank") is { } rank)
					{
						line += Gap + "#" + rank;
					}

					if (row.State is 3 or 6)
					{
						selected = rows.Count;
					}

					rows.Add(new("Statistic" + index.ToString(CultureInfo.InvariantCulture), line));
				}

				prompts.Add(new("A", accept));
				break;
			case 4:
				foreach ((int index, string path, Probe row) in Indexed(stated, Stats + ".Table.FriendsStatistics.Details.CompStatisticFriendsOnly"))
				{
					if (row.State == 3 || Text(stated, path + ".Line1.GamerTag") is not { } gamer)
					{
						continue;
					}

					string line = gamer + Gap + (Text(stated, path + ".Line1.Score") ?? string.Empty);
					if (Text(stated, path + ".Line1.Rank") is { } rank)
					{
						line += Gap + "#" + rank;
					}

					if (row.State == 2)
					{
						selected = rows.Count;
					}

					rows.Add(new("Friend" + index.ToString(CultureInfo.InvariantCulture), line));
				}

				break;
			default:
				foreach ((int index, string path, Probe row) in Indexed(stated, Stats + ".Table.Categories.Details.CompStatisticCategories"))
				{
					if (row.State == 3 || Text(stated, path + ".Categories.Category01") is not { } category)
					{
						continue;
					}

					if (row.State == 2)
					{
						selected = rows.Count;
					}

					rows.Add(new("Category" + index.ToString(CultureInfo.InvariantCulture), category));
				}

				prompts.Add(new("A", accept));
				break;
		}

		prompts.Add(new("B", resolve("GUI_BACK")));
		return new Fable3ScreenPage(title, rows, selected, string.Empty, prompts, true);
	}

	/// <summary>
	/// PROMISES. The Show sub_827A3398 writes the player's gamertag (0x827A3458), the promise's
	/// text tag `+72` into `Promises.Promise.Promise` (0x827A3498), the button labels GUI_ACCEPT /
	/// GUI_BACK by name (0x827A34D0 / 0x827A3508), and `Promises.Buttons` state 1 when the promise
	/// is already signed (`+77`, with `Signature` 3) else 2 (0x827A3530..0x827A3564). Input
	/// sub_827A3600: A (74) while unsigned signs it (`Signature` 2, `Buttons` 1, message
	/// "PromiseAccepted", 0x827A36C0..0x827A3754); B (75) only once signed closes it (+76,
	/// 0x827A3688; the Update closes at 0x827A35C0).
	/// </summary>
	private static Fable3ScreenPage PromisesPage(IReadOnlyDictionary<string, Probe> stated, System.Func<string, string> resolve)
	{
		bool signed = At(stated, Promises + ".Buttons")?.State == 1;
		var rows = new List<Fable3ScreenPage.Row>();
		if (Text(stated, Promises + ".Player.GamerTag") is { } gamer)
		{
			rows.Add(new("Signature", signed ? gamer + Gap + "✓" : gamer));
		}

		var prompts = new List<Fable3ScreenPage.Prompt>();
		if (signed)
		{
			prompts.Add(new("B", Text(stated, Promises + ".Buttons.Buttons.B_Button.Group.Text") ?? resolve("GUI_BACK")));
		}
		else
		{
			prompts.Add(new("A", Text(stated, Promises + ".Buttons.Buttons.A_Button.Group.Text") ?? resolve("GUI_ACCEPT")));
		}

		return new Fable3ScreenPage(resolve("GUI_PROMISE"), rows, -1, Text(stated, Promises + ".Promise.Promise") ?? string.Empty, prompts, false);
	}

	/// <summary>
	/// QUEST SUMMARY. The Show sub_829739D8 writes the active quest's objective title and
	/// description (sub_82973AC8, 0x82973BB0 / 0x82973DC0) and a window of seven rows
	/// `QuestSummary.Details.QuestSummaryDetails%d` from the cursor `+44` (sub_82973E20): each row
	/// is shown through the visibility toggle, its `Text.QuestName` / `Text.QuestLocation` /
	/// `Text.GuildSealValue` texts committed, and its state 2 for the cursor row, 1 otherwise, 3 for
	/// an unused slot (sub_82973FB0, 0x82974164..0x82974378). Input sub_829745B0: the stick (26/27)
	/// moves `+44` (sub_829747A8), A (72) makes the row's quest the active one (message 156 with the
	/// FNV-1 of its name, 0x82974708) and closes, B (73) closes.
	/// </summary>
	private static Fable3ScreenPage QuestsPage(IReadOnlyDictionary<string, Probe> stated, System.Func<string, string> resolve)
	{
		var rows = new List<Fable3ScreenPage.Row>();
		int selected = -1;
		foreach ((int index, string path, Probe row) in Indexed(stated, Quests + ".Details.QuestSummaryDetails"))
		{
			if (row.State == 3 || row.Hidden || Text(stated, path + ".Text.QuestName") is not { } name)
			{
				continue;
			}

			string line = name;
			if (Text(stated, path + ".Text.QuestLocation") is { } location)
			{
				line += Gap + location;
			}

			if (Text(stated, path + ".Text.GuildSealValue") is { } seals)
			{
				line += Gap + seals;
			}

			if (row.State == 2)
			{
				selected = rows.Count;
			}

			rows.Add(new("Quest" + index.ToString(CultureInfo.InvariantCulture), line));
		}

		string objective = string.Join("\n", Present(Text(stated, Quests + ".ObjectiveTitle"), Text(stated, Quests + ".ObjectiveDecription")));
		return new Fable3ScreenPage(resolve("TEXT_GUI_QUEST_SUMMARY_TITLE"), rows, selected, objective,
			new[] { new Fable3ScreenPage.Prompt("A", resolve("GUI_SELECT")), new Fable3ScreenPage.Prompt("B", resolve("GUI_BACK")) }, true);
	}

	/// <summary>
	/// THE KING'S SCHEDULE. The Show sub_82972FD8 writes the title by tag (0x82973138), the day
	/// subtitle (0x829731B8, emptied with the top line hidden when `+28` is -1), and four rows
	/// (sub_82973328): `KingsSchedule.Tasks.Task%d` the task's text tag, `KingsSchedule.TickBoxes.
	/// TickBox%d` its state — 4 for a day with fewer tasks (0x82973268), 2 stamped (the stamp sound,
	/// 0x82973410). Input sub_82973428: A (74) from pad 1 closes it.
	/// </summary>
	private static Fable3ScreenPage SchedulePage(IReadOnlyDictionary<string, Probe> stated, System.Func<string, string> resolve)
	{
		var rows = new List<Fable3ScreenPage.Row>();
		for (int i = 1; i <= 4; ++i)
		{
			string n = i.ToString(CultureInfo.InvariantCulture);
			int? tick = At(stated, Schedule + ".TickBoxes.TickBox" + n)?.State;
			if (tick == 4 || Text(stated, Schedule + ".Tasks.Task" + n) is not { } task)
			{
				continue;
			}

			rows.Add(new("Task" + n, (tick == 2 ? "■ " : "□ ") + task));
		}

		return new Fable3ScreenPage(Text(stated, Schedule + ".Title") ?? resolve("TEXT_GUI_KINGS_SCHEDULE_TITLE"), rows, -1,
			Text(stated, Schedule + ".SubTitle") ?? string.Empty,
			new[] { new Fable3ScreenPage.Prompt("A", resolve("GUI_ACCEPT")) }, false);
	}

	private static Probe? At(IReadOnlyDictionary<string, Probe> stated, string path) =>
		stated.TryGetValue(path, out Probe probe) ? probe : null;

	private static string? Text(IReadOnlyDictionary<string, Probe> stated, string path) =>
		stated.TryGetValue(path, out Probe probe) && probe.Text is { Length: > 0 } text ? text : null;

	private static IEnumerable<string> Present(params string?[] texts)
	{
		foreach (string? text in texts)
		{
			if (text is { Length: > 0 })
			{
				yield return text;
			}
		}
	}

	/// <summary>The rows `<paramref name="prefix"/>%d` that were stated, in index order.</summary>
	public static List<(int Index, string Path, Probe Row)> Indexed(IReadOnlyDictionary<string, Probe> stated, string prefix)
	{
		var rows = new List<(int, string, Probe)>();
		foreach ((string path, Probe probe) in stated)
		{
			if (path.Length > prefix.Length && path.StartsWith(prefix, System.StringComparison.Ordinal) &&
				int.TryParse(path[prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out int index))
			{
				rows.Add((index, path, probe));
			}
		}

		rows.Sort((a, b) => a.Item1.CompareTo(b.Item1));
		return rows;
	}
}
