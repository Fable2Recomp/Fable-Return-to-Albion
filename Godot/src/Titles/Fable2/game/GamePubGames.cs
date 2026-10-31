using System.Globalization;

using Godot;

using Refii.Titles.Fable2.PubGames.Framework;
using Refii.Titles.Fable2.PubGames.Keystone;
using Refii.Titles.Fable2.PubGames.MageDice;
using Refii.Titles.Fable2.PubGames.WizardsTower;
using Refii.Host;

namespace Refii.Titles.Fable2;

using Refii.Game;

/// <summary>
/// Plays the guest's pub games in place of its Scaleform movie.
/// </summary>
/// <remarks>
/// THE GUEST OWNS THE LIFECYCLE; this only draws and plays. It decides a pub game is starting,
/// which one, which table and which pad; it seeds the game's bank from the hero's real gold; and
/// afterwards it credits the winnings, updates the statistics, applies the purity balance,
/// gossips about it, awards the achievement and raises <c>FinishedPubGame</c>. None of that is
/// reimplemented here.
///
/// THE MOVIE IS THE ONLY THING REPLACED. <see cref="Fable2Bridge.ClaimPubGames"/> takes the GFx
/// module out of the guest's render-thread pump (<c>Fable2/src/vfs/src/hooks/pubgame_pump.cpp</c>); the
/// pump still walks the states and owns the sound emitter. Godot's game plays while the guest sits
/// in 3, and every fscommand it issues goes to the guest through <see cref="GuestLink"/> exactly
/// as the movie's would have — sounds, holdings (which save), points, rank, volumes, and GameOver,
/// which the guest answers by tearing down and settling. What the pump would have told the movie
/// comes back as <see cref="Fable2Bridge.PubGameEvent"/>.
///
/// A GAME WE CANNOT PLAY MUST STILL BE ANSWERED. The guest waits in 3 for as long as it takes, so
/// declining silently is a soft lock in front of a black screen. Declining means handing it
/// GameOver with the profile untouched, which settles a delta of zero.
/// </remarks>
public partial class GamePubGames : Node
{
	/// <summary>
	/// ABOVE the loading screen at 8, below video at 10.
	/// </summary>
	/// <remarks>
	/// The loading screen stays up for the whole session. <c>sub_821987B8</c> raises it in case 1
	/// (<c>sub_82361200(..., 12)</c> for keystone) and clears it only in case 9 via
	/// <c>sub_82361458</c>; the render thread draws it and then the movie over it every frame
	/// (<c>sub_82388380</c>: <c>sub_82361AF8</c> at 0x82388644, the pump at 0x82388654). So the
	/// screen is the backdrop the stage bleeds over, and what shows at the sides of a screen wider
	/// than the stage's 1555:1000 core.
	/// </remarks>
	public const int LayerIndex = 9;

	private CanvasLayer? _layer;

	/// <summary>The board on screen, as a node to add and free.</summary>
	private Node? _host;

	/// <summary>The same object, as the session the guest is waiting on.</summary>
	private IPubGameSession? _session;

	private readonly GuestLink _link = new();

	/// <summary>What the last poll reported, so a per-frame check does not flood the log.</summary>
	private string _reported = string.Empty;

	/// <summary>
	/// The state last logged. Starts at a value the guest cannot hold, so the first poll prints:
	/// that line is the heartbeat that says the read works.
	/// </summary>
	private uint _lastStage = uint.MaxValue;

	private bool _claimed;

	/// <summary>
	/// GameOver has gone to the guest for this session. The pump drains it on the guest's next
	/// render frame, so the state reads 3 for a moment longer, and without this that moment opens
	/// a second board. Cleared once the session leaves the live states.
	/// </summary>
	private bool _answered;

	/// <summary>The session being played, kept for a reload (tutorial → table) in place.</summary>
	private Fable2Bridge.PubGameState _pub;

	private PubGameSeed? _seed;

	private string _variation = string.Empty;

	public override void _Ready()
	{
		_layer = new CanvasLayer { Name = "PubGameLayer", Layer = LayerIndex };
		AddChild(_layer);
		// The phone's letterbox and the touch controls' pad: GamePubGames.Android.cs.
		if (Refii.Core.Engine_Platform.IsAndroid)
		{
			_layer.AddChild(new Letterbox { Name = "Letterbox" });
		}

		PubGameInput.Touch = TouchPad;
	}

	public override void _ExitTree()
	{
		PubGameInput.Touch = null;
		Showing = string.Empty;
		DialogUp = false;
		if (_claimed)
		{
			Fable2Bridge.ClaimPubGames(false);
			_claimed = false;
		}
	}

	/// <summary>True while a pub game is on screen, for a caller that wants to know.</summary>
	public bool IsShowing => _host is not null;

