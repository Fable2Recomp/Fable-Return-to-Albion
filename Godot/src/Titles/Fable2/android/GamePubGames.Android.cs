using Godot;

using Refii.Titles.Fable2.PubGames.Framework;

namespace Refii.Titles.Fable2;

using Refii.Game;

/// <summary>The pub games on a phone: the touch controls' pad and the letterbox beside the stage.</summary>
public partial class GamePubGames
{
	/// <summary>
	/// The touch controls' hold (<see cref="GameInput.Touch"/>) as one more device on the game's
	/// pad: the same XInput bits and units, so a tap on the glass is the pad's own press. A button
	/// pressed once by the host's own keys (<see cref="GameInput.PressedOnce"/>: the back gesture,
	/// the app's return) is pressed here too, or the board would never hear it.
	/// </summary>
	public static PubGameInput.Sample TouchPad()
	{
		GameInput.TouchHold touch = GameInput.Touch;
		return new PubGameInput.Sample((int)touch.LeftTrigger, (int)touch.RightTrigger,
			touch.LeftX, touch.LeftY, 0, 0, (int)(touch.Buttons | GameInput.PressedOnce));
	}

	/// <summary>
	/// Black at the sides of a phone's screen past the board's stage. A phone is wider than the
	/// console's 16:9, so the stage, fitted to the height, leaves strips where the loading screen
	/// behind it shows. Drawn under the board, so whatever the board bleeds past its stage stays.
	/// </summary>
	private sealed partial class Letterbox : Control
	{
		public override void _Ready()
		{
			MouseFilter = MouseFilterEnum.Ignore;
			SetAnchorsPreset(LayoutPreset.FullRect);
		}

		public override void _Process(double delta)
		{
			Visible = Showing.Length != 0;
			if (Visible)
			{
				QueueRedraw();
			}
		}

		public override void _Draw()
		{
			Rect2 stage = Refii.Titles.Fable2.PubGames.Framework.PubGameStageFit.Placed;
			Vector2 screen = GetViewportRect().Size;
			if (stage.Size.X <= 0f)
			{
				return;
			}

			if (stage.Position.X > 0f)
			{
				DrawRect(new Rect2(0f, 0f, stage.Position.X, screen.Y), Colors.Black);
			}

			if (stage.End.X < screen.X)
			{
				DrawRect(new Rect2(stage.End.X, 0f, screen.X - stage.End.X, screen.Y), Colors.Black);
			}
		}
	}
}
