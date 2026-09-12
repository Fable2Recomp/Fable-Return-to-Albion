using System.Collections.Generic;
using Refii.Host;
using Refii.Mods;

namespace Refii.Ui;

/// <summary>
/// The rows behind the shell's own content screen: the mods that are on, and RexNet's
/// friends, requests, invites and joinable games.
/// </summary>
/// <remarks>
/// A DATA-AREA SOURCE, like <see cref="Engine_UiInventorySource"/>: nothing here watches the
/// guest build a menu, because the guest has no screen for any of it. The mod rows come from the
/// load order on disk and the network rows from <see cref="NetBridge"/>, which reads the live
/// node — so a row is current at the moment it is asked for and no state is kept here.
///
/// Every row is <c>Committable: false</c> and carries its own <see cref="Command"/>: these are
/// invented rows with no guest identity to commit against, and the action belongs to the row
/// rather than to a template entry because the peer or mod it names IS the argument.
///
/// A row is GHOSTED rather than dropped when it cannot be acted on — an offline friend, a mod
/// whose folder is missing. Dropping it would make the list change shape as people come and go,
/// which moves the cursor under the player's hand.
/// </remarks>
public static class Engine_UiNetworkSource
{
	public const string ModsSource = "mods";
	public const string FriendsSource = "friends";
	public const string RequestsSource = "requests";
	public const string InvitesSource = "invites";
	public const string GamesSource = "games";

	/// <summary>Every subject in one list, for the multiplayer book.</summary>
	public const string AllSource = "multiplayer";

	/// <summary>The hosting toggle, as its own source so its label tracks the state.</summary>
	public const string HostingSource = "hosting";

	/// <summary>The four subjects that get a page of their own, in list order.</summary>
	public static readonly (string Source, string Title)[] Pages =
	{
		(FriendsSource, "Friends"),
		(RequestsSource, "Friend Requests"),
		(InvitesSource, "Invitations"),
		(GamesSource, "Games"),
	};

	private const string FriendsGroup = "Friends";
	private const string RequestsGroup = "Friend Requests";
	private const string InvitesGroup = "Invitations";
	private const string GamesGroup = "Games";

	/// <summary>Is this the name of one of the book's group headers?</summary>
	public static bool IsGroupName(string name) =>
		name is FriendsGroup or RequestsGroup or InvitesGroup or GamesGroup;

	/// <summary>Command prefix a row dispatches with; the suffix is the verb and its argument.</summary>
	public const string CommandPrefix = "Net:";

	/// <summary>Where the player's mods live, as the launcher's panel writes them.</summary>
	public static string ModsRoot { get; set; } = string.Empty;

	/// <summary>
	/// How long a produced list stands before it is read again.
	/// </summary>
	/// <remarks>
	/// THE FRONT END RE-SOURCES EVERY FRAME. `RefreshSources` compares the finished set and does
	/// nothing when it has not moved, but it has to PRODUCE the set to compare it — and
	/// producing these means a TOML read off disk and a P/Invoke into the node. At 60 fps that
	/// is a file open per frame for a list that changes when a player alt-tabs to the launcher.
	/// A quarter second is under the eye's threshold for a friend coming online and is 15 reads
	/// a second rather than 60.
	/// </remarks>
	private const ulong FreshMs = 250;

	private static readonly Dictionary<string, (ulong At, IReadOnlyList<Engine_UiMenuSources.Row> Rows)>
		Cached = new();

	/// <summary>
	/// Produces a list at most once per <see cref="FreshMs"/>.
	/// </summary>
	/// <remarks>
	/// The SAME list instance is handed back within the window, which is what keeps
	/// `RefreshSources` seeing "unchanged" rather than an equal-but-new set every frame.
	/// </remarks>
	private static IReadOnlyList<Engine_UiMenuSources.Row> Fresh(
		string key, System.Func<IReadOnlyList<Engine_UiMenuSources.Row>> produce)
	{
		ulong now = Godot.Time.GetTicksMsec();
		if (Cached.TryGetValue(key, out var held) && now - held.At < FreshMs)
		{
			return held.Rows;
		}

		IReadOnlyList<Engine_UiMenuSources.Row> rows = produce();
		Cached[key] = (now, rows);
		return rows;
	}

