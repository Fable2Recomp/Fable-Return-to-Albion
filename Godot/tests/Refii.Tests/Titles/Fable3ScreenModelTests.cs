namespace Refii.Tests.Titles;

using System.Collections.Generic;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Titles.Fable3;
using Probe = Refii.Titles.Fable3.Fable3ScreenModel.Probe;

/// <summary>
/// Fable 3's element-written screens from a stated set: the paths, value kinds, cursor states
/// and button routes of the hero status, stats, promises, quest summary and king's schedule
/// classes (Fable3ScreenModel's citations).
/// </summary>
[TestSuite]
public class Fable3ScreenModelTests
{
	private static string Resolve(string tag) => "<" + tag + ">";

	private static Probe State(int state) => new(state, false, null, null);

	private static Probe Text(string text) => new(null, false, text, null);

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

		Fable3ScreenPage page = Fable3ScreenModel.Compose(Fable3ScreenModel.HeroStatus, stated, Resolve)!.Value;
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
	public void StatsFollowsTheRootStateAndTheHighlightedRow()
	{
		var stated = new Dictionary<string, Probe>
		{
			["StatsBox"] = State(2),
			["StatsBox.Table.Categories.Details.CompStatisticCategories1"] = State(2),
			["StatsBox.Table.Categories.Details.CompStatisticCategories1.Categories.Category01"] = Text("Money"),
			["StatsBox.Table.Categories.Details.CompStatisticCategories0"] = State(1),
			["StatsBox.Table.Categories.Details.CompStatisticCategories0.Categories.Category01"] = Text("Combat"),
			["StatsBox.Table.Categories.Details.CompStatisticCategories2"] = State(3),
			["StatsBox.Table.Categories.Details.CompStatisticCategories2.Categories.Category01"] = Text("stale"),
		};

		Fable3ScreenPage page = Fable3ScreenModel.Compose(Fable3ScreenModel.Stats, stated, Resolve)!.Value;
		AssertThat(page.Rows.Count).IsEqual(2);
		AssertThat(page.Rows[0].Line).IsEqual("Combat");
		AssertThat(page.Selected).IsEqual(1);
		AssertThat(page.Title).IsEqual("<TEXT_GUI_STATS_SCREEN_TITLE>");

		stated["StatsBox"] = State(3);
		stated["StatsBox.Table.FullStatistics.Details.CompStatisticDetails0"] = State(3);
		stated["StatsBox.Table.FullStatistics.Details.CompStatisticDetails0.Statistics.Title.Statistic"] = Text("Gold earned");
		stated["StatsBox.Table.FullStatistics.Details.CompStatisticDetails0.Statistics.Title.Score"] = Text("120");
		page = Fable3ScreenModel.Compose(Fable3ScreenModel.Stats, stated, Resolve)!.Value;
		AssertThat(page.Rows.Count).IsEqual(1);
		AssertThat(page.Rows[0].Line).IsEqual("Gold earned    120");
		AssertThat(page.Selected).IsEqual(0);
	}

	[TestCase]
	public void QuestRowsSkipUnusedSlotsAndCarryTheObjective()
	{
		var stated = new Dictionary<string, Probe>
		{
			["QuestSummary.Details.QuestSummaryDetails0"] = State(1),
			["QuestSummary.Details.QuestSummaryDetails0.Text.QuestName"] = Text("The Gathering"),
			["QuestSummary.Details.QuestSummaryDetails0.Text.QuestLocation"] = Text("Bowerstone"),
			["QuestSummary.Details.QuestSummaryDetails1"] = State(2),
			["QuestSummary.Details.QuestSummaryDetails1.Text.QuestName"] = Text("Rebel"),
			["QuestSummary.Details.QuestSummaryDetails2"] = State(3),
			["QuestSummary.ObjectiveTitle"] = Text("Find Walter"),
		};

		Fable3ScreenPage page = Fable3ScreenModel.Compose(Fable3ScreenModel.Quests, stated, Resolve)!.Value;
		AssertThat(page.Rows.Count).IsEqual(2);
		AssertThat(page.Rows[0].Line).IsEqual("The Gathering    Bowerstone");
		AssertThat(page.Selected).IsEqual(1);
		AssertThat(page.Description).IsEqual("Find Walter");
		AssertThat(page.Cursor).IsTrue();
	}

	[TestCase]
	public void ScheduleTicksAndDropsAbsentDays()
	{
		var stated = new Dictionary<string, Probe>
		{
			["KingsSchedule.Tasks.Task1"] = Text("Hold court"),
			["KingsSchedule.TickBoxes.TickBox1"] = State(2),
			["KingsSchedule.Tasks.Task2"] = Text("Feast"),
			["KingsSchedule.TickBoxes.TickBox2"] = State(1),
			["KingsSchedule.Tasks.Task3"] = Text("old"),
			["KingsSchedule.TickBoxes.TickBox3"] = State(4),
		};

		Fable3ScreenPage page = Fable3ScreenModel.Compose(Fable3ScreenModel.Schedule, stated, Resolve)!.Value;
		AssertThat(page.Rows.Count).IsEqual(2);
		AssertThat(page.Rows[0].Line).IsEqual("■ Hold court");
		AssertThat(page.Rows[1].Line).IsEqual("□ Feast");
	}

	[TestCase]
	public void PromiseOffersAUntilSignedThenB()
	{
		var stated = new Dictionary<string, Probe>
		{
			["Promises.Buttons"] = State(2),
			["Promises.Player.GamerTag"] = Text("Hero"),
			["Promises.Promise.Promise"] = Text("I promise."),
			["Promises.Buttons.Buttons.A_Button.Group.Text"] = Text("Accept"),
		};

		Fable3ScreenPage page = Fable3ScreenModel.Compose(Fable3ScreenModel.Promises, stated, Resolve)!.Value;
		AssertThat(page.Prompts[0].Button).IsEqual("A");
		AssertThat(page.Prompts[0].Label).IsEqual("Accept");
		AssertThat(page.Description).IsEqual("I promise.");

		stated["Promises.Buttons"] = State(1);
		page = Fable3ScreenModel.Compose(Fable3ScreenModel.Promises, stated, Resolve)!.Value;
		AssertThat(page.Prompts[0].Button).IsEqual("B");
		AssertThat(page.Rows[0].Line).IsEqual("Hero    ✓");
	}

	[TestCase]
	public void UnknownRootHasNoPage()
	{
		AssertThat(Fable3ScreenModel.Compose("Options", new Dictionary<string, Probe>(), Resolve) is null).IsTrue();
	}
}
