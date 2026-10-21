using System;
using System.Collections.Generic;

namespace Refii.Ui;

/// <summary>
/// THE NOTICE CHANNEL: short messages shown one after another in the HUD's info box in game
/// (<c>InfoBoxEx</c> under a host name, which the TopBox shows while the guest's own box is down),
/// and in the front end's dialog out of game. The co-op notices and a mod's <c>notice</c>
/// (<c>RefiiHost.notice</c>, <c>Refii.notice</c>, <c>refii_mod_notice</c>) are both posted here, so
/// they queue behind each other and look the same.
/// </summary>
/// <remarks>
/// A notice is DONE when it has been shown and gone (answered 0: its time ran out in game, or it
/// was dismissed on the front end), or -1 when it never showed: the queue was cleared (a level
/// change) or its player is played on the other machine. Pure .NET; GameUi shows each.
/// </remarks>
public static class Engine_UiNotices
{
	/// <summary>The info box name a mod's notice stands under in game.</summary>
	public const string ModBoxName = "RefiiModNotice";

	/// <summary>How long a notice stands when it states no length.</summary>
	public const float DefaultSeconds = 5f;

	/// <summary>The most notices waiting; a flood drops the oldest waiting, answered -1.</summary>
	public const int MaxQueued = 16;

	/// <summary>One notice: who posted it (an id, 0 for the game's own), whose it is, its text and its length.</summary>
	public sealed record Notice(uint Id, int Player, string Body, float Seconds, string BoxName, Action<int>? Done);

	private static readonly Queue<Notice> _queue = new();

	/// <summary>The notice on screen, or null.</summary>
	public static Notice? Current { get; private set; }

	public static int Queued => _queue.Count;

	/// <summary>Queues a notice: <paramref name="title"/> above <paramref name="text"/>, for <paramref name="seconds"/> (0: the default).</summary>
	public static Notice Post(
		uint id, int player, string title, string text, float seconds, string boxName = ModBoxName, Action<int>? done = null)
	{
		var notice = new Notice(id, player, Engine_UiModRequest.Body(title, text),
			seconds > 0f ? seconds : DefaultSeconds, boxName, done);
		while (_queue.Count >= MaxQueued)
		{
			_queue.Dequeue().Done?.Invoke(-1);
		}

		_queue.Enqueue(notice);
		return notice;
	}

	/// <summary>The next notice to show, now on screen; null while one stands or none waits.</summary>
	public static Notice? TakeNext()
	{
		if (Current is not null || _queue.Count == 0)
		{
			return null;
		}

		Current = _queue.Dequeue();
		return Current;
	}

	/// <summary>The notice on screen has gone: <paramref name="outcome"/> 0 when it was shown, -1 when it was not.</summary>
	public static void Finish(int outcome)
	{
		Notice? done = Current;
		Current = null;
		done?.Done?.Invoke(outcome);
	}

	/// <summary>Everything waiting and standing goes unshown (-1): a level change, the UI going away.</summary>
	public static void Clear()
	{
		Finish(-1);
		while (_queue.Count != 0)
		{
			_queue.Dequeue().Done?.Invoke(-1);
		}
	}
}