	/// <summary>Drops every held list, so the next ask re-reads.</summary>
	public static void Invalidate() => Cached.Clear();

	private static Engine_UiMenuSources.Row Row(
		string name, string label, string command, bool ghosted = false) =>
		new(
			Name: name,
			Label: label,
			Icon: string.Empty,
			EventType: 0,
			Id: 0u,
			Rating: Engine_UiMenuConstants.EmptyRatingSlide,
			Committable: false,
			Ghosted: ghosted,
			Command: command);

	/// <summary>The enabled mods, in load order — the order that decides who shadows whom.</summary>
	public static IReadOnlyList<Engine_UiMenuSources.Row> Mods(string argument) =>
		Fresh("Mods", BuildMods);

	private static IReadOnlyList<Engine_UiMenuSources.Row> BuildMods()
	{
		var rows = new List<Engine_UiMenuSources.Row>();
		if (ModsRoot.Length == 0)
		{
			Placeholder(rows, "mods", "No mods folder found");
			return rows;
		}

		ModLoadOrder order;
		try
		{
			order = ModLoadOrder.Read(ModsRoot);
		}
		catch (System.Exception)
		{
			// The panel writes this file; a half-written one is not worth a dead screen.
			Placeholder(rows, "mods", "The mod load order could not be read");
			return rows;
		}

		int position = 1;
		foreach (string mod in order.Enabled)
		{
			// The number IS the contract: later wins, and a player reordering mods is choosing
			// which override survives.
			rows.Add(Row($"mod:{mod}", $"{position}. {mod}", $"{CommandPrefix}mod:{mod}"));
			++position;
		}


		Placeholder(rows, "mods", ModsRoot.Length == 0 ? "No mods folder found" : "No Mods");
		return rows;
	}

	public static IReadOnlyList<Engine_UiMenuSources.Row> Friends(string argument) =>
		Fresh("Friends", BuildFriends);

	private static IReadOnlyList<Engine_UiMenuSources.Row> BuildFriends()
	{
		var rows = new List<Engine_UiMenuSources.Row>();
		foreach (NetBridge.Friend friend in NetBridge.Friends())
		{
			string name = friend.DisplayName.Length == 0 ? Short(friend.PeerId) : friend.DisplayName;
			string state = friend.Joinable
				? (friend.TitleName.Length == 0 ? "in a game" : $"in {friend.TitleName}")
				: friend.Online ? "online" : "offline";

			rows.Add(Row(
				$"friend:{friend.PeerId}",
				$"{name} — {state}",
				$"{CommandPrefix}join:{friend.PeerId}",
				ghosted: !friend.Online));
		}


		Placeholder(rows, "friends", "No Friends");
		return rows;
	}

	public static IReadOnlyList<Engine_UiMenuSources.Row> Requests(string argument) =>
		Fresh("Requests", BuildRequests);

	private static IReadOnlyList<Engine_UiMenuSources.Row> BuildRequests()
	{
		var rows = new List<Engine_UiMenuSources.Row>();
		foreach (NetBridge.Request request in NetBridge.Requests())
		{
			string name = request.DisplayName.Length == 0
				? Short(request.PeerId)
				: request.DisplayName;
			string label = request.Note.Length == 0 ? name : $"{name} — \"{request.Note}\"";
			rows.Add(Row(
				$"request:{request.PeerId}", label, $"{CommandPrefix}accept:{request.PeerId}"));
		}


		Placeholder(rows, "requests", "No Requests");
		return rows;
	}

	public static IReadOnlyList<Engine_UiMenuSources.Row> Invites(string argument) =>
		Fresh("Invites", BuildInvites);

	private static IReadOnlyList<Engine_UiMenuSources.Row> BuildInvites()
	{
		var rows = new List<Engine_UiMenuSources.Row>();
		foreach (NetBridge.Invite invite in NetBridge.Invites())
		{
			rows.Add(Row(
				$"invite:{invite.PeerId}",
				$"{Short(invite.PeerId)} invited you",
				$"{CommandPrefix}accept-invite:{invite.PeerId}"));
		}


		Placeholder(rows, "invites", "No Invitations");
		return rows;
	}

