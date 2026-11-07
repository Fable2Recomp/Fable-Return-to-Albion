namespace Refii.Tests.Titles;

using System.Collections.Generic;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Titles.Fable3;
using Refii.Ui;

/// <summary>
/// Fable 3's HUD drawn from stated calls: the objective list's rows from the add / mark / remove /
/// clear model at each redraw (sub_82953E68) with the guest's own slide writes dropped, and the
/// colour setter's statement (sub_828190E8) landing on the element's colour channel. No Godot
/// type is built.
/// </summary>
[TestSuite]
public class Fable3HudCutTests
{
	private static readonly List<string> Logged = new();

	private static Engine_UiModel.GuiNativeArgument S(string text) => new(Engine_UiModel.GuiNativeArgumentKind.String, text);

	private static Engine_UiModel.GuiNativeArgument N(float value) =>
		new(Engine_UiModel.GuiNativeArgumentKind.Number, value.ToString(System.Globalization.CultureInfo.InvariantCulture));

	private static Engine_UiModel.GuiNativeArgument B(bool value) => new(Engine_UiModel.GuiNativeArgumentKind.Boolean, value ? "1" : "0");

	private static Engine_UiModel.GuiNativeCall Call(string name, params Engine_UiModel.GuiNativeArgument[] arguments) => new(name, arguments);

	private static void Fresh()
	{
		Logged.Clear();
		Fable3Elements.Log = Logged.Add;
		Fable3Elements.Clear();
		Fable3UiStatements.Clear();
		Fable3HudRegistry.Clear();
		Fable3Elements.Take(Call("@Presentation.Load", S("40001000"), S("HUD.bgf")));
	}

	private static Fable3Elements.Element? Hud(string path) => Fable3Elements.Get(Fable3Elements.HudDocument, path);

	/// <summary>
	/// The redraw draws the rows the model holds: slide count + 1, each row's tag by name on
	/// `Bar&lt;i&gt;.Text` and its tick on done + 1. A remove is drawn only by the next redraw; an
	/// empty list and the hide rest the list on slide 1. The guest's own `@Element.State` on the
	/// list is dropped.
	/// </summary>
	[TestCase]
	public void TheObjectiveListIsDrawnFromItsStatedRows()
	{
		Fresh();
		Fable3UiStatements.Take(Call("@Hud.Objective.Add", S("QUEST_A"), B(false)));
		Fable3UiStatements.Take(Call("@Hud.Objective.Add", S("QUEST_B"), B(true)));
		Fable3UiStatements.Take(Call("@Hud.Objective.Redraw"));

		AssertThat(Hud(Fable3UiStatements.ObjectiveListPath)!.State).IsEqual(3);
		AssertThat(Hud(Fable3UiStatements.ObjectiveTextPath(1))!.Stated!.Value.TagName).IsEqual("QUEST_A");
		AssertThat(Hud(Fable3UiStatements.ObjectiveTextPath(2))!.Stated!.Value.TagName).IsEqual("QUEST_B");
		AssertThat(Hud(Fable3UiStatements.ObjectiveTickPath(1))!.State).IsEqual(1);
		AssertThat(Hud(Fable3UiStatements.ObjectiveTickPath(2))!.State).IsEqual(2);

		// The guest's own slide report on the owned list is not taken.
		Fable3Elements.Take(Call("@Element.State", S("=hud"), S("47000010"), S(Fable3UiStatements.ObjectiveListPath), N(6f)));
		AssertThat(Hud(Fable3UiStatements.ObjectiveListPath)!.State).IsEqual(3);

		// A remove changes the model; the screen keeps the rows until the next redraw.
		Fable3UiStatements.Take(Call("RemoveSubObjective", S("QUEST_A")));
		AssertThat(Hud(Fable3UiStatements.ObjectiveListPath)!.State).IsEqual(3);
		Fable3UiStatements.Take(Call("@Hud.Objective.Mark", S("QUEST_B"), B(false)));
		Fable3UiStatements.Take(Call("@Hud.Objective.Redraw"));
		AssertThat(Hud(Fable3UiStatements.ObjectiveListPath)!.State).IsEqual(2);
		AssertThat(Hud(Fable3UiStatements.ObjectiveTextPath(1))!.Stated!.Value.TagName).IsEqual("QUEST_B");
		AssertThat(Hud(Fable3UiStatements.ObjectiveTickPath(1))!.State).IsEqual(1);

		Fable3UiStatements.Take(Call("HideSubObjectives"));
		AssertThat(Hud(Fable3UiStatements.ObjectiveListPath)!.State).IsEqual(1);

		Fable3UiStatements.Take(Call("ClearSubObjectives"));
		Fable3UiStatements.Take(Call("@Hud.Objective.Redraw"));
		AssertThat(Hud(Fable3UiStatements.ObjectiveListPath)!.State).IsEqual(1);
	}

	/// <summary>
	/// `@Ui.Colour(pres, elem, path, r, g, b, a, via)` — the money gain's green (byte_832A5058:
	/// B 0xA7, G 0xE0, R 0xB1) — lands on the element and joins its ordinal's colour channel.
	/// </summary>
	[TestCase]
	public void AStatedColourLandsOnItsElement()
	{
		Fresh();
		const string Text = "MoneyAlert.MainPlayer.Group.Text1";
		Fable3Elements.Take(Call("@Ui.Colour", S("=hud"), S("47000020"), S(Text), N(177f), N(224f), N(167f), N(255f), S("828190E8")));
		(float R, float G, float B, float A) colour = Hud(Text)!.Colour!.Value;
		AssertThat(colour.R).IsEqualApprox(177f / 255f, 1e-6f);
		AssertThat(colour.G).IsEqualApprox(224f / 255f, 1e-6f);
		AssertThat(colour.B).IsEqualApprox(167f / 255f, 1e-6f);
		AssertThat(colour.A).IsEqual(1f);

		string[] paths = { string.Empty, "Layer", "Layer.MoneyAlert", "Layer." + Text };
		var statements = new Fable3HudStatements(paths, (_, _) => System.Array.Empty<int>(), System.Array.Empty<int>());
		var colours = new (float R, float G, float B, float A)?[paths.Length];
		statements.Apply(
			new[] { new Fable3HudStatements.Statement(Text, null, null, null, null, 1u, Colour: colour) },
			new int[paths.Length], new float?[paths.Length], new string?[paths.Length], new float?[paths.Length],
			colours: colours);
		AssertThat(colours[3] == colour).IsTrue();
		AssertThat(colours[2] is null).IsTrue();
	}
}
