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

	/// <summary>Asks <paramref name="body"/> as a yes/no question; <paramref name="onYes"/> runs on A.</summary>
	public static void Ask(string body, Action onYes)
	{
		Question = body;
		_onYes = onYes;
		Godot.GD.Print($"[host-box] asked: {body}");
	}

	/// <summary>Shows <paramref name="body"/> until it is dismissed.</summary>
	public static void Tell(string body)
	{
		Notice = body;
		Godot.GD.Print($"[host-box] told: {body}");
	}

	/// <summary>The question's answer: runs the yes action, or simply closes it.</summary>
	public static void Answer(bool yes)
	{
		Action? onYes = _onYes;
		Question = null;
		_onYes = null;
		if (yes)
		{
			onYes?.Invoke();
		}
	}

	public static void Dismiss() => Notice = null;
}
