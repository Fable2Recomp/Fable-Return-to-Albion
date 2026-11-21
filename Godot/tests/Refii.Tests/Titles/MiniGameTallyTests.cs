using GdUnit4;
using Refii.Titles.Fable2;
using static GdUnit4.Assertions;

namespace Refii.Tests.Titles;

/// <summary>
/// Fable 2's minigame tally as its natives state it (<see cref="Engine_UiGuiWidgets"/>): rows count
/// up one GUI tick at a time in order, A snaps only rows below their target, and the tally closes
/// when screen 11 is seen leaving.
/// </summary>
[TestSuite]
public sealed class MiniGameTallyTests
{
	[TestCase]
	public void RowsCountUpInOrderOneTickAtATime()
	{
		var widgets = new Engine_UiGuiWidgets();
		widgets.TurnOnMiniGame("Title", "Continue");
		widgets.AddMiniGameAmount("Gold", 25);
		widgets.AddMiniGameAmount("Bonus", 3);

		widgets.StepMiniGame(1);
		AssertThat(widgets.MiniGame[0].Displayed).IsEqual(1);
		AssertThat(widgets.MiniGame[1].Displayed).IsEqual(0);

		// 1..9 by one, then by 19 from 10: 10 + 19 clamps onto 25.
		widgets.StepMiniGame(9);
		AssertThat(widgets.MiniGame[0].Displayed).IsEqual(10);
		widgets.StepMiniGame(1);
		AssertThat(widgets.MiniGame[0].Displayed).IsEqual(25);
		AssertThat(widgets.MiniGame[1].Displayed).IsEqual(0);

		widgets.StepMiniGame(100);
		AssertThat(widgets.MiniGame[1].Displayed).IsEqual(3);
	}

	[TestCase]
	public void ASnapsOnlyRowsBelowTheirTarget()
	{
		var widgets = new Engine_UiGuiWidgets();
		widgets.TurnOnMiniGame("Title", "Continue");
		widgets.AddMiniGameAmount("Gold", 500);
		widgets.AddMiniGameAmount("Fine", -40);

		AssertThat(widgets.ConfirmMiniGame()).IsTrue();
		AssertThat(widgets.MiniGame[0].Displayed).IsEqual(500);
		AssertThat(widgets.MiniGame[1].Displayed).IsEqual(0);
		AssertThat(widgets.MiniGameUp).IsTrue();

		AssertThat(widgets.ConfirmMiniGame()).IsFalse();
		AssertThat(widgets.MiniGameUp).IsTrue();
	}

	[TestCase]
	public void TheTallyClosesWhenItsScreenIsSeenLeaving()
	{
		var widgets = new Engine_UiGuiWidgets();
		widgets.TurnOnMiniGame("Title", "Continue");
		widgets.AddMiniGameAmount("Gold", 5);

		// Not yet seen up: a screen that has not switched yet is not a close.
		widgets.ObserveMiniGameScreen(false);
		AssertThat(widgets.MiniGameUp).IsTrue();

		widgets.ObserveMiniGameScreen(true);
		widgets.ObserveMiniGameScreen(false);
		AssertThat(widgets.MiniGameUp).IsFalse();
		AssertThat(widgets.MiniGame.Count).IsEqual(0);
	}
}
