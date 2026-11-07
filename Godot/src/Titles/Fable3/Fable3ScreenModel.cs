namespace Refii.Titles.Fable3;

using System.Collections.Generic;
using System.Globalization;

/// <summary>
/// The pages of Fable 3's element-written box-manager screens — hero status, stats, promises,
/// quest summary, king's schedule — from what the guest STATES: the texts, slides and meters its
/// writers pass at their entry (ui_stated_hooks.cpp: the path setters, the text funnels, the
/// visibility toggle) and the screens' own calls (<see cref="Fable3ScreenState"/>: the rows the
/// quest summary and the king's schedule write, the promise's signature). Pure, so the tests can run
/// it against a stated set. Every path, value and route below is the screen
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
	public static Fable3ScreenPage? Compose(string root, IReadOnlyDictionary<string, Probe> stated, Fable3ScreenState screen,
		System.Func<string, string> resolve) => root switch
	{
		HeroStatus => HeroStatusPage(stated, resolve),
		Stats => StatsPage(stated, screen, resolve),
		Promises => PromisesPage(stated, screen, resolve),
		Quests => QuestsPage(stated, screen, resolve),
		Schedule => SchedulePage(stated, screen, resolve),
		_ => Fable3BoxScreenModel.Compose(root, stated, screen, resolve),
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
	/// STATS. Fields (page `+52`, cursor `+68`, category `+72`, friend `+76`); the page moves through
	/// sub_8296EC88 (`@Screen.StatsPage`; the Show slides the root to 2 at 0x8296D54C). Each page's
	/// writer decides its window (`@Screen.StatsWindow`: first row, count, rows) and highlights the
	/// row where `start + row == cursor`: 2 the categories, `…Categories.Details.
	/// CompStatisticCategories%d` labelled `.Categories.Category01` (sub_8296ED30, 0x8296EE30); 3 a
	/// category's statistics, `…FullStatistics.Details.CompStatisticDetails%d` with
	/// `Statistics.Title.Statistic` / `.Score` / `.Rank` (sub_8296EF78, 0x8296F50C; six rows when
	/// online, nine offline); 4 the friends' board, `…FriendsStatistics.Details.
	/// CompStatisticFriendsOnly%d` with `Line1.GamerTag` / `.Score` / `.Rank` (sub_829708B0,
	/// 0x82970C50). Rows past the list are its unused slides and are not drawn. Input sub_8296DA10 by
	/// page: categories A (72) opens the statistics, B (73) closes (sub_8296DC00); statistics B back to
	/// the categories, A the friends' board (sub_8296DD90).
	/// </summary>
	private static Fable3ScreenPage StatsPage(IReadOnlyDictionary<string, Probe> stated, Fable3ScreenState screen, System.Func<string, string> resolve)
	{
		int page = screen.StatsPage ?? (screen.Field(0, 2) is 3 or 4 ? screen.Field(0, 2) : 2);
		int cursor = screen.Field(1, 0);
		var rows = new List<Fable3ScreenPage.Row>();
		int selected = -1;
		string title = Text(stated, Stats + ".Title") ?? resolve("TEXT_GUI_STATS_SCREEN_TITLE");
		string accept = Text(stated, Stats + ".Buttons.A_Button.Group.Text") ?? resolve("GUI_TEXT_MORE_DETAILS");
		var prompts = new List<Fable3ScreenPage.Prompt>();
		(int start, int count, int shown) = screen.StatsWindows.TryGetValue(page, out (int, int, int) window)
			? window : (0, int.MaxValue, page == 3 ? 9 : page == 4 ? 9 : 8);

		for (int row = 0; row < shown && start + row < count; ++row)
		{
			string n = row.ToString(CultureInfo.InvariantCulture);
			string? line = page switch
			{
				3 => Line(Stats + ".Table.FullStatistics.Details.CompStatisticDetails" + n + ".Statistics.Title.", "Statistic"),
				4 => Line(Stats + ".Table.FriendsStatistics.Details.CompStatisticFriendsOnly" + n + ".Line1.", "GamerTag"),
				_ => Text(stated, Stats + ".Table.Categories.Details.CompStatisticCategories" + n + ".Categories.Category01"),
			};

			if (line is null)
			{
				continue;
			}

			if (start + row == cursor)
			{
				selected = rows.Count;
			}

			rows.Add(new((page switch { 3 => "Statistic", 4 => "Friend", _ => "Category" }) + n, line));
		}

		if (page != 4)
		{
			prompts.Add(new("A", accept));
		}

		prompts.Add(new("B", resolve("GUI_BACK")));
		return new Fable3ScreenPage(title, rows, selected, string.Empty, prompts, true);

		string? Line(string path, string name)
		{
			if (Text(stated, path + name) is not { } label)
			{
				return null;
			}

			string line = label + Gap + (Text(stated, path + "Score") ?? string.Empty);
			return Text(stated, path + "Rank") is { } rank ? line + Gap + "#" + rank : line;
		}
	}

	/// <summary>
	/// PROMISES. The Show sub_827A3398 writes the player's gamertag (0x827A3458), the promise's
	/// text tag `+72` into `Promises.Promise.Promise` (0x827A3498), the button labels GUI_ACCEPT /
	/// GUI_BACK by name (0x827A34D0 / 0x827A3508), and slides `Promises.Buttons` 1 when the promise
	/// is already signed (`+77`, with `Signature` 3) else 2 (0x827A350C..0x827A3564). The signature
	/// is the field `+77`: the ctor's accepted argument (sub_827A3290, 0x827A3300), set by A while
	/// unsigned (sub_827A3600, 0x827A36EC, message "PromiseAccepted"); B only once signed closes it
	/// (+76, 0x827A3688; the Update closes at 0x827A35C0).
	/// </summary>
	private static Fable3ScreenPage PromisesPage(IReadOnlyDictionary<string, Probe> stated, Fable3ScreenState screen, System.Func<string, string> resolve)
	{
		bool signed = screen.Field(0, screen.Promise is { Accepted: true } ? 1 : 0) != 0;
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
	/// description (sub_82973AC8, 0x82973BB0 / 0x82973DC0) and sub_82973E20 a window of seven rows
	/// `QuestSummary.Details.QuestSummaryDetails%d` from the cursor `+44` (start `+44 - 6` from 7 on,
	/// 0x82973E3C): each listed quest through sub_82973FB0(box, row, quest, selected, isLast)
	/// (0x82973E9C) — the row shown (0x82974164), its `Text.QuestName` / `Text.QuestLocation` /
	/// `Text.GuildSealValue` committed — and the rest slid to 3, unused (0x82973F74). The rows are
	/// those calls (<see cref="Fable3ScreenState.QuestRows"/>), up to the one stated last. Input
	/// sub_829745B0: the stick (26/27) moves `+44` (sub_829747A8), A (72) makes the row's quest the
	/// active one (message 156 with the FNV-1 of its name, 0x82974708) and closes, B (73) closes.
	/// </summary>
	private static Fable3ScreenPage QuestsPage(IReadOnlyDictionary<string, Probe> stated, Fable3ScreenState screen, System.Func<string, string> resolve)
	{
		var rows = new List<Fable3ScreenPage.Row>();
		int selected = -1;
		foreach ((int index, (uint quest, bool isSelected, bool last)) in screen.QuestRows)
		{
			string path = Quests + ".Details.QuestSummaryDetails" + index.ToString(CultureInfo.InvariantCulture);
			if (quest != 0u && Text(stated, path + ".Text.QuestName") is { } name)
			{
				string line = name;
				if (Text(stated, path + ".Text.QuestLocation") is { } location)
				{
					line += Gap + location;
				}

				if (Text(stated, path + ".Text.GuildSealValue") is { } seals)
				{
					line += Gap + seals;
				}

				if (isSelected)
				{
					selected = rows.Count;
				}

				rows.Add(new("Quest" + index.ToString(CultureInfo.InvariantCulture), line));
			}

			if (last)
			{
				break;
			}
		}

		string objective = string.Join("\n", Present(Text(stated, Quests + ".ObjectiveTitle"), Text(stated, Quests + ".ObjectiveDecription")));
		return new Fable3ScreenPage(resolve("TEXT_GUI_QUEST_SUMMARY_TITLE"), rows, selected, objective,
			new[] { new Fable3ScreenPage.Prompt("A", resolve("GUI_SELECT")), new Fable3ScreenPage.Prompt("B", resolve("GUI_BACK")) }, true);
	}

	/// <summary>
	/// THE KING'S SCHEDULE. The Show sub_82972FD8 writes the title by tag (0x82973138), the day
	/// subtitle (0x829731B8, emptied with the top line hidden when `+28` is -1), and calls
	/// sub_82973328(box, row 1..4, tick, tag) per row (0x82973270): the day's task tag on
	/// `KingsSchedule.Tasks.Task%d` and its tick slide on `TickBoxes.TickBox%d` — 4 and tag 0 past the
	/// day's tasks (0x82973264 / 0x82973268), 2 stamped (the stamp sound, 0x82973410). The rows are
	/// those calls (<see cref="Fable3ScreenState.ScheduleRows"/>). Input sub_82973428: A (74) from pad
	/// 1 closes it.
	/// </summary>
	private static Fable3ScreenPage SchedulePage(IReadOnlyDictionary<string, Probe> stated, Fable3ScreenState screen, System.Func<string, string> resolve)
	{
		var rows = new List<Fable3ScreenPage.Row>();
		foreach ((int row, (int tick, uint _)) in screen.ScheduleRows)
		{
			string n = row.ToString(CultureInfo.InvariantCulture);
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