	/// <summary>
	/// The game on screen as the guest names it (<c>keystone</c>, <c>magedice</c>,
	/// <c>wizardstower</c>), empty while none is: the touch controls lay out its buttons.
	/// </summary>
	public static string Showing { get; private set; } = string.Empty;

	/// <summary>
	/// Whether one of the board's dialogs owns its pad (<c>OverlayManager.AbsorbingInput</c>: the
	/// pause menu, the bank, a quit or cash-out question), as of the last poll: the phone's back
	/// gesture backs out of one and otherwise pauses (AndroidSystemKeys).
	/// </summary>
	public static bool DialogUp { get; private set; }

	/// <summary>
	/// One frame of the watcher. Call from the owner's <c>_Process</c>.
	/// </summary>
	public void Poll()
	{
		if (Fable2Bridge.GetPubGame() is not { } pub)
		{
			_claimed = false;
			Abandon("the guest is gone");
			return;
		}

		// Claimed before any session can start, and again if the bridge ever lost it: an unclaimed
		// pump creates the real movie at state 2 and runs it invisibly on the same pad.
		if (!_claimed)
		{
			_claimed = Fable2Bridge.ClaimPubGames(true);
		}

		if (pub.Stage != _lastStage)
		{
			_lastStage = pub.Stage;
			GD.Print(
				$"[pubgame] guest state {pub.Stage}" +
				(pub.Game.Length == 0 ? string.Empty : $" '{pub.Game}'") +
				(pub.Variation.Length == 0 ? string.Empty : $" variation '{pub.Variation}'") +
				$", profile 0x{pub.Profile:X8}, gold {pub.Gold:N0}, debt {pub.Debt:N0}");
		}

		_link.Stage = pub.Stage;
		Fable2Bridge.PubGameEvent events = Fable2Bridge.TakePubGameEvents();

		if (_host is not null)
		{
			Running(pub, events);
			return;
		}

		if (!pub.IsLive)
		{
			_answered = false;
		}

		if (pub.WantsHost && !_answered)
		{
			Start(pub);
		}
	}

	private void Running(Fable2Bridge.PubGameState pub, Fable2Bridge.PubGameEvent events)
	{
		if (!pub.IsLive)
		{
			Abandon($"the guest moved to state {pub.Stage}");
			return;
		}

		PubGame? game = _session?.Game;
		DialogUp = (game?.OverlayManager as OverlayManager)?.AbsorbingInput == true;
		if ((events & (Fable2Bridge.PubGameEvent.SaveEnded | Fable2Bridge.PubGameEvent.SaveSkipped)) != 0)
		{
			_link.SaveEnded(game);
		}

		// The guest is ending it: a GameFlow command pushed over the session (0x82468C98 moves
		// 3 to 7). The movie gets `pubGame.EndGame` and answers with GameOver like any ending.
		if ((events & Fable2Bridge.PubGameEvent.EndGame) != 0)
		{
			Report($"[pubgame] the guest is ending '{pub.Game}'");
			game?.EndGame();
		}

		if (_session is { Failed: true })
		{
			Report($"[pubgame] '{pub.Game}' could not open its board — ending it with nothing spent");
			Fable2Bridge.PostPubGameCommand(Fable2Bridge.PubGameCommand.GameOver);
			_answered = true;
			Teardown();
			return;
		}

		if (_session is null)
		{
			return;
		}

		if (_session.ReloadRequested || _session.TutorialRequested)
		{
			// The guest reloads the movie in place (`sub_82EF2708`: game+1160 for the tutorial,
			// +1161 for the chosen table) without leaving the session.
			string table = _session.TutorialRequested ? "Tutorial" : _variation;
			Report($"[pubgame] reloading '{pub.Game}' as '{table}'");
			Teardown();
			Open(_pub, table);
			return;
		}

		if (_session.IsFinished)
		{
			Report($"[pubgame] '{pub.Game}' is over; the guest settles it");
			_answered = true;
			Teardown();
		}
	}

	private void Start(Fable2Bridge.PubGameState pub)
	{
		if (Fable2Bridge.GetPubGameProfile() is not { } profile)
		{
			Report($"[pubgame] '{pub.Game}' has no profile to seed from — ending it with nothing spent");
			Fable2Bridge.PostPubGameCommand(Fable2Bridge.PubGameCommand.GameOver);
			_answered = true;
			return;
		}

		Vector2 window = GetViewport().GetVisibleRect().Size;
		_pub = pub;
		_seed = new PubGameSeed(
			profile.Name,
			Clamp(pub.Gold),
			Clamp(pub.Debt),
			(int)profile.Rank,
			profile.TotalPoints,
			profile.Points,
			profile.Items,
			(int)(profile.SoundVolume * 10f),
			(int)(profile.MusicVolume * 10f),
			window.X >= window.Y * 1.5f,
			profile.Language.Length != 0 ? profile.Language : "en");
		_variation = Table(pub.Variation, profile.Rank);

		Report(
			$"[pubgame] the guest started '{pub.Game}' variation '{pub.Variation}' → table " +
			$"'{_variation}' for '{profile.Name}' (rank {profile.Rank}) with {pub.Gold:N0} gold " +
			$"and {pub.Debt:N0} debt");
		Open(pub, _variation);
	}

