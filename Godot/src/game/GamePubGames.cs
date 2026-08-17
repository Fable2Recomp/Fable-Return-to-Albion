using Godot;

using Refii.FlashGames.Framework;
using Refii.FlashGames.Keystone;
using Refii.FlashGames.MageDice;
using Refii.Host;

namespace Refii.Game;

/// <summary>
/// Plays the guest's pub games, and hands the guest back its own settlement.
/// </summary>
/// <remarks>
/// THE GUEST OWNS THE LIFECYCLE; this only draws and plays. It decides a pub game is starting,
/// which one, which table variation and which pad; it seeds the game's bank from the hero's real
/// gold; and afterwards it credits the winnings, updates the statistics, applies the purity
/// balance, gossips about it, awards the achievement and raises <c>FinishedPubGame</c>. None of
/// that is reimplemented here — see the block comment above <c>RefiiHostGetPubGame</c> in
/// <c>host_bridge.cpp</c> for the addresses it was read from.
///
/// STATE 2 IS THE SEAM, and the binary is what says so. The guest's tick brings a start request
/// from state 1 to 2 having already done every snapshot we would otherwise have to reproduce, and
/// states 2 through 8 are the Scaleform module — creating a GFx movie, running it, handing it
/// <c>EndGame</c>, tearing it down. That is exactly the part Godot replaces. State 9 settles and
/// resets while dereferencing NEITHER the module pointer nor the game pointer, so a host may run
/// 1 → play → 9 and the guest settles correctly with Scaleform never created at all.
///
/// A GAME WE CANNOT PLAY MUST STILL BE ANSWERED. Keystone and Mage Dice have C# gameplay;
/// Wizard's Tower ships art and rules we have not built. The guest waits at state 2 for as long as
/// it takes, so declining silently is a soft lock in front of a black screen. Declining means
/// handing back the balance UNCHANGED, which settles a delta of zero and puts the player back in
/// the world having lost nothing.
///
/// THE DISPATCH IS ON THE GUEST'S OWN NAME for the game, which is the string it holds at
/// <c>mgr+8</c> and matches the game-data folder: <c>keystone</c>, <c>magedice</c>,
/// <c>wizardstower</c>. Anything else — including a name we cannot read — falls through to the
/// decline, which is the safe direction to be wrong in.
/// </remarks>
public partial class GamePubGames : Node
{
	/// <summary>
	/// ABOVE the loading screen at 8, below video at 10.
	/// </summary>
	/// <remarks>
	/// THE LOADING SCREEN NEVER COMES DOWN WHILE THE GAME IS PLAYING, so a board underneath it is
	/// a board nobody sees. This was at 6 — under the screen — on the reasoning that the guest
	/// raises <c>loading_keystone.tex</c> as it starts a pub game and the table should not appear
	/// behind a fade meant to hide it. The binary refutes the second half: <c>sub_821987B8</c>
	/// raises the screen in case 1 (<c>sub_82361200(..., 12)</c> for keystone) and clears it in
	/// <c>case 9</c> — the SETTLE — via <c>sub_82361458</c>. Nothing touches it in between, and
	/// states 3 and 4 are the whole session.
	///
	/// That is not an oversight in the guest. In the shipped title the Scaleform movie draws over
	/// the loading screen, so the screen is the pub game's backdrop rather than a curtain in front
	/// of it, and it is already there to cover the world while the table is up. Godot's board takes
	/// the movie's place, so it takes the movie's layer.
	/// </remarks>
	public const int LayerIndex = 9;

	private CanvasLayer? _layer;

	/// <summary>The board on screen, as a node to add and free.</summary>
	private Node? _host;

	/// <summary>The same object, as the two numbers the guest is waiting for.</summary>
	private IPubGameSession? _session;

	/// <summary>What the last poll reported, so a per-frame check does not flood the log.</summary>
	private string _reported = string.Empty;

	/// <summary>
	/// The state last logged, so the trace prints on change rather than every frame.
	/// </summary>
	/// <remarks>
	/// STARTS AT A VALUE THE GUEST CANNOT HOLD, so the FIRST poll always prints. Starting at 0
	/// would mean an idle session — which is nearly all of them — logged nothing at all, and a run
	/// with no pub-game line would be ambiguous between "the player never sat at a table" and "the
	/// bridge never read anything". The first line is the heartbeat that says the read works.
	/// </remarks>
	private uint _lastStage = uint.MaxValue;

