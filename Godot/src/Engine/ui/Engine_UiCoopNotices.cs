using System.Collections.Generic;
using Refii.Host;

namespace Refii.Ui;

/// <summary>
/// Fable 3's co-op join and leave notices, adapted: what to tell the player when the co-op session
/// they are in changes, and the "press START" invitation while a second pad sits idle. Fed the
/// session transitions the shell already detects (<c>GameUi.DriveCoopHeroChannel</c>); it decides
/// the text and the order, the shell shows each one in the info box for <see cref="Seconds"/>.
/// </summary>
/// <remarks>
/// ONE AT A TIME. A join can raise two notices on one frame (the join and the progression note);
/// they queue and each gets its full five seconds, as Fable 3 shows them.
///
/// THE PEER'S NAME ARRIVES AFTER THE PEER. An online join is told only once the peer's name is
/// known (their hero record crosses the co-op channel a moment after the session stands), or
/// after <see cref="NameWaitSeconds"/> without one, under a generic name. A loss inside that wait
/// cancels the join notice.
///
/// Holds no Godot or bridge state: every input is an argument, so the rules are testable.
/// </remarks>
public sealed class Engine_UiCoopNotices
{
	/// <summary>The info box's identity — one box for every co-op notice.</summary>
	public const string BoxName = "RefiiCoopNotice";

	/// <summary>How long each notice stands, as Fable 3 shows them.</summary>
	public const float Seconds = 5f;

	/// <summary>How long an online join notice waits for the peer's name.</summary>
	public const double NameWaitSeconds = 10.0;

	public const string LocalJoined = "Player Two has joined the game.";

	public const string LocalLeft = "Player Two has left the game.";

	/// <summary>
	/// Told once per run on the first join of either kind. Refii gives each player their own
	/// progression (Fable 3's rule), which is why the co-op screen has no share sliders.
	/// </summary>
	public const string Progression = "Each Hero keeps their own gold and experience.";

	/// <summary>Fable 3's TEXT_TUTORIAL_COOP.</summary>
	public const string PressStart = "Press START on a second controller to join.";

	public const string HostLost = "Your connection with your co-op partner has been lost.";

	/// <summary>The joiner's guest saves on a lost connection, so the joiner is told so.</summary>
	public const string JoinerLost =
		"Your connection with your co-op partner has been lost. Your progress has been saved.";

	public static string HostJoined(string peer) =>
		$"{(peer.Length == 0 ? "Your co-op partner" : peer)} has joined your game.";

	public static string JoinerJoined(string host) =>
		host.Length == 0 ? "Successfully connected to your partner's game!"
			: $"Successfully connected to {host}'s game!";

	private readonly Queue<string> _queue = new();

	/// <summary>Seconds left on the notice standing now.</summary>
	private double _standing;

	/// <summary>Seconds an online join notice still waits for a name; negative when none waits.</summary>
	private double _nameWait = -1.0;

	/// <summary>The side the standing online session was joined on.</summary>
	private CoopSession.Role _joinedAs = CoopSession.Role.Solo;

	private bool _progressionTold;

	private bool _pressStartTold;

	private readonly HashSet<int> _padsSeen = new();

	/// <summary>The notice queued last.</summary>
	private string _last = string.Empty;

	/// <summary>Notices waiting to stand.</summary>
	public int Queued => _queue.Count;

	/// <summary>
	/// An online session transition. <paramref name="told"/> is false when the player is not in
	/// a game to be told (the front end), and <paramref name="refused"/> when this side refused
	/// the peer itself — that refusal is said on the multiplayer page, not as a lost connection.
	/// </summary>
	public void Session(
		CoopSession.Phase was, CoopSession.Phase now, CoopSession.Role side, bool told, bool refused)
	{
		if (was == now)
		{
			return;
		}

		// A join is told whenever the player is next in a game — a joiner from the title reaches
		// the session before their world has loaded (see Tick).
		if (now == CoopSession.Phase.PeerJoined)
		{
			_joinedAs = side;
			_nameWait = NameWaitSeconds;
			return;
		}

		if (was != CoopSession.Phase.PeerJoined)
		{
			return;
		}

		// The peer is gone. A join notice still waiting for a name is moot now.
		_nameWait = -1.0;
		CoopSession.Role joinedAs = _joinedAs;
		_joinedAs = CoopSession.Role.Solo;
		if (told && !refused)
		{
			Enqueue(joinedAs == CoopSession.Role.Client ? JoinerLost : HostLost);
		}
	}

	/// <summary>Local player two came or went (the guest's own `IsLocalCoopGame`).</summary>
	public void LocalCoop(bool was, bool now, bool told)
	{
		if (was == now || !told)
		{
			return;
		}

		if (now)
		{
			Enqueue(LocalJoined);
			TellProgression();
		}
		else
		{
			Enqueue(LocalLeft);
		}
	}

	/// <summary>
	/// The pads connected now, and whether a second player could join this moment (a game in
	/// progress with nobody else in it). Told once per run, and again whenever a pad that was not
	/// connected before appears.
	/// </summary>
	public void Pads(IReadOnlyCollection<int> connected, bool open)
	{
		bool fresh = false;
		foreach (int pad in connected)
		{
			fresh |= _padsSeen.Add(pad);
		}

		// A pad that went away is forgotten, so plugging it back in is a new connection.
		_padsSeen.IntersectWith(connected);
		if (fresh && connected.Count >= 2)
		{
			_pressStartTold = false;
		}

		if (!open || connected.Count < 2 || _pressStartTold)
		{
			return;
		}

		_pressStartTold = true;
		Enqueue(PressStart);
	}

	/// <summary>A notice of the shell's own, queued behind the session's.</summary>
	public void Tell(string text) => Enqueue(text);

	/// <summary>
	/// Advances the clocks and returns the notice to stand now, or null. <paramref name="peerName"/>
	/// is the online peer's display name as far as it is known, empty until it is.
	/// <paramref name="inGame"/> false holds a waiting join and drops the queue: the notices are
	/// about the game the player is in, and one held over the front end would be stale by the next.
	/// </summary>
	public string? Tick(double delta, string peerName, bool inGame)
	{
		if (!inGame)
		{
			_queue.Clear();
			_standing = 0.0;
			return null;
		}

		if (_nameWait >= 0.0)
		{
			_nameWait -= delta;
			if (peerName.Length != 0 || _nameWait < 0.0)
			{
				_nameWait = -1.0;
				Enqueue(_joinedAs == CoopSession.Role.Client
					? JoinerJoined(peerName)
					: HostJoined(peerName));
				TellProgression();
			}
		}

		_standing = System.Math.Max(0.0, _standing - delta);
		if (_standing > 0.0 || _queue.Count == 0)
		{
			return null;
		}

		_standing = Seconds;
		return _queue.Dequeue();
	}

	private void TellProgression()
	{
		if (_progressionTold)
		{
			return;
		}

		_progressionTold = true;
		Enqueue(Progression);
	}

	private void Enqueue(string text)
	{
		// The same notice twice in a row is one notice: a flapping pad or a session that drops
		// and returns inside a box's lifetime says it once.
		if (text == _last && _queue.Count != 0)
		{
			return;
		}

		_last = text;
		_queue.Enqueue(text);
	}
}
