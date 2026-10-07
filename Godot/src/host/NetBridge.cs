using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Refii.Host;

/// <summary>
/// RexNet as the shell sees it: who we are, who our friends are, and what is joinable.
/// </summary>
/// <remarks>
/// OVER THE LIVE NODE. The native side reads <c>RexNet::shared()</c> — the instance the guest
/// runtime already stands up — rather than the <c>rexnet_*</c> FFI, which would create a second
/// libp2p node with its own identity. See the RexNet section of <c>host_bridge.cpp</c>.
///
/// Every reader returns LF-separated records of TAB-separated fields. A malformed record is
/// SKIPPED rather than throwing: the fields are peer-supplied and this is a menu, so one bad
/// row must not take the screen down with it.
///
/// Nothing here is cached. The node's own snapshots are the model, and a menu that redrew from
/// a host-side copy would show a friend as online after they had gone.
/// </remarks>
public static class NetBridge
{
	private const string Library = "refii_hostbridge";

	/// <summary>Enough for a few hundred friends; a longer answer is truncated to nothing.</summary>
	private const int ReadBuffer = 64 * 1024;

	/// <summary>What <see cref="PeerAction"/> does, as the native switch numbers it.</summary>
	public enum Act
	{
		AcceptRequest = 0,
		DismissRequest = 1,
		RemoveFriend = 2,
		AcceptInvite = 3,
		DeclineInvite = 4,
		Invite = 5,

		/// <summary>
		/// Join this peer's advertised session, uninvited.
		/// </summary>
		/// <remarks>
		/// Raises the accept for ourselves off the peer's presence — the host peer and the
		/// 16-byte session id are the same material an invite carries — and starts the search
		/// whose result resolves it. The guest's own invite pump does the rest.
		/// </remarks>
		Join = 6,
	}

	/// <summary>Our own identity, or null when RexNet is off or not up yet.</summary>
	public readonly record struct Identity(string DisplayName, string PeerId, string FriendCode);

	public readonly record struct Friend(
		string PeerId,
		string DisplayName,
		bool Online,
		uint TitleId,
		string TitleName,
		bool HasSession,
		string SessionId,
		int PresenceState)
	{
		/// <summary>Joinable means they are hosting AND we can reach them.</summary>
		public bool Joinable => Online && HasSession;
	}

	public readonly record struct Request(string PeerId, string DisplayName, string Note);

	public readonly record struct Invite(string PeerId, uint TitleId, string SessionId);

	public readonly record struct Session(
		string HostPeerId, string SessionId, int SlotsTotal, int SlotsOpen, bool RequiresInvite);

	/// <summary>
	/// Whether we are hosting a session, and whether we will admit a joiner.
	/// </summary>
	/// <remarks>
	/// Hosting is the session machinery's to decide; only <see cref="SetJoinable"/> is ours.
	/// Null when the guest is not up — both bytes live in guest memory.
	/// </remarks>
	public static (bool Hosting, bool Joinable)? HostState()
	{
		foreach (string[] f in Records(RefiiNetHostState, 2))
		{
			return (f[0] == "1", f[1] == "1");
		}

		return null;
	}

	/// <summary>Opens or closes our session to joiners.</summary>
	public static bool SetJoinable(bool on)
	{
		try
		{
			return RefiiNetSetJoinable(on ? 1 : 0) != 0;
		}
		catch (DllNotFoundException)
		{
			return false;
		}
		catch (EntryPointNotFoundException)
		{
			return false;
		}
	}

	/// <summary>True when the node answered — the one test for "is RexNet available".</summary>
	public static bool Available => Self() is not null;

	public static Identity? Self()
	{
		foreach (string[] f in Records(RefiiNetIdentity, 3))
		{
			return new Identity(f[0], f[1], f[2]);
		}

		return null;
	}

	public static List<Friend> Friends()
	{
		var list = new List<Friend>();
		foreach (string[] f in Records(RefiiNetFriends, 8))
		{
			list.Add(new Friend(
				f[0], f[1], f[2] == "1", Num(f[3]), f[4], f[5] == "1", f[6], (int)Num(f[7])));
		}

		return list;
	}

	public static List<Request> Requests()
	{
		var list = new List<Request>();
		foreach (string[] f in Records(RefiiNetRequests, 3))
		{
			list.Add(new Request(f[0], f[1], f[2]));
		}

		return list;
	}

