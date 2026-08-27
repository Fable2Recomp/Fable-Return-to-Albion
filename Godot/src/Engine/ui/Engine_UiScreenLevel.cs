namespace Refii.Ui;

/// <summary>
/// Input cadence for a screen's second input level (the options category page). Nothing is
/// drawn: the level stays guest-mirrored; this only supplies the auto-repeat the guest's stick
/// translator would (the masked D-pad never reaches it). 36 commits the whole page
/// (`sub_829BF698`); nothing is written while scrolling. Each axis has its own clock.
/// </summary>
public sealed class Engine_UiScreenLevel
{
	private Engine_UiNavigationClock? _vertical;

	private Engine_UiNavigationClock? _horizontal;

	/// <summary>A clock on the page gate's repeat numbers, not the menu's.</summary>
	private static Engine_UiNavigationClock PageClock() => new()
	{
		FirstInterval = Engine_UiScreenInput.PageFirstRepeat,
		SubsequentInterval = Engine_UiScreenInput.PageRepeat,
	};

	public void Drive(Engine_UiSurfaceInput input)
	{
		_vertical ??= PageClock();
		_horizontal ??= PageClock();

		for (int step = _vertical.Steps(input.Direction, input.Delta);
			step > 0; --step)
		{
			Engine_UiSource.Current.ScreenAction(input.Direction < 0
				? Engine_UiScreenInput.Up
				: Engine_UiScreenInput.Down);
		}

		for (int step = _horizontal.Steps(input.Horizontal, input.Delta);
			step > 0; --step)
		{
			Engine_UiSource.Current.ScreenAction(input.Horizontal < 0
				? Engine_UiScreenInput.Left
				: Engine_UiScreenInput.Right);
		}

		if (input.Accept)
		{
			Engine_UiSource.Current.ScreenAction(Engine_UiScreenInput.Select);
		}
		else if (input.Back)
		{
			Engine_UiSource.Current.ScreenAction(Engine_UiScreenInput.Back);
		}
	}
}
