using System;
using System.Collections.Generic;
using Godot;
using Refii.Host;

namespace Refii.Ui;

/// <summary>
/// A MOD'S MESSAGE BOXES, NOTICES, MENU BOXES AND MENUS, from every surface:
/// <c>RefiiHost.message_box</c> / <c>notice</c> / <c>menu_box</c> / <c>open_menu</c> in Godot, and
/// the native and Lua requests that cross the bus as <see cref="Engine_UiModRequest.Kind"/>
/// (<c>refii_mod_message_box</c>, <c>refii_mod_notice</c>, <c>refii_mod_menu_box</c>,
/// <c>refii_mod_open_menu</c> and their <c>Refii.*</c> twins). A message box is the host's
/// (<see cref="Engine_UiHostBoxes"/>): in game the game's own query or message box, on the front end
/// its own dialog. A notice joins the notice channel (<see cref="Engine_UiNotices"/>), the one the
/// co-op notices use. A menu box is the game's menu box art the Golden Cock's flow uses
/// (<see cref="Engine_UiModMenuBox"/>). Each answer raises its event on all three surfaces — the
/// Godot signal at once, the native and Lua handlers on the next guest tick: <c>ModMessageBox</c>
/// (<c>box_answer</c>), <c>ModNotice</c> (<c>notice_done</c>), <c>ModMenuBox</c> (<c>menu_box_answer</c>).
/// </summary>
/// <remarks>
/// <para>A title, text, caption or option may name a mod's text as <c>{KEY}</c>; it is drawn in the culture
/// in force (<see cref="Refii.Mods.ModText.Game"/>), a plain string as written.</para>
/// <para>PLAYER-AWARE: a request names player 1 or 2, or 0 for whoever owns the menus here. A machine
/// shows only the players it plays (<see cref="Engine_UiModRequest.PlayedHere"/>); the mod's code
/// runs on the other machine too and shows it there. A box for a player not played here is
/// answered -1 at once, so a script waiting on it does not wait for ever.</para>
/// </remarks>
public static class Engine_UiModBoxes
{
	/// <summary>Whether this machine plays <paramref name="player"/> (0: whoever owns the menus here).</summary>
	public static bool PlayedHere(int player) =>
		Engine_UiModRequest.PlayedHere(
			player, Refii.Core.Engine_TitleGame.Active.OnlineClient, Refii.Core.Engine_TitleGame.Active.PeerJoined);

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

			case Engine_UiModRequest.Op.Notice:
				Notice(request.Id, request.Player, request.Title, request.Text, request.Seconds, null);
				break;

			case Engine_UiModRequest.Op.MenuBox:
				MenuBox(request.Id, request.Player, request.Title, request.Text, request.Options ?? Array.Empty<string>(),
					request.Cancelable, null);
				break;

			// The HUD: markers, fades and meters are state the HUD's own layers read every frame.
			case Engine_UiModRequest.Op.Mark:
				Engine_HudModMarkers.Set(request.Id, request.Player, request.Entity,
					new Godot.Vector3(request.X, request.Y, request.Z), request.Icon, request.Text);
				if (request.HasMeter)
				{
					Engine_HudModMarkers.SetMeter(request.Id, request.Value, request.Max, request.Style, Engine_HudModMarkers.Clock);
				}

				break;

			case Engine_UiModRequest.Op.Unmark:
				Engine_HudModMarkers.Remove(request.Id);
				break;

			case Engine_UiModRequest.Op.MarkMeter:
				Engine_HudModMarkers.SetMeter(request.Id, request.Value, request.Max, request.Style, Engine_HudModMarkers.Clock);
				break;

			case Engine_UiModRequest.Op.Fade:
				if (PlayedHere(request.Player))
				{
					Engine_UiModFade.Request(request.Player, request.ToBlack, request.Seconds);
				}

				break;

			case Engine_UiModRequest.Op.Meter:
				Engine_HudModMeters.Set(request.Name, request.Player, request.Title, request.Value, request.Max, request.Style);
				break;

			case Engine_UiModRequest.Op.MeterRemove:
				Engine_HudModMeters.Remove(request.Name);
				break;

			// A mod's input claim, its presses and HUD overrides: state the pad publish and the HUD feed read.
			case Engine_UiModRequest.Op.InputClaim:
				if (!Refii.Mods.ModInput.Claim(request.Player, request.Name, request.On))
				{
					GD.PushWarning($"[mod-ui] input_claim: no action '{request.Name}' (input.json declares <mod>.<action>)");
				}

				break;

			case Engine_UiModRequest.Op.Press:
				if (!Refii.Mods.ModInputPresses.Press(request.Player, request.Name, request.Slide))
				{
					GD.PushWarning($"[mod-ui] press: no pad button '{request.Name}' for player {request.Player}");
				}

				break;

			case Engine_UiModRequest.Op.Tap:
				if (!Refii.Mods.ModInputPresses.Tap(request.Player, request.Name, request.Slide))
				{
					GD.PushWarning($"[mod-ui] tap: no pad button '{request.Name}' for player {request.Player}, or no ticks");
				}