	private void Open(Fable2Bridge.PubGameState pub, string table)
	{
		if (_layer is null || _seed is null)
		{
			return;
		}

		// Everything else is the table's: buy-in, seat count and skin are stated per variation in
		// settings.xml; the hosts' exports are harness conveniences that must not speak over it.
		Node? host = pub.Game switch
		{
			"keystone" => new KeystoneHost
			{
				Name = "PubGame",
				Profile = _seed,
				Guest = _link,
				Language = _seed.Language,
				AiPlayers = KeystoneHost.Unset,
				Skin = KeystoneHost.Unset,
				BuyIn = KeystoneHost.Unset,
				ShowDiagnostics = false,
				Variant = table,
			},

			"magedice" => new MageDiceHost
			{
				Name = "PubGame",
				Profile = _seed,
				Guest = _link,
				Language = _seed.Language,
				AiPlayers = MageDiceHost.Unset,
				Skin = MageDiceHost.Unset,
				BuyIn = MageDiceHost.Unset,
				ShowDiagnostics = false,
				Variant = table,
			},

			"wizardstower" => new WizardsTowerHost
			{
				Name = "PubGame",
				Profile = _seed,
				Guest = _link,
				Language = _seed.Language,
				AiPlayers = WizardsTowerHost.Unset,
				Skin = WizardsTowerHost.Unset,
				BuyIn = WizardsTowerHost.Unset,
				ShowDiagnostics = false,
				Variant = table,
			},

			_ => null,
		};

		if (host is not IPubGameSession session)
		{
			Report($"[pubgame] the guest asked for '{pub.Game}', which has no gameplay — ending it with nothing spent");
			host?.QueueFree();
			Fable2Bridge.PostPubGameCommand(Fable2Bridge.PubGameCommand.GameOver);
			_answered = true;
			return;
		}

		_host = host;
		_session = session;
		Showing = pub.Game;
		_link.Game = () => _session?.Game;
		_layer.AddChild(host);
	}

	/// <summary>
	/// The table the guest would load: the village's variation, plus the player's rank.
	/// </summary>
	/// <remarks>
	/// A village states a family, not a table (<c>KSJackpot</c>, <c>SBCowAndCorset</c>,
	/// <c>FTDiamondDeck</c>), and no settings.xml holds a variation by that name. <c>sub_82EF20E0</c>
	/// (retail, <c>game+0x468</c>) appends the profile's rank with <c>L"%s%d"</c> when the name does
	/// not already end in a digit (0x82EF2170 <c>cmpwi 0x30</c>/<c>0x39</c>, rank from
	/// <c>sub_82EE86A0</c> = <c>ld 0x110</c>), so a rank-3 player at Bowerstone sits at KSJackpot3.
	/// </remarks>
	private static string Table(string variation, long rank)
	{
		if (variation.Length == 0 || char.IsAsciiDigit(variation[^1]))
		{
			return variation;
		}

		return variation + rank.ToString(CultureInfo.InvariantCulture);
	}

	/// <summary>
	/// Drops the board without telling the guest anything.
	/// </summary>
	/// <remarks>
	/// Runs when there is no longer a session to answer — the guest left 3-8 on its own, or is
	/// gone — so anything posted now would land on whichever session came next.
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
		_link.Reset();
		if (_host is null)
		{
			return;
		}

