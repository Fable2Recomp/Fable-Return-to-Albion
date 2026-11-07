namespace Refii.Tests.Titles;

using System.Collections.Generic;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Titles.Fable3;
using Probe = Refii.Titles.Fable3.Fable3ScreenModel.Probe;

/// <summary>
/// Fable 3's box-manager screens from a stated set: the texts, slides and meters their writers
/// state, and the cursor rows, rows and signature their own calls state (Fable3ScreenState) — the
/// hero status, stats, promises, quest summary and king's schedule classes (Fable3ScreenModel's
/// citations).
/// </summary>
[TestSuite]
public class Fable3ScreenModelTests
{
	private static string Resolve(string tag) => "<" + tag + ">";

	private static Probe State(int state) => new(state, false, null, null);

	private static Probe Text(string text) => new(null, false, text, null);

	private static readonly Fable3ScreenState None = new();

	[TestCase]
	public void HeroStatusStatesPipsMetersAndWeapons()
	{
		var stated = new Dictionary<string, Probe>
		{
			["HeroStatus.Section1.Values.Strength"] = State(4),
			["HeroStatus.Section1.Values.Will"] = State(9),
			["HeroStatus.Section2.Values.Morality"] = new(null, false, null, 750f),
			["HeroStatus.Section3.Titles.Melee"] = Text("Hammer"),
			["HeroStatus.Section3.Values.Melee"] = new(2, false, null, null),
			["HeroStatus.Section3.Titles.Ranged"] = Text(string.Empty),
			["HeroStatus.Section3.Values.Ranged"] = new(null, true, null, null),
		};

		Fable3ScreenPage page = Fable3ScreenModel.Compose(Fable3ScreenModel.HeroStatus, stated, None, Resolve)!.Value;
		AssertThat(page.Title).IsEqual("<GUI_TEXT_HERO_STATUS_TITLE>");
		AssertThat(page.Rows.Count).IsEqual(4);
		AssertThat(page.Rows[0].Line).IsEqual("<TEXT_GUI_STRENGTH>    ■■■■□□");
		AssertThat(page.Rows[1].Line).IsEqual("<TEXT_GUI_WILL>    ■■■■■■");
		AssertThat(page.Rows[2].Line).IsEqual("<TEXT_GUI_MORALITY>    " + Fable3OptionsModel.Slider(0.75f));
		AssertThat(page.Rows[3].Line).IsEqual("Hammer    ■■");
		AssertThat(page.Cursor).IsFalse();
		AssertThat(page.Prompts.Count).IsEqual(1);
		AssertThat(page.Prompts[0].Button).IsEqual("B");
	}

	[TestCase]
	public void StatsHighlightsTheStatedCursorInTheStatedWindow()
	{
		var stated = new Dictionary<string, Probe>
		{
			["StatsBox.Table.Categories.Details.CompStatisticCategories0.Categories.Category01"] = Text("Combat"),
			["StatsBox.Table.Categories.Details.CompStatisticCategories1.Categories.Category01"] = Text("Money"),
			["StatsBox.Table.Categories.Details.CompStatisticCategories2.Categories.Category01"] = Text("stale"),
		};
		var screen = new Fable3ScreenState { Kind = "stats", Fields = new[] { 2, 1, 0, 0 } };
		screen.StatsWindows[2] = (0, 2, 8);

		Fable3ScreenPage page = Fable3ScreenModel.Compose(Fable3ScreenModel.Stats, stated, screen, Resolve)!.Value;
		AssertThat(page.Rows.Count).IsEqual(2);
		AssertThat(page.Rows[0].Line).IsEqual("Combat");
		AssertThat(page.Selected).IsEqual(1);
		AssertThat(page.Title).IsEqual("<TEXT_GUI_STATS_SCREEN_TITLE>");

		stated["StatsBox.Table.FullStatistics.Details.CompStatisticDetails0.Statistics.Title.Statistic"] = Text("Gold earned");
		stated["StatsBox.Table.FullStatistics.Details.CompStatisticDetails0.Statistics.Title.Score"] = Text("120");
		stated["StatsBox.Table.FullStatistics.Details.CompStatisticDetails1.Statistics.Title.Statistic"] = Text("Gold spent");
		screen.StatsPage = 3;
		screen.Fields = new[] { 3, 7, 1, 0 };
		screen.StatsWindows[3] = (6, 12, 6);
		page = Fable3ScreenModel.Compose(Fable3ScreenModel.Stats, stated, screen, Resolve)!.Value;
		AssertThat(page.Rows.Count).IsEqual(2);
		AssertThat(page.Rows[0].Line).IsEqual("Gold earned    120");
		AssertThat(page.Selected).IsEqual(1);
	}

