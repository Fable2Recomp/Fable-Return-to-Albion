namespace Refii.Tests.Titles;

using System.Collections.Generic;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Titles.Fable3;
using Refii.Ui;

/// <summary>
/// Godot's answers to Fable 3's boxes, menu box and Options screen (Fable3BoxAnswers), and the
/// Options screen's stated state (Fable3OptionsState): the tick action each press becomes, the
/// menu box's wrap, and the statement's words joined to the stated levels.
/// </summary>
[TestSuite]
public class Fable3BoxAnswersTests
{
	private static readonly Fable3BoxAnswers.Press A = new(true, false, false, false, false, false, false, false);
	private static readonly Fable3BoxAnswers.Press B = new(false, true, false, false, false, false, false, false);
	private static readonly Fable3BoxAnswers.Press X = new(false, false, true, false, false, false, false, false);
	private static readonly Fable3BoxAnswers.Press Up = new(false, false, false, false, true, false, false, false);

	[TestCase]
	public void QuestionsAnswerYesOnAAndNoOnB()
	{
		AssertThat(Fable3BoxAnswers.ForBox("question", 0x8A001234u, A)).IsEqual("box:8A001234:yes");
		AssertThat(Fable3BoxAnswers.ForBox("question", 0x8A001234u, B)).IsEqual("box:8A001234:no");
		AssertThat(Fable3BoxAnswers.ForBox("question", 0x8A001234u, X)).IsNull();
	}

	[TestCase]
	public void TheAdjusterAnswersWithGodotsValue()
	{
		AssertThat(Fable3BoxAnswers.ForBox("adjuster", 0x10u, A, adjusterValue: 250)).IsEqual("box:00000010:accept:250");
		AssertThat(Fable3BoxAnswers.ForBox("adjuster", 0x10u, B, adjusterValue: 250)).IsEqual("box:00000010:cancel");
		AssertThat(Fable3BoxAnswers.ForBox("adjuster", 0x10u, Up)).IsNull();
	}

	[TestCase]
	public void MessagesDismissAndTheDownloadFormTakesX()
	{
		AssertThat(Fable3BoxAnswers.ForBox("message", 0x20u, A)).IsEqual("box:00000020:dismiss");
		AssertThat(Fable3BoxAnswers.ForBox("message", 0x20u, B)).IsEqual("box:00000020:dismiss");
		AssertThat(Fable3BoxAnswers.ForBox("message", 0x20u, X)).IsNull();
		AssertThat(Fable3BoxAnswers.ForBox("message", 0x20u, X, download: true)).IsEqual("box:00000020:action");
		AssertThat(Fable3BoxAnswers.ForBox("modal", 0x30u, A)).IsEqual("box:00000030:dismiss");
		AssertThat(Fable3BoxAnswers.ForBox("modal", 0x30u, B)).IsNull();
		AssertThat(Fable3BoxAnswers.ForBox("modal", 0x30u, X, download: true)).IsEqual("box:00000030:action");
		AssertThat(Fable3BoxAnswers.ForBox("notice", 0x40u, B)).IsNull();
		AssertThat(Fable3BoxAnswers.ForBox("notice", 0x40u, A)).IsEqual("box:00000040:dismiss");
	}

	[TestCase]
	public void OnlyTheAnsweredKindsAreGodots()
	{
		foreach (string kind in new[] { "message", "question", "adjuster", "quest", "notice", "modal" })
		{
			AssertThat(Fable3BoxAnswers.Owns(kind)).IsTrue();
		}

		foreach (string kind in new[] { "card", "buttons", "intermission", "display", "screen:options", "unknown" })
		{
			AssertThat(Fable3BoxAnswers.Owns(kind)).IsFalse();
			AssertThat(Fable3BoxAnswers.ForBox(kind, 1u, A)).IsNull();
		}
	}

