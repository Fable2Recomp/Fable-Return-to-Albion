using Godot;
using Refii.Ui;

namespace Refii.Titles.Fable2;

using Refii.Game;

/// <summary>
/// The notice channel's presenter (<see cref="Engine_UiNotices"/>): in game each notice stands in
/// the HUD's info box (an <c>InfoBoxEx</c> under its box name, one of
/// <see cref="RefiiNoticeBoxes"/>) for its length; out of game it is the front end's own dialog
/// until dismissed. One at a time, and never over another of Refii's notices.
/// </summary>
public partial class GameUi
{
	/// <summary>The notice up is on the front end's dialog, so its dismiss (not a timer) finishes it.</summary>
	private bool _noticeOnFrontEnd;

	private void DriveNotices(bool inGame)
	{
		if (Engine_UiNotices.Current is { } up)
		{
			if (_noticeOnFrontEnd)
			{
				return;
			}

			foreach (Engine_UiGuiBoxes.Box box in _boxes.Boxes)
			{
				if (box.Name == up.BoxName)
				{
					return;
				}
			}

			Engine_UiNotices.Finish(0);
		}

		if (inGame ? _boxes.FrontOf(RefiiNoticeBoxes) is not null : Engine_UiHostBoxes.Notice is not null)
		{
			return;
		}

		if (Engine_UiNotices.TakeNext() is not { } next)
		{
			return;
		}

		if (inGame)
		{
			_noticeOnFrontEnd = false;
			_boxes.InfoBoxEx(next.BoxName, next.Seconds, next.Body);
			return;
		}

		_noticeOnFrontEnd = true;
		Engine_UiHostBoxes.Tell(next.Body, outcome =>
		{
			_noticeOnFrontEnd = false;
			Engine_UiNotices.Finish(outcome < 0 ? -1 : 0);
		});
	}
}