	[TestCase]
	public void QuestRowsAreTheWritersCallsUpToTheLast()
	{
		var stated = new Dictionary<string, Probe>
		{
			["QuestSummary.Details.QuestSummaryDetails0.Text.QuestName"] = Text("The Gathering"),
			["QuestSummary.Details.QuestSummaryDetails0.Text.QuestLocation"] = Text("Bowerstone"),
			["QuestSummary.Details.QuestSummaryDetails1.Text.QuestName"] = Text("Rebel"),
			["QuestSummary.Details.QuestSummaryDetails2.Text.QuestName"] = Text("stale"),
			["QuestSummary.ObjectiveTitle"] = Text("Find Walter"),
		};
		var screen = new Fable3ScreenState();
		screen.QuestRows[0] = (0x1000u, false, false);
		screen.QuestRows[1] = (0x2000u, true, true);
		screen.QuestRows[2] = (0x3000u, false, true);

		Fable3ScreenPage page = Fable3ScreenModel.Compose(Fable3ScreenModel.Quests, stated, screen, Resolve)!.Value;
		AssertThat(page.Rows.Count).IsEqual(2);
		AssertThat(page.Rows[0].Line).IsEqual("The Gathering    Bowerstone");
		AssertThat(page.Selected).IsEqual(1);
		AssertThat(page.Description).IsEqual("Find Walter");
		AssertThat(page.Cursor).IsTrue();
	}

	[TestCase]
	public void ScheduleTicksAreTheRowCallsAndDropAbsentDays()
	{
		var stated = new Dictionary<string, Probe>
		{
			["KingsSchedule.Tasks.Task1"] = Text("Hold court"),
			["KingsSchedule.Tasks.Task2"] = Text("Feast"),
			["KingsSchedule.Tasks.Task3"] = Text("old"),
		};
		var screen = new Fable3ScreenState();
		screen.ScheduleRows[1] = (2, 0x11u);
		screen.ScheduleRows[2] = (1, 0x22u);
		screen.ScheduleRows[3] = (4, 0u);

		Fable3ScreenPage page = Fable3ScreenModel.Compose(Fable3ScreenModel.Schedule, stated, screen, Resolve)!.Value;
		AssertThat(page.Rows.Count).IsEqual(2);
		AssertThat(page.Rows[0].Line).IsEqual("■ Hold court");
		AssertThat(page.Rows[1].Line).IsEqual("□ Feast");
	}

	[TestCase]
	public void PromiseOffersAUntilSignedThenB()
	{
		var stated = new Dictionary<string, Probe>
		{
			["Promises.Player.GamerTag"] = Text("Hero"),
			["Promises.Promise.Promise"] = Text("I promise."),
			["Promises.Buttons.Buttons.A_Button.Group.Text"] = Text("Accept"),
		};
		var screen = new Fable3ScreenState { Promise = (0x1234u, false) };

		Fable3ScreenPage page = Fable3ScreenModel.Compose(Fable3ScreenModel.Promises, stated, screen, Resolve)!.Value;
		AssertThat(page.Prompts[0].Button).IsEqual("A");
		AssertThat(page.Prompts[0].Label).IsEqual("Accept");
		AssertThat(page.Description).IsEqual("I promise.");

		screen.Fields = new[] { 1 };
		page = Fable3ScreenModel.Compose(Fable3ScreenModel.Promises, stated, screen, Resolve)!.Value;
		AssertThat(page.Prompts[0].Button).IsEqual("B");
		AssertThat(page.Rows[0].Line).IsEqual("Hero    ✓");
	}

	[TestCase]
	public void UnknownRootHasNoPage()
	{
		AssertThat(Fable3ScreenModel.Compose("Options", new Dictionary<string, Probe>(), None, Resolve) is null).IsTrue();
	}
}
