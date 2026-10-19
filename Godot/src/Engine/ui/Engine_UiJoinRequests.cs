using System.Collections.Generic;
using Refii.Host;

namespace Refii.Ui;

/// <summary>
/// The Discord "ask to join" requests the node has handed over and nobody has answered yet — ONE
/// store, read by both places that answer them: the Multiplayer page's rows
/// (<see cref="Engine_UiNetworkSource"/>) and the in-world question (<c>GameUi</c>).
/// </summary>
/// <remarks>
/// <see cref="NetBridge.JoinRequests"/> DRAINS the node's queue, so whoever reads it first owns
/// what it returned. Both readers go through <see cref="Refresh"/>, which keeps every request
/// here until it is answered (<see cref="Answer"/>, from either place) or goes stale, so a request
/// read by one is never lost to the other and an answered one leaves both at once.
/// </remarks>
public static class Engine_UiJoinRequests
{
	/// <summary>How long an unanswered ask stays listed; Discord's own ask lapses sooner.</summary>
	public const ulong LifetimeMs = 60_000;

	private static readonly List<(NetBridge.JoinRequest Request, ulong SeenMs)> _pending = new();

	/// <summary>Arrivals not yet asked in the world, oldest first.</summary>
	private static readonly List<NetBridge.JoinRequest> _arrived = new();

	/// <summary>The display name an ask is shown under; Discord can hand over an empty one.</summary>
	public static string Who(NetBridge.JoinRequest ask) =>
		ask.UserName.Length == 0 ? "A Discord player" : ask.UserName;

	/// <summary>
	/// Takes what the node handed over since the last read and drops what went stale. A repeat
	/// ask from the same user replaces the earlier one and counts as a new arrival.
	/// </summary>
	public static void Refresh(ulong nowMs)
	{
		foreach (NetBridge.JoinRequest ask in NetBridge.JoinRequests())
		{
			_pending.RemoveAll(entry => entry.Request.UserId == ask.UserId);
			_pending.Add((ask, nowMs));
			_arrived.RemoveAll(entry => entry.UserId == ask.UserId);
			_arrived.Add(ask);
		}

		_pending.RemoveAll(entry => nowMs - entry.SeenMs > LifetimeMs);
		_arrived.RemoveAll(ask => !IsPending(ask.UserId));
	}

	/// <summary>Every unanswered ask, oldest first.</summary>
	public static IEnumerable<NetBridge.JoinRequest> Pending()
	{
		foreach ((NetBridge.JoinRequest ask, ulong _) in _pending)
		{
			yield return ask;
		}
	}

	public static bool IsPending(string userId) =>
		_pending.Exists(entry => entry.Request.UserId == userId);

	/// <summary>The oldest ask not yet raised in the world, taken; null when there is none.</summary>
	public static NetBridge.JoinRequest? TakeArrival()
	{
		if (_arrived.Count == 0)
		{
			return null;
		}

		NetBridge.JoinRequest ask = _arrived[0];
		_arrived.RemoveAt(0);
		return ask;
	}

	/// <summary>
	/// Answers one ask through the node and forgets it everywhere. False when the node refused —
	/// the ask may have lapsed on Discord's side; it is forgotten either way.
	/// </summary>
	public static bool Answer(string userId, bool accept)
	{
		_pending.RemoveAll(entry => entry.Request.UserId == userId);
		_arrived.RemoveAll(entry => entry.UserId == userId);
		return NetBridge.JoinRequestReply(userId, accept);
	}
}