	/// <summary>
	/// Friends who are hosting, plus whatever the last DHT sweep turned up.
	/// </summary>
	/// <remarks>
	/// FRIENDS FIRST AND WITHOUT A SEARCH. A friend's session arrives on their presence record,
	/// which the node already holds, so those rows are there the moment the screen opens;
	/// `Net:search` is only needed for public sessions from strangers.
	/// </remarks>
	public static IReadOnlyList<Engine_UiMenuSources.Row> Games(string argument) =>
		Fresh("Games", BuildGames);

	private static IReadOnlyList<Engine_UiMenuSources.Row> BuildGames()
	{
		var rows = new List<Engine_UiMenuSources.Row>();
		foreach (NetBridge.Friend friend in NetBridge.Friends())
		{
			if (!friend.Joinable)
			{
				continue;
			}

			string name = friend.DisplayName.Length == 0 ? Short(friend.PeerId) : friend.DisplayName;
			rows.Add(Row(
				$"game:{friend.PeerId}",
				$"{name}'s game",
				$"{CommandPrefix}join:{friend.PeerId}"));
		}

		foreach (NetBridge.Session session in Found)
		{
			rows.Add(Row(
				$"game:{session.HostPeerId}",
				$"{Short(session.HostPeerId)} — {session.SlotsOpen}/{session.SlotsTotal} open",
				$"{CommandPrefix}join:{session.HostPeerId}",
				ghosted: session.SlotsOpen == 0));
		}


		Placeholder(rows, "games", "No Games");
		return rows;
	}

	/// <summary>
	/// What the last search turned up.
	/// </summary>
	/// <remarks>
	/// HELD HERE because the native read DRAINS: `TakeSessionResults` empties the queue, so a
	/// list rebuilt straight from it would be full on one frame and empty on the next. Cleared
	/// when a new search starts.
	/// </remarks>
	private static readonly List<NetBridge.Session> Found = new();

	/// <summary>Starts a sweep and drops what the last one found.</summary>
	public static void BeginSearch()
	{
		Found.Clear();
		Invalidate();
		NetBridge.SearchSessions();
	}

	/// <summary>Drains any results that have landed. True when the list changed.</summary>
	public static bool PollSearch()
	{
		List<NetBridge.Session> landed = NetBridge.TakeSessions();
		if (landed.Count == 0)
		{
			return false;
		}

		Found.AddRange(landed);
		Invalidate();
		return true;
	}

	/// <summary>
	/// A ghosted stand-in when a list is empty.
	/// </summary>
	/// <remarks>
	/// An empty fold opens onto a single line and reads as a fold that did not open at all.
	/// NOT ghosted: a ghosted row cannot be activated (`Select` refuses one before anything is
	/// pushed), so the one row an empty section has would be the one row that does nothing. It
	/// carries `about:` instead, which explains the section and what fills it.
	/// </remarks>
	private static void Placeholder(List<Engine_UiMenuSources.Row> rows, string key, string text)
	{
		if (rows.Count == 0)
		{
			rows.Add(Row($"none:{key}", text, $"{CommandPrefix}about:{key}"));
		}
	}

	/// <summary>
	/// The hosting row, whose label IS the current state — a toggle the player reads rather
	/// than a command they have to remember the effect of.
	/// </summary>
	public static IReadOnlyList<Engine_UiMenuSources.Row> Hosting(string argument) =>
		Fresh("Hosting", BuildHosting);

	private static IReadOnlyList<Engine_UiMenuSources.Row> BuildHosting()
	{
		var rows = new List<Engine_UiMenuSources.Row>();
		if (NetBridge.HostState() is not { } state)
		{
			Placeholder(rows, "hosting", "Hosting is unavailable — RexNet is off");
			return rows;
		}

		// GHOSTED WHEN NOT HOSTING, because the byte is read only while a session is up: the
		// admit test checks hosting FIRST and refuses before it ever looks at joinable.
		rows.Add(Row(
			"host:joinable",
			state.Hosting
				? state.Joinable ? "Friends can join: ON" : "Friends can join: OFF"
				: "Friends can join — no game is hosted yet",
			$"{CommandPrefix}joinable:{(state.Joinable ? "0" : "1")}",
			ghosted: !state.Hosting));

		return rows;
	}

