namespace Refii.Titles.Fable3;

using System;
using System.Globalization;
using System.Text;
using Refii.Host;
using Refii.Ui;

/// <summary>
/// The notice channel's presenter for Fable 3 (<see cref="Engine_UiNotices"/>: a mod's
/// <c>Refii.notice</c>, <c>refii_mod_notice</c> or <c>RefiiHost.notice</c>): each notice is the game's
/// own TopBox message, posted by the payload (<c>topbox_notice</c>, Fable3/src/hooks/src/
/// mod_lua_hooks.cpp) as a wide-text message the game takes without its text book, so it stands in
/// the TopBox the HUD draws, in Fable 3's look. One at a time, for its length; the TopBox's own
/// queue orders it among the game's messages. Out of game a notice waits for the game.
/// </summary>
public partial class Fable3Notices : Godot.Node
{
	private double _remaining;

	public override void _Process(double delta)
	{
		if (Engine_UiNotices.Current is not null)
		{
			_remaining -= delta;
			if (_remaining <= 0)
			{
				Engine_UiNotices.Finish(0);
			}

			return;
		}

		if (!HostBridge.IsInGame || Engine_UiNotices.TakeNext() is not { } next)
		{
			return;
		}

		if (!Engine_UiModBoxes.PlayedHere(next.Player))
		{
			Engine_UiNotices.Finish(-1);
			return;
		}

		string text = Convert.ToBase64String(Encoding.UTF8.GetBytes(next.Body));
		HostBridge.PostModCommand(string.Create(CultureInfo.InvariantCulture,
			$"topbox_notice {next.Seconds * 1000f:0} {text}"));
		_remaining = next.Seconds;
	}
}
