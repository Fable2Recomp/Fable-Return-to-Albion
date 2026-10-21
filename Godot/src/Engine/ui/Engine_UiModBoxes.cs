using System;
using Godot;
using Refii.Host;

namespace Refii.Ui;

/// <summary>
/// A MOD'S MESSAGE BOXES AND MENUS, from every surface: <c>RefiiHost.message_box</c> /
/// <c>open_menu</c> in Godot, and the native and Lua requests that cross the bus as
/// <see cref="Engine_UiModRequest.Kind"/> (<c>refii_mod_message_box</c>, <c>Refii.message_box</c>,
/// <c>refii_mod_open_menu</c>, <c>Refii.open_menu</c>). A box is the host's
/// (<see cref="Engine_UiHostBoxes"/>): in game the game's own query or message box, on the front end
/// its own dialog. Its answer raises the <c>ModMessageBox</c> event on all three surfaces — the
/// Godot signal at once, the native and Lua handlers on the next guest tick (<c>box_answer</c>).
/// </summary>
/// <remarks>
/// PLAYER-AWARE: a request names player 1 or 2, or 0 for whoever owns the menus here. A machine
/// shows only the players it plays (<see cref="Engine_UiModRequest.PlayedHere"/>); the mod's code
/// runs on the other machine too and shows it there. A box for a player not played here is
/// answered -1 at once, so a script waiting on it does not wait for ever.
/// </remarks>
public static class Engine_UiModBoxes
{
	/// <summary>Whether this machine plays <paramref name="player"/> (0: whoever owns the menus here).</summary>
	public static bool PlayedHere(int player) =>
		Engine_UiModRequest.PlayedHere(player, CoopSession.IsClient, CoopSession.PeerStanding);

	/// <summary>The player a request means: its own, or 0 as this machine's menu owner.</summary>
	public static int Resolve(int player) => player is 1 or 2 ? player : Engine_UiModMenus.MenuPlayer;

	/// <summary>One request off the bus (<see cref="Engine_UiModRequest.Kind"/>).</summary>
	public static void Handle(string fields)
	{
		if (Engine_UiModRequest.Parse(Refii.Mods.RefiiHost.ParseFieldsPlain(fields)) is not { } request)
		{
			GD.PushWarning($"[mod-ui] a UI request the host does not read: {fields}");
			return;
		}

		switch (request.Op)
		{
			case Engine_UiModRequest.Op.OpenMenu:
				Engine_UiModMenus.OpenFor(request.Name, request.Player);
				break;

			case Engine_UiModRequest.Op.MessageBox:
				Raise(request.Id, request.Player, request.Title, request.Text, request.Accept, request.Cancel,
					request.Question, null);
				break;
		}
	}

	/// <summary>
	/// Raises a mod's box: two buttons for a <paramref name="question"/> (captions
	/// <paramref name="accept"/> and <paramref name="cancel"/>, null keeping the box's own), else one.
	/// <paramref name="answered"/> gets 0 for A, 1 for B, -1 when another box replaced it or the
	/// player is not played here; the <c>ModMessageBox</c> event carries the same. False when the
	/// box was not raised here.
	/// </summary>
	public static bool Raise(
		uint id, int player, string title, string text, string? accept, string? cancel, bool question,
		Action<int>? answered)
	{
		int shownTo = Resolve(player);
		void Answered(int button)
		{
			answered?.Invoke(button);
			RaiseAnswered(id, button, shownTo);
		}

		if (!PlayedHere(player))
		{
			GD.Print($"[mod-ui] box {id} is for player {player}, who is played on the other machine — answered -1 here");
			Answered(-1);
			return false;
		}

		string body = Engine_UiModRequest.Body(title, text);
		if (question)
		{
			Engine_UiHostBoxes.Ask(body, Answered, accept, cancel);
		}
		else
		{
			Engine_UiHostBoxes.Tell(body, Answered);
		}

		return true;
	}

	/// <summary>The <c>ModMessageBox</c> event: the Godot signal now, native and Lua on the next guest tick.</summary>
	private static void RaiseAnswered(uint id, int button, int player)
	{
		Refii.Mods.ModGodot.Host?.RaiseModMessageBox(id, button, player);
		if (HostBridge.IsGuestUp)
		{
			HostBridge.PostModCommand(Engine_UiModRequest.AnswerCommandLine(id, button, player));
		}
	}
}