				break;

			case Engine_UiModRequest.Op.HudIcon:
				Engine_UiHudOverrides.SetIcon(request.Player, request.Name, request.Icon);
				break;

			case Engine_UiModRequest.Op.HudSlide:
				Engine_UiHudOverrides.SetSlide(request.Player, request.Name, request.Slide);
				break;

			case Engine_UiModRequest.Op.HudHold:
				Engine_UiHudOverrides.SetHold(request.Player, request.Name, request.On);
				break;

			// Godot owns the skeleton a hidden part is a bone of, and the views (Engine_HiddenParts).
			case Engine_UiModRequest.Op.HidePart:
				Refii.Entities.Engine_HiddenParts.Set(request.Entity, request.Name, request.On, request.Player);
				break;
		}
	}

	/// <summary>
	/// Posts a mod's notice for <paramref name="player"/> (0: whoever owns the menus here) on the
	/// notice channel, for <paramref name="seconds"/> (0: the channel's length). <paramref name="done"/>
	/// gets 0 once it has shown and gone, -1 when it never showed (its player is played on the other
	/// machine, or the queue overflowed); the <c>ModNotice</c> event carries the same.
	/// </summary>
	public static bool Notice(uint id, int player, string title, string text, float seconds, Action<int>? done)
	{
		int shownTo = Resolve(player);
		void Done(int outcome)
		{
			done?.Invoke(outcome);
			Refii.Mods.ModGodot.Host?.RaiseModNotice(id, outcome, shownTo);
			if (HostBridge.IsGuestUp)
			{
				HostBridge.PostModCommand(Engine_UiModRequest.NoticeCommandLine(id, outcome, shownTo));
			}
		}

		if (!PlayedHere(player))
		{
			GD.Print($"[mod-ui] notice {id} is for player {player}, who is played on the other machine — done -1 here");
			Done(-1);
			return false;
		}

		Engine_UiNotices.Post(id, shownTo, Refii.Mods.ModText.Game(title), Refii.Mods.ModText.Game(text), seconds,
			Engine_UiNotices.ModBoxName, Done);
		return true;
	}

	/// <summary>
	/// Puts up a mod's menu box for <paramref name="player"/> (0: whoever owns the menus here):
	/// <paramref name="title"/> above <paramref name="text"/>, then <paramref name="options"/> (paged
	/// past the box's rows). <paramref name="answered"/> gets the chosen option's index, -1 for B on a
	/// <paramref name="cancelable"/> box, another box replacing it or a player played on the other
	/// machine. The answer raises <c>ModMenuBox</c> — or, for a <c>message_box</c> with more than two
	/// buttons (<paramref name="asMessageBox"/>), <c>ModMessageBox</c> with the button's index.
	/// </summary>
	public static bool MenuBox(
		uint id, int player, string title, string text, IReadOnlyList<string> options, bool cancelable,
		Action<int>? answered, bool asMessageBox = false)
	{
		int shownTo = Resolve(player);
		void Answered(int option)
		{
			answered?.Invoke(option);
			if (asMessageBox)
			{
				RaiseAnswered(id, option, shownTo);
				return;
			}

			Refii.Mods.ModGodot.Host?.RaiseModMenuBox(id, option, shownTo);
			if (HostBridge.IsGuestUp)
			{
				HostBridge.PostModCommand(Engine_UiModRequest.MenuBoxCommandLine(id, option, shownTo));
			}
		}

		if (options.Count == 0 || !PlayedHere(player))
		{
			GD.Print($"[mod-ui] menu box {id} " + (options.Count == 0
				? "has no options — answered -1"
				: $"is for player {player}, who is played on the other machine — answered -1 here"));
			Answered(-1);
			return false;
		}

		var shown = new List<string>(options.Count);
		foreach (string option in options)
		{
			shown.Add(Refii.Mods.ModText.Game(option));
		}

		Engine_UiModMenuBox.Open(id, shownTo, Engine_UiModRequest.Body(Refii.Mods.ModText.Game(title), Refii.Mods.ModText.Game(text)),
			shown, cancelable, Answered);
		GD.Print($"[mod-ui] menu box {id} up for player {shownTo}: {options.Count} option(s)");
		return true;
	}

	/// <summary>
	/// Raises a mod's box: two buttons for a <paramref name="question"/> (captions
	/// <paramref name="accept"/> and <paramref name="cancel"/>, null keeping the box's own), else one.
	/// <paramref name="answered"/> gets 0 for A, 1 for B, -1 when it was withdrawn or the
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

		string body = Engine_UiModRequest.Body(Refii.Mods.ModText.Game(title), Refii.Mods.ModText.Game(text));
		if (question)
		{
			Engine_UiHostBoxes.Ask(body, Answered,
				accept is null ? null : Refii.Mods.ModText.Game(accept), cancel is null ? null : Refii.Mods.ModText.Game(cancel));
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
