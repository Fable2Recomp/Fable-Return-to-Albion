namespace Refii.Tests.Titles;

using System.Collections.Generic;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Titles.Fable3;
using Refii.Ui;

/// <summary>
/// Fable 3's HUD drawn from stated calls: the objective list's rows from its redraw's own writer
/// calls (sub_82953E68) with the guest's own slide reports dropped, and the
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
	/// The objective list is drawn from its redraw's own calls (sub_82953E68): the list's slide
	/// count + 1 (sub_8281A428 at 0x82953EAC), each row's tag by name on `Bar&lt;i&gt;.Text`
	/// (sub_828191A8 via 0x82953F10) and its tick on done + 1 (0x82953F64), in the guest's order —
	/// so a completion's redraw, which runs inside the completion, draws the new tick. The guest's
	/// own `@Element.State` report has no route; the hide's slide 1 (0x8251D89C) lands.
	/// </summary>
	[TestCase]
	public void TheObjectiveListIsDrawnFromTheRedrawsOwnCalls()
	{
		Fresh();
		void Redraw(params (string Tag, bool Done)[] rows)
		{
			Fable3Elements.Take(Call("@Ui.State", S("40001000"), S("00000000"), S(Fable3UiStatements.ObjectiveListPath),
				N(rows.Length == 0 ? 1f : rows.Length + 1f), S("8281A428")));
			for (int i = 0; i < rows.Length; ++i)
			{
				Fable3Elements.Take(Call("@Ui.TextName", S("40001000"), S("00000000"), S(Fable3UiStatements.ObjectiveTextPath(i + 1)),
					S(rows[i].Tag), S("828191A8")));
				Fable3Elements.Take(Call("@Ui.State", S("40001000"), S("00000000"), S(Fable3UiStatements.ObjectiveTickPath(i + 1)),
					N(rows[i].Done ? 2f : 1f), S("8281A428")));
			}
		}

		Redraw(("QUEST_A", false), ("QUEST_B", true));
		AssertThat(Hud(Fable3UiStatements.ObjectiveListPath)!.State).IsEqual(3);
		AssertThat(Hud(Fable3UiStatements.ObjectiveTextPath(1))!.Stated!.Value.TagName).IsEqual("QUEST_A");
		AssertThat(Hud(Fable3UiStatements.ObjectiveTextPath(2))!.Stated!.Value.TagName).IsEqual("QUEST_B");
		AssertThat(Hud(Fable3UiStatements.ObjectiveTickPath(1))!.State).IsEqual(1);
		AssertThat(Hud(Fable3UiStatements.ObjectiveTickPath(2))!.State).IsEqual(2);

		// The guest's own slide report has no route.
		Fable3Elements.Take(Call("@Element.State", S("=hud"), S("47000010"), S(Fable3UiStatements.ObjectiveListPath), N(6f)));
		AssertThat(Hud(Fable3UiStatements.ObjectiveListPath)!.State).IsEqual(3);

		// SetSubObjectiveCompletion's redraw (0x82953E58) draws the new tick at once.
		Redraw(("QUEST_A", true), ("QUEST_B", true));
		AssertThat(Hud(Fable3UiStatements.ObjectiveTickPath(1))!.State).IsEqual(2);

		// GUI.HideSubObjectives, then a cleared list's redraw: slide 1.
		Fable3Elements.Take(Call("@Ui.State", S("40001000"), S("00000000"), S(Fable3UiStatements.ObjectiveListPath), N(1f), S("8281A428")));
		AssertThat(Hud(Fable3UiStatements.ObjectiveListPath)!.State).IsEqual(1);
		Redraw();
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