	/// <summary>Peers a request we sent reached (their ack), since the last call. Drained.</summary>
	public static List<string> Delivered()
	{
		var list = new List<string>();
		foreach (string[] f in Records(RefiiNetDelivered, 1))
		{
			list.Add(f[0]);
		}

		return list;
	}

	/// <summary>Peers that dismissed a request we sent, since the last call. Drained.</summary>
	public static List<string> Declined()
	{
		var list = new List<string>();
		foreach (string[] f in Records(RefiiNetDeclined, 1))
		{
			list.Add(f[0]);
		}

		return list;
	}

	public static List<Invite> Invites()
	{
		var list = new List<Invite>();
		foreach (string[] f in Records(RefiiNetInvites, 3))
		{
			list.Add(new Invite(f[0], Num(f[1]), f[2]));
		}

		return list;
	}

	/// <summary>
	/// Drains the accumulated session search.
	/// </summary>
	/// <remarks>
	/// DRAINING: the native call empties the queue, so the caller keeps what it reads and a
	/// second call returns nothing. <see cref="SearchSessions"/> starts a fresh sweep.
	/// </remarks>
	public static List<Session> TakeSessions()
	{
		var list = new List<Session>();
		foreach (string[] f in Records(RefiiNetSessionResults, 5))
		{
			list.Add(new Session(f[0], f[1], (int)Num(f[2]), (int)Num(f[3]), f[4] == "1"));
		}

		return list;
	}

	public static bool SearchSessions() => Call(RefiiNetSessionSearch);

	/// <summary>
	/// Who a <c>REXN-…</c> code names, without acting on it, or null when it is not a code.
	/// </summary>
	/// <remarks>
	/// A pure parse on the native side, so it answers with the node down. The name is the one
	/// the code's owner asserted when they generated it, and it may be empty.
	/// </remarks>
	public static (string Name, string PeerId)? PreviewCode(string code)
	{
		if (string.IsNullOrWhiteSpace(code))
		{
			return null;
		}

		var buffer = new byte[1024];
		uint written;
		try
		{
			written = RefiiNetPreviewCode(code, buffer, (uint)buffer.Length);
		}
		catch (DllNotFoundException)
		{
			return null;
		}
		catch (EntryPointNotFoundException)
		{
			return null;
		}

		if (written == 0 || written > buffer.Length)
		{
			return null;
		}

		string[] parts = Encoding.UTF8.GetString(buffer, 0, (int)written)
			.TrimEnd('\n').Split('\t');
		return parts.Length == 2 ? (parts[0], parts[1]) : null;
	}

	/// <summary>Sends a friend request to a <c>REXN-…</c> code.</summary>
	/// <summary>Outcome of <see cref="AddFriendByCode"/>: 0 failed, 1 sent, 2 that was our own code.</summary>
	public static int AddFriendByCodeResult(string code, string note)
	{
		try
		{
			return RefiiNetAddFriendByCode(code ?? string.Empty, note ?? string.Empty);
		}
		catch (DllNotFoundException)
		{
			return 0;
		}
		catch (EntryPointNotFoundException)
		{
			return 0;
		}
	}

	public static bool AddFriendByCode(string code, string note)
	{
		try
		{
			return RefiiNetAddFriendByCode(code ?? string.Empty, note ?? string.Empty) != 0;
		}
		catch (DllNotFoundException)
		{
			return false;
		}
		catch (EntryPointNotFoundException)
		{
			return false;
		}
	}

	/// <summary>Our session's id as 32 hex digits, or null when we are not hosting one.</summary>
	public static string? OwnSessionId()
	{
		var buffer = new byte[64];
		uint written;
		try
		{
			written = RefiiNetOwnSession(buffer, (uint)buffer.Length);
		}
		catch (DllNotFoundException)
		{
			return null;
		}
		catch (EntryPointNotFoundException)
		{
			return null;
		}

		return written == 0 || written > buffer.Length
			? null
			: Encoding.UTF8.GetString(buffer, 0, (int)written);
	}

	/// <summary>
	/// Joins a session from its material — the host's peer id, the title id and the session
	/// id as <see cref="OwnSessionId"/> gives it — without the host being a friend. What a
	/// Discord join secret carries.
	/// </summary>
	/// <summary>
	/// States what Discord shows for us; the node owns the Discord connection, the party and
	/// the join secret. <paramref name="joinable"/> null means "whenever a session exists".
	/// </summary>
	public static void DiscordActivity(string details, string state, bool? joinable)
	{
		try
		{
			RefiiNetDiscordActivity(details ?? string.Empty, state ?? string.Empty,
				joinable is null ? -1 : joinable.Value ? 1 : 0);
		}
		catch (DllNotFoundException)
		{
		}
		catch (EntryPointNotFoundException)
		{
		}
	}

