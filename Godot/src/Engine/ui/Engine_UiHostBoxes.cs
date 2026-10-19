using System;

namespace Refii.Ui;

/// <summary>
/// Questions and notices the HOST raises in the game's own boxes — the query box and the message
/// box the guest's boxes are drawn with, owned by <see cref="Engine_UiQuerySurface"/> and
/// <see cref="Engine_UiMessageSurface"/> — for a flow that is ours rather than the guest's, such
/// as unlearning a mod's ability. The answer runs host code; nothing is posted to the guest.
/// </summary>
/// <remarks>
/// One of each at a time: a host question raised while another is up replaces it, as a second
/// press of the same key would.
/// </remarks>
public static class Engine_UiHostBoxes
{
	/// <summary>The question up, or null.</summary>
	public static string? Question { get; private set; }

	/// <summary>The notice up, or null.</summary>
	public static string? Notice { get; private set; }

	private static Action? _onYes;

	private static Action? _onNo;

	/// <summary>Who asked the question up, so its asker can take it back; empty for none.</summary>
	private static string _key = string.Empty;

	/// <summary>
	/// Asks <paramref name="body"/> as a yes/no question; <paramref name="onYes"/> runs on A and
	/// <paramref name="onNo"/>, when given, on B. A <paramref name="key"/> lets the asker take the
	/// question back with <see cref="Withdraw"/> when it is answered somewhere else.
	/// </summary>
	public static void Ask(string body, Action onYes, Action? onNo = null, string key = "")
	{
		Question = body;
		_onYes = onYes;
		_onNo = onNo;
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
		_onYes = null;
		_onNo = null;
		_key = string.Empty;
		Godot.GD.Print($"[host-box] withdrawn: {key}");
	}

	/// <summary>Shows <paramref name="body"/> until it is dismissed.</summary>
	public static void Tell(string body)
	{
		Notice = body;
		Godot.GD.Print($"[host-box] told: {body}");
	}

	/// <summary>The question's answer: runs the matching action, or simply closes it.</summary>
	public static void Answer(bool yes)
	{
		Action? onYes = _onYes;
		Action? onNo = _onNo;
		Question = null;
		_onYes = null;
		_onNo = null;
		_key = string.Empty;
		(yes ? onYes : onNo)?.Invoke();
	}

	public static void Dismiss() => Notice = null;
}