	public override void _Ready()
	{
		_layer = new CanvasLayer { Name = "PubGameLayer", Layer = LayerIndex };
		AddChild(_layer);
	}

	/// <summary>True while a pub game is on screen, for a caller that wants to know.</summary>
	public bool IsShowing => _host is not null;

	/// <summary>
	/// One frame of the watcher. Call from the owner's <c>_Process</c>.
	/// </summary>
	/// <remarks>
	/// POLLED, because the guest publishes a state rather than calling us. The whole interface is
	/// one dword it writes under its own critical section, and reading it once a frame is both the
	/// cheapest and the only honest way to follow a value that nothing signals.
	/// </remarks>
	public void Poll()
	{
		if (HostBridge.GetPubGame() is not { } pub)
		{
			// The guest is not up, or the bridge is older than this build. Either way anything on
			// screen is orphaned — there is nobody left to hand a result to.
			Abandon("the guest is gone");
			return;
		}

		// EVERY TRANSITION, not just the one we act on. The player has to walk into a pub and sit
		// at a table for any of this to fire, so a run that produces nothing is ambiguous between
		// "they never sat down" and "the seam is wrong". A state that moves 0 -> 1 -> 2 and stops
		// says which.
		if (pub.Stage != _lastStage)
		{
			_lastStage = pub.Stage;
			GD.Print(
				$"[pubgame] guest state {pub.Stage}" +
				(pub.Game.Length == 0 ? string.Empty : $" '{pub.Game}'") +
				(pub.Variation.Length == 0 ? string.Empty : $" variation '{pub.Variation}'") +
				$", profile 0x{pub.Profile:X8}, gold {pub.Gold:N0}, debt {pub.Debt:N0}");
		}

		if (_host is not null)
		{
			Running(pub);
			return;
		}

		if (pub.WantsHost)
		{
			Start(pub);
		}
	}

	private void Running(HostBridge.PubGameState pub)
	{
		// THE GUEST DRIVES ITS OWN STATES, and most of them mean "still playing" — see
		// PubGameState.IsLive. It leaves 2 for 3 under its own steam on the very next tick, so a
		// host that treats anything but 2 as abandonment walks away from every session it starts.
		//
		// What genuinely ends a session is 0 (gone), 6 (tearing down) or 9 (settling), and by then
		// there is nothing left to hand back — 6 and 9 are the states OUR settlement writes.
		if (!pub.IsLive)
		{
			Abandon($"the guest moved to state {pub.Stage}");
			return;
		}

		if (_session is null || !_session.IsFinished)
		{
			return;
		}

		// THE BALANCE, NOT THE WINNINGS. The guest seeded the bank from the hero's real gold and
		// settles by crediting the difference; handing it a delta would pay the player twice.
		long gold = _session.FinalGold;
		long debt = _session.FinalDebt;

		Report(
			$"[pubgame] finished with {gold:N0} gold and {debt:N0} debt " +
			$"(started {pub.Gold:N0}/{pub.Debt:N0}); handing the guest its settlement");

		// WRITTEN BEFORE THE BOARD GOES AWAY, so a failure to reach the guest is reported while
		// the numbers are still recoverable rather than after they have been dropped.
		if (!HostBridge.SetPubGameResult(gold, debt))
		{
			GD.PushWarning("[pubgame] the guest would not take the result; the session is lost");
		}

		Teardown();
	}