	/// <summary>Rich Presence on or off, the player's setting.</summary>
	public static void DiscordEnabled(bool enabled)
	{
		try
		{
			RefiiNetDiscordEnabled(enabled ? 1 : 0);
		}
		catch (DllNotFoundException)
		{
		}
		catch (EntryPointNotFoundException)
		{
		}
	}

	public static bool JoinSession(string peerId, uint titleId, string sessionId)
	{
		try
		{
			return RefiiNetJoinSession(peerId ?? string.Empty, titleId, sessionId ?? string.Empty) != 0;
		}
		catch (DllNotFoundException)
		{
			return false;
		}
		catch (EntryPointNotFoundException)
		{
			return false;
		}
	}

	public static bool PeerAction(string peerId, Act act)
	{
		try
		{
			return RefiiNetPeerAction(peerId ?? string.Empty, (int)act) != 0;
		}
		catch (DllNotFoundException)
		{
			return false;
		}
		catch (EntryPointNotFoundException)
		{
			return false;
		}
	}

	private static uint Num(string text) => uint.TryParse(text, out uint value) ? value : 0u;

	private static bool Call(Func<int> entry)
	{
		try
		{
			return entry() != 0;
		}
		catch (DllNotFoundException)
		{
			return false;
		}
		catch (EntryPointNotFoundException)
		{
			return false;
		}
	}

	/// <summary>
	/// Splits one reader's answer into records of exactly <paramref name="fields"/> fields.
	/// </summary>
	/// <remarks>
	/// A record with the wrong count is dropped. The bridge is a different binary with its own
	/// build, so a field added on one side and not the other is a real state, and dropping the
	/// row keeps the menu standing while it is fixed.
	/// </remarks>
	private static IEnumerable<string[]> Records(Reader entry, int fields)
	{
		var buffer = new byte[ReadBuffer];
		uint written;
		try
		{
			written = entry(buffer, (uint)buffer.Length);
		}
		catch (DllNotFoundException)
		{
			yield break;
		}
		catch (EntryPointNotFoundException)
		{
			// An older bridge with no RexNet exports. Not a fault: the screen says so.
			yield break;
		}

		if (written == 0 || written > buffer.Length)
		{
			yield break;
		}

		string text = Encoding.UTF8.GetString(buffer, 0, (int)written);
		foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
		{
			string[] parts = line.Split('\t');
			if (parts.Length == fields)
			{
				yield return parts;
			}
		}
	}

	private delegate uint Reader(byte[] outText, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiNetIdentity(byte[] outText, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiNetFriends(byte[] outText, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiNetRequests(byte[] outText, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiNetDeclined(byte[] outText, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiNetDelivered(byte[] outText, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiNetInvites(byte[] outText, uint capacity);

	[DllImport(Library)]
	private static extern uint RefiiNetSessionResults(byte[] outText, uint capacity);

	[DllImport(Library)]
	private static extern int RefiiNetSessionSearch();

	[DllImport(Library)]
	private static extern uint RefiiNetHostState(byte[] outText, uint capacity);

	[DllImport(Library)]
	private static extern int RefiiNetSetJoinable(int on);

	[DllImport(Library, CharSet = CharSet.Ansi)]
	private static extern uint RefiiNetPreviewCode(string code, byte[] outText, uint capacity);

	[DllImport(Library, CharSet = CharSet.Ansi)]
	private static extern int RefiiNetAddFriendByCode(string code, string note);

	[DllImport(Library, CharSet = CharSet.Ansi)]
	private static extern int RefiiNetPeerAction(string peerId, int action);

	[DllImport(Library)]
	private static extern uint RefiiNetOwnSession(byte[] outText, uint capacity);

	[DllImport(Library, CharSet = CharSet.Ansi)]
	private static extern int RefiiNetJoinSession(string peerId, uint titleId, string sessionId);

	[DllImport(Library)]
	private static extern void RefiiNetDiscordActivity(string details, string state, int joinable);

	[DllImport(Library)]
	private static extern void RefiiNetDiscordEnabled(int enabled);
}