		_host.QueueFree();
		_host = null;
		_session = null;
		Showing = string.Empty;
		DialogUp = false;
	}

	/// <summary>
	/// The profile's gold is an <c>int64</c> and the pub game's is an <c>int</c>. Clamped rather
	/// than cast, so a corrupt read cannot wrap to a negative bank.
	/// </summary>
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

	/// <summary>
	/// The guest's side of <c>FSCommand</c>: what <c>sub_82EF2AC0</c> parses out of each command.
	/// </summary>
	/// <remarks>
	/// The argument strings are the movie's own (<c>"gold:G; debt:D"</c>, <c>"points:N"</c>,
	/// <c>"name?x=v"</c>) and are split the way the guest splits them — <c>strtok_s</c> on
	/// <c>";: "</c>, or <c>"? &amp;="</c> for a sound — so a value lands in the same slot it would
	/// have. Print, Assert, Profile, Running, TournamentBuyIn, TournamentCompleted and
	/// AchievementEarned are no-ops in the guest (null or <c>li r3, 0</c> handlers) and stay so.
	/// </remarks>
	private sealed class GuestLink : IPubGameHost
	{
		/// <summary>The guest's state as of the last poll.</summary>
		public uint Stage { get; set; }

		/// <summary>
		/// The game this link speaks for. Resolved on each command: <c>PubGame.Run</c> issues its
		/// first holdings from inside the host's load, before the host is ready.
		/// </summary>
		public System.Func<PubGame?> Game { get; set; } = () => null;

		/// <summary>A holdings update started a guest save the game has not heard the end of.</summary>
		private bool _saving;

		public void Reset() => _saving = false;

		public void Command(string command, string? argument)
		{
			string text = argument ?? string.Empty;
			switch (command)
			{
				case PubCommand.PlaySound:
				{
					string[] tokens = Split(text, "? &=");
					if (tokens.Length == 0)
					{
						return;
					}

					Fable2Bridge.PostPubGameCommand(
						Fable2Bridge.PubGameCommand.PlaySound, tokens[0],
						Number(tokens, 2), Number(tokens, 4), Number(tokens, 6));
					return;
				}

				case PubCommand.PauseSound:
					Fable2Bridge.PostPubGameCommand(
						Fable2Bridge.PubGameCommand.PauseSound, i0: Bool(text) ? 1 : 0);
					return;

				case PubCommand.UpdatePlayerHoldings:
				{
					string[] tokens = Split(text, ";: ");
					Fable2Bridge.PostPubGameCommand(
						Fable2Bridge.PubGameCommand.Holdings,
						i0: Integer(tokens, 1), i1: Integer(tokens, 3));

					// The guest answers synchronously. Its handler's slot 0 (`sub_824675E0`) calls
					// the movie's `pubGame.StartSave` from state 3 before the fscommand returns, and
					// `PubGame::GameOver` reads `saving` on the very next line to decide whether to
					// fade out now or wait for EndSave. So StartSave is said here, not on a later poll.
					if (!_saving && Stage == Fable2Bridge.PubGameState.RunningStage && Game() is { } game)
					{
						_saving = true;
						game.StartSave();
					}

					return;
				}

				case PubCommand.PointsEarned:
				{
					string[] tokens = Split(text, ";: ");
					Fable2Bridge.PostPubGameCommand(
						Fable2Bridge.PubGameCommand.Points,
						i0: (long)(float)Number(tokens, 1), i1: Game()?.GameIndex ?? 0);
					return;
				}

				case PubCommand.NewRank:
				{
					string[] tokens = Split(text, ";: ");
					Fable2Bridge.PostPubGameCommand(
						Fable2Bridge.PubGameCommand.Rank, i0: (int)(float)Number(tokens, 1));
					return;
				}

				case PubCommand.UpdateSoundVolume:
				case PubCommand.UpdateMusicVolume:
				{
					string[] tokens = Split(text, ";: ");
					Fable2Bridge.PostPubGameCommand(
						command == PubCommand.UpdateSoundVolume
							? Fable2Bridge.PubGameCommand.SoundVolume
							: Fable2Bridge.PubGameCommand.MusicVolume,
						n0: (float)Number(tokens, 1) / 10f);
					return;
				}

				case PubCommand.GameOver:
					Fable2Bridge.PostPubGameCommand(Fable2Bridge.PubGameCommand.GameOver);
					return;

				case PubCommand.Running:
					return;
			}
		}

		/// <summary>State 5, or a holdings update that started no save: <c>pubGame.EndSave</c>.</summary>
		public void SaveEnded(PubGame? game)
		{
			if (!_saving)
			{
				return;
			}

			_saving = false;
			game?.EndSave();
		}

		private static string[] Split(string text, string delimiters) =>
			text.Split(delimiters.ToCharArray(), System.StringSplitOptions.RemoveEmptyEntries);

		/// <summary><c>strtod</c> on one token, 0 when it is missing.</summary>
		private static double Number(string[] tokens, int index) =>
			index < tokens.Length && double.TryParse(
				tokens[index], NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
				? value
				: 0.0;

		/// <summary><c>strtol</c> base 10 on one token, as the int the guest then sign-extends.</summary>
		private static long Integer(string[] tokens, int index) =>
			index < tokens.Length && int.TryParse(
				tokens[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
				? value
				: 0;

		/// <summary><c>sub_82EF5540</c>: true on a leading t, y or 1.</summary>
		private static bool Bool(string text) =>
			text.Length != 0 && "tTyY1".IndexOf(text[0]) >= 0;
	}
}