	[TestCase]
	public void TheMenuBoxWrapsAsTheGuestsHandlerDoes()
	{
		AssertThat(Fable3BoxAnswers.MenuStep(0, 4, down: false)).IsEqual(3);
		AssertThat(Fable3BoxAnswers.MenuStep(3, 4, down: true)).IsEqual(0);
		AssertThat(Fable3BoxAnswers.MenuStep(1, 4, down: true)).IsEqual(2);
		AssertThat(Fable3BoxAnswers.MenuStep(0, 0, down: true)).IsEqual(0);
		AssertThat(Fable3BoxAnswers.MenuPick(2)).IsEqual("menubox:2");
		AssertThat(Fable3BoxAnswers.MenuCancel).IsEqual("menubox-cancel");
	}

	[TestCase]
	public void OptionsPressesAreTheHandlersActions()
	{
		AssertThat(Fable3BoxAnswers.OptionsAction(A)).IsEqual(Fable3BoxAnswers.ActionA);
		AssertThat(Fable3BoxAnswers.OptionsAction(B)).IsEqual(75);
		AssertThat(Fable3BoxAnswers.OptionsAction(Up)).IsEqual(29);
		AssertThat(Fable3BoxAnswers.OptionsAction(new Fable3BoxAnswers.Press(false, false, false, false, false, false, false, true))).IsEqual(32);
		AssertThat(Fable3BoxAnswers.OptionsAction(X)).IsNull();
		AssertThat(Fable3BoxAnswers.Options(31)).IsEqual("options:31");
	}

	private static Engine_UiModel.GuiNativeCall State(params float[] fields)
	{
		var arguments = new List<Engine_UiModel.GuiNativeArgument>
		{
			new(Engine_UiModel.GuiNativeArgumentKind.String, "8B0010A0"),
		};
		foreach (float field in fields)
		{
			arguments.Add(new Engine_UiModel.GuiNativeArgument(Engine_UiModel.GuiNativeArgumentKind.Number,
				field.ToString(System.Globalization.CultureInfo.InvariantCulture)));
		}

		return new Engine_UiModel.GuiNativeCall("@Options.State", arguments);
	}

	[TestCase]
	public void TheOptionsStateJoinsTheStatedLevels()
	{
		// page 3, row 4, invert on, subtitles off, tutorial on, breadcrumb 3, speaker 0 (the box's),
		// language 1 of 4, flags 0/0, from the pause, in game, save allowed, no query, unchanged.
		uint[]? words = Fable3OptionsState.Parse(State(3, 4, 1, 0, 1, 3, 0, 1, 4, 0, 0, 1, 0, 1, 0, 0));
		AssertThat(words).IsNotNull();
		AssertThat(words![0]).IsEqual(0x8B0010A0u);
		AssertThat(words[Fable3OptionsState.QueryWord]).IsEqual(0u);

		Fable3OptionsModel.Snapshot snapshot = Fable3OptionsState.Snapshot(words, 0.25f, 0.5f, 0.75f, 1.0f, 2);
		AssertThat(snapshot.Page).IsEqual(Fable3OptionsModel.PageGameSettings);
		AssertThat(snapshot.SelectedRow).IsEqual(4);
		AssertThat(snapshot.Controller).IsTrue();
		AssertThat(snapshot.Subtitles).IsFalse();
		AssertThat(snapshot.Breadcrumb).IsEqual(3);
		// The applier's speaker mode wins over the screen's field; the levels are the appliers'.
		AssertThat(snapshot.Speaker).IsEqual(2);
		AssertThat(snapshot.Gamma).IsEqual(0.25f);
		AssertThat(snapshot.Music).IsEqual(0.5f);
		AssertThat(snapshot.Sfx).IsEqual(0.75f);
		AssertThat(snapshot.Voice).IsEqual(1.0f);
		AssertThat(snapshot.CanSave).IsTrue();

		// Unknown speaker: the screen's own field stands.
		AssertThat(Fable3OptionsState.Snapshot(words, 0.5f, 1f, 1f, 0.9f, -1).Speaker).IsEqual(0);

		// A short statement is no state.
		AssertThat(Fable3OptionsState.Parse(State(3, 4))).IsNull();
	}
}