	private void Start(HostBridge.PubGameState pub)
	{
		if (_layer is null)
		{
			return;
		}

		// SEEDED FROM THE GUEST, which is the point of entering at state 2 rather than launching
		// this ourselves — the bank it hands over is the hero's real gold, already snapshotted.
		//
		// EVERYTHING ELSE IS THE VARIATION'S. Buy-in, seat count and skin are stated per table in
		// settings.xml, and a host's exports are a harness convenience that must not speak over the
		// file — see the Unset remarks on either host. Leaving them at their old defaults played
		// every Keystone table in the game at a 500 buy-in with three opponents.
		//
		// SAID, NOT SUBSTITUTED. The variation is the table — its stakes, its odds and whether it
		// is a tournament — so if the guest names one we take it verbatim. If it names none there
		// is nothing to follow, and the host's own default table is better than a game with no
		// buy-in; but that is a guess, and a guess about the player's money gets logged.
		Node? host = pub.Game switch
		{
			"keystone" => new KeystoneHost
			{
				Name = "PubGame",
				StartingGold = Clamp(pub.Gold),
				AiPlayers = KeystoneHost.Unset,
				Skin = KeystoneHost.Unset,
				BuyIn = KeystoneHost.Unset,

				// A PLAYER IS NOT A HARNESS. The status label reports the frame rate, the raw
				// stick axes and the internal phase name.
				ShowDiagnostics = false,
				Variant = Table(pub, "Standard1"),
			},

			"magedice" => new MageDiceHost
			{
				Name = "PubGame",
				StartingGold = Clamp(pub.Gold),
				AiPlayers = MageDiceHost.Unset,
				Skin = MageDiceHost.Unset,
				BuyIn = MageDiceHost.Unset,
				ShowDiagnostics = false,
				Variant = Table(pub, "CowAndCorset1"),
			},

			_ => null,
		};

		if (host is not IPubGameSession session)
		{
			// DECLINED, AND ANSWERED. Leaving the guest at state 2 would hang it forever, and
			// wizardstower still has no gameplay. Handing back the balance UNCHANGED settles a
			// delta of zero and puts the player back in the world having lost nothing.
			Report(
				$"[pubgame] the guest asked for '{pub.Game}', which has no gameplay yet — " +
				"returning the player's balance unchanged");
			host?.QueueFree();
			HostBridge.SetPubGameResult(pub.Gold, pub.Debt);
			return;
		}

		Report(
			$"[pubgame] the guest started '{pub.Game}' variation '{pub.Variation}' " +
			$"with {pub.Gold:N0} gold and {pub.Debt:N0} debt");

		_host = host;
		_session = session;
		_layer.AddChild(host);
	}

	/// <summary>
	/// The variation the guest named, or the game's own default with a warning.
	/// </summary>
	/// <remarks>
	/// THE FALLBACK IS PER GAME because the tables are: Keystone's variations are Standard1 through
	/// the tournament tables, Mage Dice's are named after the pubs they sit in. There is no shared
	/// name that means "the cheapest table".
	/// </remarks>
	private static string Table(HostBridge.PubGameState pub, string fallback)
	{
		if (pub.Variation.Length != 0)
		{
			return pub.Variation;
		}

		GD.PushWarning($"[pubgame] the guest named no variation; playing '{fallback}'");
		return fallback;
	}

	/// <summary>
	/// Drops the board without writing anything back.
	/// </summary>
	/// <remarks>
	/// Deliberately silent about money. This runs when there is no longer a guest to settle with,
	/// so a write would either go nowhere or land on a manager that has already been reset — and
	/// the second of those would credit gold into whatever session came next.
	/// </remarks>
	private void Abandon(string why)
	{
		if (_host is null)
		{
			return;
		}

		Report($"[pubgame] abandoning the board: {why}");
		Teardown();
	}

	private void Teardown()
	{
		if (_host is null)
		{
			return;
		}

		_host.QueueFree();
		_host = null;
		_session = null;
	}

	/// <summary>
	/// The profile's gold is an <c>int64</c> and the pub game's is an <c>int</c>.
	/// </summary>
	/// <remarks>
	/// Clamped rather than cast. The guest's own money component saturates at 999,999,999, so a
	/// balance beyond an int is not reachable through play — but an unclamped cast of a corrupt
	/// read would wrap to a negative bank and the settlement would take that difference off the
	/// player.
	/// </remarks>
	private static int Clamp(long gold) =>
		(int)System.Math.Clamp(gold, 0, int.MaxValue);

	/// <summary>Prints a line only when it changes, so a per-frame poll does not flood.</summary>
	private void Report(string line)
	{
		if (line == _reported)
		{
			return;
		}

		_reported = line;
		GD.Print(line);
	}
}