	/// <summary>
	/// The whole book's list: every subject, each row tagged with its group so
	/// <c>Engine_UiTemplates.Expanded</c> raises a header for it.
	/// </summary>
	public static IReadOnlyList<Engine_UiMenuSources.Row> All(string argument) =>
		Fresh("All", BuildAll);

	private static IReadOnlyList<Engine_UiMenuSources.Row> BuildAll()
	{
		var rows = new List<Engine_UiMenuSources.Row>();
		Group(rows, FriendsGroup, BuildFriends());
		Group(rows, RequestsGroup, BuildRequests());
		Group(rows, InvitesGroup, BuildInvites());
		Group(rows, GamesGroup, BuildGames());
		return rows;
	}

	private static void Group(
		List<Engine_UiMenuSources.Row> into, string group,
		IReadOnlyList<Engine_UiMenuSources.Row> rows)
	{
		foreach (Engine_UiMenuSources.Row row in rows)
		{
			into.Add(row with { Group = group, GroupTag = group });
		}
	}

	/// <summary>
	/// What the page says about one row.
	/// </summary>
	/// <remarks>
	/// Re-read from the node rather than carried on the row: a row is spawned once and the
	/// answer ("online", "in a game") changes underneath it. Keyed by the row NAME, which
	/// carries the peer id, so nothing has to be stored beside the list.
	/// </remarks>
	public static string Detail(string rowName)
	{
		if (IsGroupName(rowName))
		{
			return rowName switch
			{
				FriendsGroup => "Everyone who has added you, and what they are doing.",
				RequestsGroup => "People asking to be your friend. A accepts.",
				InvitesGroup => "Games you have been invited to. A joins.",
				_ => "Friends who are hosting, and any public games a search found.",
			};
		}

		int colon = rowName.IndexOf(':');
		string kind = colon < 0 ? rowName : rowName[..colon];
		string peer = colon < 0 ? string.Empty : rowName[(colon + 1)..];

		switch (kind)
		{
			case "friend":
			case "game":
				foreach (NetBridge.Friend friend in NetBridge.Friends())
				{
					if (friend.PeerId != peer)
					{
						continue;
					}

					string where = friend.TitleName.Length == 0 ? "a game" : friend.TitleName;
					return friend.Joinable
						? $"In {where}, and hosting.\n\nA asks them for an invitation."
						: friend.Online
							? "Online, not in a game.\n\nA asks them for an invitation."
							: "Offline.\n\nThey appear here again when they next play.";
				}

				return "This player is no longer listed.";

			case "request":
				foreach (NetBridge.Request request in NetBridge.Requests())
				{
					if (request.PeerId == peer)
					{
						return request.Note.Length == 0
							? "Wants to be your friend.\n\nA accepts."
							: $"\"{request.Note}\"\n\nA accepts.";
					}
				}

				return "This request has been withdrawn.";

			case "invite":
				return "You have been invited to this game.\n\nA joins it.";

			case "none":
				// THE PAGE AN EMPTY SECTION HAS: what fills it, written where the player is
				// looking. Never a box — a box draws under the book and blocks it.
				return peer switch
				{
					"mods" => "Mods are installed and ordered in the launcher's Mods panel.\n\n" +
						"Anything enabled there is listed here, in load order — later wins.",
					"friends" => NetBridge.Available
						? "No friends yet.\n\nGive someone your friend code from the row below, " +
							"and once they add you they appear here with what they are playing."
						: "RexNet is off, so there are no friends to list.\n\nTurn it on in the " +
							"launcher's RexNet settings.",
					"requests" => "Nobody has asked to be your friend.\n\nWhen someone adds you " +
						"by your friend code, their request appears here to accept or dismiss.",
					"invites" => "No invitations.\n\nWhen a friend invites you to their game it " +
						"appears here, and accepting is how you join.",
					"games" => NetBridge.Available
						? "No games found.\n\nFriends who are hosting appear here on their own; " +
							"choose Search again to look for public games."
						: "RexNet is off, so there are no games to find.\n\nTurn it on in the " +
							"launcher's RexNet settings.",
					_ => "Nothing here yet.",
				};

			default:
				return string.Empty;
		}
	}

	/// <summary>How long the page holds an outcome before going back to the offer.</summary>
	private const ulong CopiedShownMs = 4000;

