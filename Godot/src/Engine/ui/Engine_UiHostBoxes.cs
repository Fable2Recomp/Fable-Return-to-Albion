using System;

namespace Refii.Ui;

/// <summary>
/// Questions and notices the HOST raises in the game's own boxes — in game the query box and the
/// message box the guest's boxes are drawn with, owned by <see cref="Engine_UiQuerySurface"/> and
/// <see cref="Engine_UiMessageSurface"/>; out of game the front end's own dialog
/// (<see cref="Engine_FrontEndWarning"/>, stood by GameUi) — for a flow that is ours rather than the
/// guest's, such as unlearning a mod's ability or a mod's message box (<see cref="Engine_UiModBoxes"/>).
/// The answer runs host code; nothing is posted to the guest.
/// </summary>
/// <remarks>
/// One of each at a time: a host question raised while another is up replaces it, as a second
/// press of the same key would, and the replaced one is answered -1. An answer is the button's
/// index: 0 for A (the question's accept, the notice's dismiss), 1 for B.
/// </remarks>
public static class Engine_UiHostBoxes
{
	/// <summary>The question up, or null.</summary>
	public static string? Question { get; private set; }

	/// <summary>The question's A caption, or null for the box's authored one.</summary>
	public static string? AcceptCaption { get; private set; }

	/// <summary>The question's B caption, or null for the box's authored one.</summary>
	public static string? CancelCaption { get; private set; }

	/// <summary>The notice up, or null.</summary>
	public static string? Notice { get; private set; }

	private static Action<int>? _onQuestion;
	private static Action<int>? _onNotice;

	/// <summary>Who asked the question up, so its asker can take it back; empty for none.</summary>
	private static string _key = string.Empty;

	/// <summary>
	/// Asks <paramref name="body"/> as a yes/no question; <paramref name="onYes"/> runs on A and
	/// <paramref name="onNo"/>, when given, on B. A <paramref name="key"/> lets the asker take the
	/// question back with <see cref="Withdraw"/> when it is answered somewhere else.
	/// </summary>
	public static void Ask(string body, Action onYes, Action? onNo = null, string key = "") =>
		Ask(body, answer =>
		{
			if (answer == 0)
			{
				onYes();
			}
			else if (answer == 1)
			{
				onNo?.Invoke();
			}
		}, null, null, key);

	/// <summary>
	/// Asks <paramref name="body"/> with the two captions given (null keeps the box's own);
	/// <paramref name="answered"/> gets 0 for A, 1 for B, -1 when another question replaced it.
	/// </summary>
	public static void Ask(string body, Action<int>? answered, string? accept, string? cancel, string key = "")
	{
		Action<int>? replaced = _onQuestion;
		_onQuestion = null;
		replaced?.Invoke(-1);
		Question = body;
		AcceptCaption = accept;
		CancelCaption = cancel;
		_onQuestion = answered;
		_key = key;
		Godot.GD.Print($"[host-box] asked: {body}");
	}

	/// <summary>Whether the question up was asked under <paramref name="key"/>.</summary>
	public static bool Asking(string key) => Question is not null && key.Length != 0 && _key == key;

	/// <summary>Closes the question asked under <paramref name="key"/> without running either answer.</summary>
	public static void Withdraw(string key)
	{
		if (!Asking(key))
		{
			return;
		}

		Question = null;
		AcceptCaption = null;
		CancelCaption = null;
		_onQuestion = null;
		_key = string.Empty;
		Godot.GD.Print($"[host-box] withdrawn: {key}");
	}

	/// <summary>Shows <paramref name="body"/> until it is dismissed.</summary>
	public static void Tell(string body) => Tell(body, null);

	/// <summary>
	/// Shows <paramref name="body"/> until it is dismissed; <paramref name="dismissed"/> gets 0 on
	/// the dismiss, -1 when another notice replaced it.
	/// </summary>
	public static void Tell(string body, Action<int>? dismissed)
	{
		Action<int>? replaced = _onNotice;
		_onNotice = null;
		replaced?.Invoke(-1);
		Notice = body;
		_onNotice = dismissed;
		Godot.GD.Print($"[host-box] told: {body}");
	}

	/// <summary>The question's answer: A (<paramref name="yes"/>) or B. Closes it either way.</summary>
	public static void Answer(bool yes)
	{
		Action<int>? answered = _onQuestion;
		Question = null;
		AcceptCaption = null;
		CancelCaption = null;
		_onQuestion = null;
		_key = string.Empty;
		answered?.Invoke(yes ? 0 : 1);
	}

	public static void Dismiss()
	{
		Action<int>? dismissed = _onNotice;
		Notice = null;
		_onNotice = null;
		dismissed?.Invoke(0);
	}
}
