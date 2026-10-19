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
	public void TestPlayerTwoPiecesMirrorPlayerOnes()
	{
		var layout = new Engine_HudLayout()
			.Add(Piece("group.PlayerHealth.PlayerHealth", "PlayerHealth", 0f, 0f, 69f))
			.Add(Piece("group.CoopHealth.CoopHealth", "CoopHealth", 1f, 0f, -29f))
			.Add(Piece("group.SpellCharging.SpellCharging", "SpellCharging", 0f, 0.5f, 110f))
			.Add(Piece("group.SpellCharging2.SpellCharging2", "SpellCharging2", 1f, 0.5f, -110f));

		Engine_HudLayout.Piece? health = layout.CounterpartOf(layout.Pieces[1]);
		AssertThat(health.HasValue).IsTrue();
		AssertThat(health!.Value.Id).IsEqual("group.PlayerHealth.PlayerHealth");

		Engine_HudLayout.Piece? will = layout.CounterpartOf(layout.Pieces[3]);
		AssertThat(will.HasValue).IsTrue();
		AssertThat(will!.Value.Id).IsEqual("group.SpellCharging.SpellCharging");

		AssertThat(layout.CounterpartOf(layout.Pieces[0]).HasValue).IsFalse();
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