	private static ulong _copiedAt;

	/// <summary>Notes that the code was just copied, so the page can say so.</summary>
	public static void MarkCopied() => _copiedAt = Godot.Time.GetTicksMsec();

	/// <summary>
	/// What the add-a-friend row's page is saying, and the code it is holding.
	/// </summary>
	/// <remarks>
	/// ON THE PAGE, NOT IN A BOX. Every outcome of a paste — an empty clipboard, something that
	/// is not a code, a name to confirm — belongs where the player is already looking. A box
	/// says the same words and costs a press to dismiss.
	///
	/// The pending code makes the confirm a SECOND PRESS on the same row rather than a modal:
	/// the page names who is about to be added, and A sends it.
	/// </remarks>
	private static string _addSays = string.Empty;

	private static ulong _addAt;

	private static string _addPending = string.Empty;

	/// <summary>The code awaiting a confirming press, or empty.</summary>
	public static string PendingAdd => _addPending;

	/// <summary>States what the add row's page says now, and what it is holding.</summary>
	public static void SetAddState(string says, string pending = "")
	{
		_addSays = says;
		_addPending = pending;
		_addAt = Godot.Time.GetTicksMsec();
	}

	/// <summary>What the page says about a row that is an action rather than a subject.</summary>
	public static string ActionDetail(string rowName) => rowName switch
	{
		"Hosting" or "host:joinable" =>
			"Whether people can join the game you are playing.\n\nA turns it on and off.",
		// A PENDING CONFIRM HOLDS THE PAGE until it is answered; a plain outcome fades back to
		// the offer so the row does not keep shouting about a paste from minutes ago.
		"AddFriend" => _addPending.Length != 0 || Godot.Time.GetTicksMsec() - _addAt < CopiedShownMs
			? _addSays
			: "Copy someone's friend code, then choose this.\n\nA reads your clipboard.",
		// THE CODE ITSELF, on the page. It exists to be handed to someone, so a page that
		// describes it without showing it is a page that does nothing; A copies it.
		"MyCode" => NetBridge.Self() is { FriendCode.Length: > 0 } self
			? $"{self.FriendCode}\n\n" + (Godot.Time.GetTicksMsec() - _copiedAt < CopiedShownMs
				? "Copied to your clipboard."
				: "A copies it to your clipboard.")
			: "RexNet is not up, so there is no friend code yet.\n\n" +
				"Turn it on in the launcher's RexNet settings.",
		_ => string.Empty,
	};

	/// <summary>
	/// What the page says about the row the player last acted on.
	/// </summary>
	/// <remarks>
	/// NEVER A BOX. The front end's warning box draws UNDER the multiplayer book and blocks it,
	/// so every outcome of a press — a join under way, a search started, a request accepted, a
	/// refusal — is written onto the row's own page, where the player is already looking. It is
	/// keyed to the row it answered (<see cref="CurrentRow"/> at the time of the press) so
	/// moving the cursor shows the next row's plain page; a plain outcome fades back after
	/// <see cref="SaidShownMs"/>, and a held one (a join in flight) stays until the cursor moves.
	/// </remarks>
	private const ulong SaidShownMs = 6000;

	private static string _saysRow = string.Empty;

	private static string _says = string.Empty;

	private static ulong _saysAt;

	private static bool _saysHeld;

	/// <summary>The row whose page is up now — the page states it every frame.</summary>
	public static string CurrentRow { get; set; } = string.Empty;

	/// <summary>Writes an outcome onto the current row's page.</summary>
	public static void Say(string text, bool hold = false)
	{
		_saysRow = CurrentRow;
		_says = text;
		_saysAt = Godot.Time.GetTicksMsec();
		_saysHeld = hold;
	}

	/// <summary>The outcome standing on this row's page, or empty.</summary>
	public static string Said(string rowName) =>
		_says.Length != 0 && rowName == _saysRow &&
		(_saysHeld || Godot.Time.GetTicksMsec() - _saysAt < SaidShownMs)
			? _says
			: string.Empty;

	/// <summary>The tail of a peer id — enough to tell two strangers apart in a row.</summary>
	private static string Short(string peerId) =>
		peerId.Length <= 8 ? peerId : "…" + peerId[^8..];
}
