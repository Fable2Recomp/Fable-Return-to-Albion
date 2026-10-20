namespace Refii.Tests;

using GdUnit4;
using Godot;
using static GdUnit4.Assertions;
using Refii.Ui;

[TestSuite]
public class CoopHudLayoutTests
{
	private static Engine_HudLayout.Piece Piece(string id, string art, float x, float y, float ox) =>
		new(id, art, new Engine_HudLayout.Anchor(x, y), new Vector2(ox, 0f));

	[TestCase]
	public void TestPlayerOnePiecesAreKeyedToPlayerTwosSources()
	{
		var layout = new Engine_HudLayout()
			.Add(Piece("group.PlayerHealth.PlayerHealth", "PlayerHealth", 0f, 0f, 69f))
			.Add(Piece("group.CoopHealth.CoopHealth", "CoopHealth", 1f, 0f, -29f))
			.Add(Piece("group.SpellCharging.SpellCharging", "SpellCharging", 0f, 0.5f, 110f))
			.Add(Piece("group.SpellCharging2.SpellCharging2", "SpellCharging2", 1f, 0.5f, -110f));

		Engine_HudLayout.Piece? health = layout.PlayerTwoSourceOf(layout.Pieces[0]);
		AssertThat(health.HasValue).IsTrue();
		AssertThat(health!.Value.Id).IsEqual("group.CoopHealth.CoopHealth");

		Engine_HudLayout.Piece? will = layout.PlayerTwoSourceOf(layout.Pieces[2]);
		AssertThat(will.HasValue).IsTrue();
		AssertThat(will!.Value.Id).IsEqual("group.SpellCharging2.SpellCharging2");

		AssertThat(layout.PlayerTwoSourceOf(layout.Pieces[1]).HasValue).IsFalse();
		AssertThat(Engine_HudLayout.PlayerOneIdOf("group.CoopHealth.CoopHealth")).IsEqual("group.PlayerHealth.PlayerHealth");
	}

	[TestCase]
	public void TestSharedAndWheelPieces()
	{
		AssertThat(Engine_HudLayout.SharedOnSplit(Piece("group.QuestBox.TopBox", "TopBox", 0f, 0f, 0f))).IsTrue();
		AssertThat(Engine_HudLayout.SharedOnSplit(Piece("text.Subtitles.Text", "Text", 0f, 0f, 0f))).IsTrue();
		AssertThat(Engine_HudLayout.SharedOnSplit(Piece("meter.SubtitlesY.dpad", "dpad", 0f, 0f, 0f))).IsFalse();
		AssertThat(Engine_HudLayout.IsExpressionWheel(Piece("group.ExpressionWheel.wheelmenu", "wheelmenu", 0f, 0f, 0f))).IsTrue();
	}
}
